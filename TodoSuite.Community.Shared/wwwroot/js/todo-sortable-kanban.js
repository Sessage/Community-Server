// wwwroot/js/todo-sortable-kanban.js
// Requires SortableJS global "Sortable"
//
// Unterstützt:
//  - Karten Drag&Drop (inkl. Auto-Scroll + Highlight)
//  - Spalten Drag&Drop (Handle: .kanban-column-handle)
//  - Dispose für beide
//
// NEU (Fix für SortMode-Wechsel):
//  - initKanban akzeptiert optional 4. Parameter "sortMode" (z.B. "Custom", "Importance", ...)
//  - Karten-DnD wird nur bei sortMode === "Custom" aktiviert, sonst sauber deaktiviert.
//  - Beim Zurückschalten auf Custom ist Karten-DnD wieder aktiv.
//
// Erwartete DOM-Struktur:
//  - Board-Scroll-Container: id = boardScrollId
//  - Karten-Hosts: .kanban-sortable[data-col] + data-colkey
//  - Jede Spalten-Card (Column Container): .kanban-column[data-col] + data-colkey
//  - Spalten-Host: id = columnsHostId (enthält .kanban-column Elemente)
//  - Drag-Handle für Spalten: .kanban-column-handle innerhalb .kanban-column
//
// Blazor Callbacks (DotNet):
//  - SetDragging(bool)
//  - OnKanbanDropped(string taskId, string fromCol, string toCol, string[] fromIds, string[] toIds)
//  - OnKanbanColumnsReordered(string[] orderedColumns)

window.todoUi = window.todoUi || {};

(function () {
    // ── Globaler Pointer-Tracker (einmalig, geteilt zwischen list- und kanban-Modul) ──
    // Zuverlässiger als evt.originalEvent.buttons, das in SortableJS-Callbacks
    // unter bestimmten Browser-/WebView2-Bedingungen null/0 sein kann, obwohl der
    // Nutzer die Taste noch gedrückt hält.
    if (!window._todoUiPointerTracker) {
        const _pts = new Set();
        let _mouseDown = false;
        window._todoUiPointerTracker = { isDown: () => _pts.size > 0 || _mouseDown };
        document.addEventListener('pointerdown',   e => _pts.add(e.pointerId),    { capture: true, passive: true });
        document.addEventListener('pointerup',     e => _pts.delete(e.pointerId), { capture: true, passive: true });
        document.addEventListener('pointercancel', e => _pts.delete(e.pointerId), { capture: true, passive: true });
        document.addEventListener('mousedown',     () => { _mouseDown = true; },  { capture: true, passive: true });
        document.addEventListener('mouseup',       () => { _mouseDown = false; }, { capture: true, passive: true });
        window.addEventListener('blur',            () => { _mouseDown = false; }, { passive: true });
    }

    let _dotNet = null;
    let _boardEl = null;

    let _columnsHostEl = null;
    let _columnsSortable = null;

    const _cardSortables = [];

    // Modulweiter Drag-State: wird in onStart/finishDragAsync synchronisiert.
    // Ermöglicht dem visibilitychange- und watchdog-Handler den Drag-State zu prüfen.
    let _isDragging = false;
    let _dragToken = 0;
    let _dragStartedAt = 0;       // performance.now() Zeitstempel des letzten Drag-Starts
    let _dragWatchdogTimer = null; // ID des laufenden setTimeout
    let _pendingResetTimer = null;
    let _pendingInitArgs = null;
    let _pendingDispose = false;
    let _preventNativeScrollForDrag = false;

    // Watchdog deaktiviert:
    // Nutzer halten Karten teils länger bewusst "in der Luft". Ein fixer Timeout
    // würde dann fälschlich den aktiven Drag abbrechen (Karte "verschwindet").
    // Hängende Drags werden stattdessen über onEnd/onCancel/onUnchoose sowie
    // visibilitychange / dispose robust bereinigt.
    const ENABLE_DRAG_WATCHDOG = false;
    const DRAG_TIMEOUT_MS = 8_000;

    function preventNativeScrollWhileDragging(ev) {
        if (!_isDragging && !_preventNativeScrollForDrag) return;

        const pointerType = ev?.pointerType;
        if (pointerType && pointerType !== "touch") return;
        if (ev?.cancelable === false) return;

        ev.preventDefault();
    }

    document.addEventListener("touchmove", preventNativeScrollWhileDragging, { capture: true, passive: false });
    document.addEventListener("pointermove", preventNativeScrollWhileDragging, { capture: true, passive: false });

    function lockNativeScrollForDrag() {
        _preventNativeScrollForDrag = true;
    }

    function unlockNativeScrollForDrag() {
        _preventNativeScrollForDrag = false;
    }

    function scheduleDragStateCleanup(delayMs = 0) {
        if (!_isDragging && !_preventNativeScrollForDrag) return;

        setTimeout(function () {
            const pointerStillDown = window._todoUiPointerTracker?.isDown();
            if (_isDragging && !hasActiveDragVisuals() && !pointerStillDown) {
                forceResetDragState();
            } else if (!_isDragging && _preventNativeScrollForDrag && !hasActiveDragVisuals() && !pointerStillDown) {
                unlockNativeScrollForDrag();
            }
        }, delayMs);
    }

    // merkt sich den aktuellen Mode (Default Custom)
    let _sortMode = "Custom";

    function isCustomMode() {
        return (String(_sortMode || "").trim().toLowerCase() === "custom");
    }

    // ✅ Liest NUR die Karten-Reihenfolge aus dem Host (DOM ist Quelle der Wahrheit).
    // Wichtig: querySelectorAll ist stabil, aber wir filtern explizit auf direkte Card-Elemente,
    // um "verschachtelte data-taskid" oder Wrapper auszuschließen.
    function getIds(hostEl) {
        if (!hostEl) return [];

        return Array.from(hostEl.children)
            .filter(el => el && el.nodeType === 1 && el.hasAttribute("data-taskid"))
            .map(el => el.getAttribute("data-taskid"))
            .filter(Boolean);
    }

    function moveIdToIndex(ids, taskId, targetIndex) {
        if (!taskId || !Array.isArray(ids)) return ids || [];

        const next = ids.filter(id => id !== taskId);
        const index = Math.max(0, Math.min(targetIndex, next.length));
        next.splice(index, 0, taskId);
        return next;
    }

    function getBodyFallbackForTask(taskId) {
        if (!taskId) return null;

        return Array.from(document.body?.children || []).find(el =>
            el?.getAttribute?.("data-taskid") === taskId
            && (el.classList.contains("sortable-fallback")
                || el.classList.contains("sortable-drag")
                || el.classList.contains("sortable-ghost")));
    }

    function getEventClientY(originalEvent) {
        const ev = originalEvent?.changedTouches?.[0]
            || originalEvent?.touches?.[0]
            || originalEvent;

        return typeof ev?.clientY === "number" ? ev.clientY : null;
    }

    function normalizeDropOrderForTopEdge(hostEl, movedEl, originalEvent) {
        const ids = getIds(hostEl);
        const movedId = (movedEl?.getAttribute("data-taskid") || "").trim();
        if (!hostEl || !movedEl || !movedId || ids.length < 2 || ids[0] === movedId) {
            return ids;
        }

        const firstOther = Array.from(hostEl.children).find(el =>
            el && el.nodeType === 1
            && el.hasAttribute("data-taskid")
            && el.getAttribute("data-taskid") !== movedId);

        if (!firstOther) return ids;

        const firstRect = firstOther.getBoundingClientRect();
        const fallbackRect = getBodyFallbackForTask(movedId)?.getBoundingClientRect();
        const itemRect = movedEl.getBoundingClientRect();
        const pointerY = getEventClientY(originalEvent);

        // WKWebView + forceFallback: when a tall card is dragged above the first
        // card, Sortable can still leave the real item at index 1 because it uses
        // pointer/center based thresholds. The user-facing visual is the fallback
        // clone, so trust its top edge for the top-of-column drop decision.
        const visualTop = fallbackRect?.top ?? itemRect.top;
        const firstUpperBand = firstRect.top + Math.min(28, firstRect.height * 0.35);
        const pointerIsAboveFirstCenter = pointerY !== null && pointerY <= firstRect.top + (firstRect.height / 2);

        if (visualTop <= firstUpperBand || pointerIsAboveFirstCenter) {
            return moveIdToIndex(ids, movedId, 0);
        }

        return ids;
    }

    function shouldInsertBeforeFirst(hostEl, movedEl, relatedEl, originalEvent) {
        if (!hostEl || !movedEl || !relatedEl) return false;

        const movedId = (movedEl.getAttribute("data-taskid") || "").trim();
        const firstOther = Array.from(hostEl.children).find(el =>
            el && el.nodeType === 1
            && el.hasAttribute("data-taskid")
            && el.getAttribute("data-taskid") !== movedId);

        if (!firstOther || firstOther !== relatedEl) return false;

        const firstRect = firstOther.getBoundingClientRect();
        const fallbackRect = getBodyFallbackForTask(movedId)?.getBoundingClientRect();
        const pointerY = getEventClientY(originalEvent);
        const visualTop = fallbackRect?.top;

        return (typeof visualTop === "number" && visualTop <= firstRect.top + Math.min(28, firstRect.height * 0.35))
            || (pointerY !== null && pointerY <= firstRect.top + (firstRect.height / 2));
    }

    function getColName(hostEl) {
        return (hostEl?.getAttribute("data-col") || "").trim();
    }

    function highlightColumn(hostEl, on) {
        if (!hostEl) return;
        // hostEl ist .kanban-sortable
        const colCard = hostEl.closest(".kanban-column");
        if (!colCard) return;

        if (on) colCard.classList.add("kanban-drop-target");
        else colCard.classList.remove("kanban-drop-target");
    }

    function autoScrollX(container, clientX) {
        if (!container) return;
        const rect = container.getBoundingClientRect();
        const edge = 70;
        const speed = 18;

        if (clientX < rect.left + edge) container.scrollLeft -= speed;
        else if (clientX > rect.right - edge) container.scrollLeft += speed;
    }

    function autoScrollY(container, clientY) {
        if (!container) return;
        const rect = container.getBoundingClientRect();
        const edge = 70;
        const speed = 18;

        if (clientY < rect.top + edge) container.scrollTop -= speed;
        else if (clientY > rect.bottom - edge) container.scrollTop += speed;
    }

    function safeInvoke(method, ...args) {
        if (!_dotNet) return Promise.resolve();
        try {
            return _dotNet.invokeMethodAsync(method, ...args)
                .catch(e => console.error("[kanban] DotNet invoke failed:", method, e));
        } catch (e) {
            console.error("[kanban] DotNet invoke sync failed:", method, e);
            return Promise.resolve();
        }
    }

    function setDraggingWithToken(value, token) {
        return safeInvoke("SetDraggingWithToken", value, token)
            .catch(() => safeInvoke("SetDragging", value));
    }

    function isPointerStillDown(evt) {
        // Primär: globaler Tracker ist unabhängig von SortableJS-internen Events,
        // die evt.originalEvent manchmal ohne gültige buttons-Property liefern.
        if (window._todoUiPointerTracker?.isDown()) return true;
        // Fallback: SortableJS originalEvent
        const buttons = evt?.originalEvent?.buttons;
        return typeof buttons === "number" && buttons !== 0;
    }

    function hasActiveDragVisuals() {
        // Während eines laufenden Fallback-Drags existiert i.d.R. ein Body-Klon
        // oder mindestens ein Element mit Sortable-Dragklassen.
        if (document.querySelector('body > .sortable-drag, body > .sortable-fallback, body > .sortable-ghost, body > [data-taskid].sortable-drag, body > [data-taskid].sortable-fallback, body > [data-taskid].sortable-ghost')) {
            return true;
        }
        return !!document.querySelector('.kanban-sortable .sortable-chosen, .kanban-sortable .sortable-drag, .kanban-sortable .sortable-fallback, .kanban-sortable [data-taskid].sortable-chosen, .kanban-sortable [data-taskid].sortable-drag, .kanban-sortable [data-taskid].sortable-fallback');
    }

    // ── Drag-Watchdog ──────────────────────────────────────────────────────────
    // Wenn ein Drag nach DRAG_TIMEOUT_MS noch aktiv ist (z.B. durch unterbrochene
    // Pointer-Events beim App-Backgrounding), wird er erzwungen abgebrochen.
    function startDragWatchdog() {
        if (!ENABLE_DRAG_WATCHDOG) return;
        clearDragWatchdog();
        _dragWatchdogTimer = setTimeout(function () {
            if (_isDragging) {
                forceResetDragState();
            }
        }, DRAG_TIMEOUT_MS);
    }

    function clearDragWatchdog() {
        if (_dragWatchdogTimer !== null) {
            clearTimeout(_dragWatchdogTimer);
            _dragWatchdogTimer = null;
        }
    }

    function schedulePendingReset() {
        if (!ENABLE_DRAG_WATCHDOG) return;
        if (_pendingResetTimer !== null) return;
        _pendingResetTimer = setTimeout(function () {
            _pendingResetTimer = null;
            if (_isDragging) forceResetDragState();
        }, 1500);
    }

    function clearPendingReset() {
        if (_pendingResetTimer !== null) {
            clearTimeout(_pendingResetTimer);
            _pendingResetTimer = null;
        }
    }

    // Setzt den Drag-State vollständig zurück und informiert Blazor.
    // Wird vom Watchdog und vom visibilitychange-Handler aufgerufen.
    function forceResetDragState() {
        _isDragging = false;
        _dragStartedAt = 0;
        unlockNativeScrollForDrag();
        clearDragWatchdog();
        clearPendingReset();
        cleanupSortableGhosts();
        setDraggingWithToken(false, _dragToken);
        safeInvoke("OnCircuitReconnected");
        runPendingAfterDrag();
    }

    function runPendingAfterDrag() {
        if (_isDragging) return;

        if (_pendingDispose) {
            _pendingDispose = false;
            doDisposeKanban();
        }

        if (_pendingInitArgs) {
            const args = _pendingInitArgs;
            _pendingInitArgs = null;
            doInitKanban(args.boardScrollId, args.columnsHostId, args.dotNetRef, args.sortMode);
        }
    }

    function cleanupSortableGhosts() {
        // 1) Nur echte Body-Fallback-Klone entfernen.
        //    Nicht global ".sortable-*" entfernen: in WebView/Sortable-Randfällen kann diese
        //    Klasse kurzzeitig am realen Karten-Element hängen, was sonst echte Aufgaben
        //    während des Ziehens löschen würde.
        document.querySelectorAll('body > [data-taskid].sortable-drag, body > [data-taskid].sortable-fallback, body > [data-taskid].sortable-ghost').forEach(el => {
            try { el.remove(); } catch { }
        });

        // 2) Defensive Class-Cleanup auf echten Karten, damit keine "Schattenkarte"
        //    mit ghost/chosen/drag Styling sichtbar bleibt.
        document.querySelectorAll('.kanban-sortable [data-taskid]').forEach(el => {
            try {
                el.classList.remove('sortable-ghost', 'sortable-chosen', 'sortable-drag', 'sortable-fallback');
            } catch { }
        });

        // 3) (Body-Klone wurden bereits in Schritt 1 entfernt.)
    }

    function destroyCards() {
        _cardSortables.forEach(entry => {
            try { entry.cleanupTouchGuard?.(); } catch { }
            try { entry.sortable?.destroy(); } catch { }
        });
        _cardSortables.length = 0;

        // Defensive reset: drag-state darf nie beim Dispose hängen bleiben.
        _isDragging = false;
        unlockNativeScrollForDrag();
        clearDragWatchdog();
        safeInvoke("SetDragging", false);

        // Remove orphaned fallback elements from interrupted drags (WebView2/offline edge cases).
        cleanupSortableGhosts();
    }

    function destroyColumns() {
        if (_columnsSortable) {
            try { _columnsSortable.destroy(); } catch { }
            _columnsSortable = null;
        }
        _columnsHostEl = null;
    }

    // (Optional) im laufenden Betrieb togglen (falls du später ohne dispose arbeiten willst)
    function setCardsEnabled(enabled) {
        _cardSortables.forEach(entry => {
            try { entry.sortable.option("disabled", !enabled); } catch { }
        });

        if (!enabled) {
            // Wenn während eines Drags deaktiviert wird, defensiv aufräumen.
            cleanupSortableGhosts();
            safeInvoke("SetDragging", false);
        }
    }

    function attachTouchScrollGuard(sortable, rootEl) {
        // Native scrolling is provided by CSS touch-action. Do not disable Sortable
        // while the finger is down: WKWebView can then keep the fallback clone alive
        // but stop calculating sortable targets, so cards drag without a drop preview.
        const onPointerDown = (ev) => {
            if (ev.pointerType !== "touch") return;
            if (!ev.target?.closest?.("[data-taskid]")) return;
            try { sortable.option("disabled", !isCustomMode()); } catch { }
        };

        rootEl.addEventListener("pointerdown", onPointerDown, { capture: true, passive: true });

        return () => {
            rootEl.removeEventListener("pointerdown", onPointerDown, true);
        };
    }

    function createSortableForHost(hostEl) {
        // hostEl ist .kanban-sortable[data-col][data-colkey]
        const colKey = hostEl.getAttribute("data-colkey") || "";
        const scrollId = `kanban-col-scroll-${colKey}`;
        const scrollEl = colKey ? document.getElementById(scrollId) : null;

        const enabled = isCustomMode(); // nur in Custom darf Card-DnD aktiv sein
        let draggingActive = false;

        async function finishDragAsync(evt) {
            if (!draggingActive) return;
            draggingActive = false;
            _isDragging = false;
            unlockNativeScrollForDrag();
            clearDragWatchdog();
            clearPendingReset();

            if (evt?.from) highlightColumn(evt.from, false);
            if (evt?.to) highlightColumn(evt.to, false);

            cleanupSortableGhosts();
            await safeInvoke("SetDragging", false);
            runPendingAfterDrag();
        }

        const sortable = new Sortable(hostEl, {
            group: { name: "kanban", pull: true, put: true },
            animation: 150,
            forceFallback: true,
            fallbackTolerance: 14,

            draggable: "[data-taskid]",

            // Bereiche, die NICHT ziehen sollen (Inputs/Buttons/Links/controls)
            filter: ".no-drag, input, select, textarea, button, a, [data-nodrag='true']",
            preventOnFilter: true,

            // Trello-Feeling: Drag startet erst nach kurzem Hold (v.a. touch/trackpad)
            delay: 520,
            delayOnTouchOnly: true,
            touchStartThreshold: 14,

            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            fallbackOnBody: true,

            scroll: true,
            scrollSensitivity: 70,
            scrollSpeed: 18,

            // ✅ wichtig: Sortable selbst deaktivieren, wenn nicht Custom
            disabled: !enabled,

            onChoose: () => {
                // iPhone/WKWebView: chosenClass (blauer Rahmen) kann vor onStart
                // erscheinen. Ab dann native Scroll-Gesten blockieren, damit die
                // nächste vertikale Bewegung nicht die Seite statt der Karte bewegt.
                if (isCustomMode()) lockNativeScrollForDrag();
            },

            onStart: (evt) => {
                // wenn disabled ist, sollte onStart nicht feuern – aber defensiv:
                if (!isCustomMode()) return;

                draggingActive = true;
                _isDragging = true;
                lockNativeScrollForDrag();
                _dragToken++;
                _dragStartedAt = performance.now();
                startDragWatchdog();
                highlightColumn(evt.from, true);
                setDraggingWithToken(true, _dragToken);
            },

            onMove: (evt, originalEvent) => {
                if (!isCustomMode()) return false;
                if (draggingActive) startDragWatchdog();

                highlightColumn(evt.to, true);
                if (evt.from !== evt.to) highlightColumn(evt.from, false);

                if (_boardEl && originalEvent) autoScrollX(_boardEl, originalEvent.clientX);
                if (scrollEl && originalEvent) autoScrollY(scrollEl, originalEvent.clientY);

                if (shouldInsertBeforeFirst(evt.to, evt.dragged || evt.item, evt.related, originalEvent)) {
                    return -1;
                }

                return true;
            },

            onEnd: async (evt) => {
                // ✅ Alle Drop-Infos VOR finishDragAsync aus dem DOM lesen!
                // finishDragAsync() setzt _isDragging=false und ruft runPendingAfterDrag() auf,
                // das ggf. doInitKanban() ausführt (cleanupSortableGhosts, neue Sortable-Instanzen).
                // Außerdem können durch das Zurücksetzen des Drag-States auf Blazor-Seite
                // Re-Renders angestoßen werden. Würden IDs/Spalten erst danach gelesen,
                // könnten DOM-Patches die Reihenfolge bereits verändert haben.
                const movedEl = evt.item;
                const taskId = (movedEl?.getAttribute("data-taskid") || "").trim();

                // ✅ echte Spaltennamen
                const fromCol = getColName(evt.from);
                const toCol = getColName(evt.to || evt.from); // defensive fallback

                // ✅ WICHTIG: Reihenfolge IMMER direkt nach dem Drop aus dem DOM lesen.
                // Auch wenn gleiche Spalte: wir lesen trotzdem neu (Bugfix!).
                const fromIds = evt.to && evt.to !== evt.from
                    ? getIds(evt.from)
                    : normalizeDropOrderForTopEdge(evt.from, movedEl, evt.originalEvent);
                const toIds = (evt.to && evt.to !== evt.from)
                    ? normalizeDropOrderForTopEdge(evt.to, movedEl, evt.originalEvent)
                    : fromIds;

                await finishDragAsync(evt);

                // ✅ wenn nicht Custom: keine Persistierung / kein Callback
                if (!isCustomMode()) return;

                if (!taskId || !fromCol || !toCol) return;

                await safeInvoke("OnKanbanDropped", taskId, fromCol, toCol, fromIds, toIds);
            },

            onUnchoose: async (evt) => {
                // WICHTIG: In einigen Browser/WebView-Konstellationen kann onUnchoose
                // auch während eines weiterhin aktiven Drags feuern. Ein sofortiges
                // finishDragAsync() würde dann die "an Maus hängende" Karte entfernen.
                // Daher nur dann finalisieren, wenn kein Button mehr gedrückt ist.
                if (!draggingActive) {
                    unlockNativeScrollForDrag();
                    return;
                }

                if (isPointerStillDown(evt) || hasActiveDragVisuals()) return;
                // Ein Tick verzögert erneut prüfen, um kurze DOM-Übergangsphasen bei SortableJS
                // (insb. in WebView2) nicht als Drag-Ende fehlzuinterpretieren.
                await new Promise(resolve => setTimeout(resolve, 0));
                if (isPointerStillDown(evt) || hasActiveDragVisuals()) return;
                await finishDragAsync(evt);
            },

            onCancel: async (evt) => {
                if (!draggingActive) {
                    unlockNativeScrollForDrag();
                    return;
                }

                if (isPointerStillDown(evt) || hasActiveDragVisuals()) return;
                await finishDragAsync(evt);
            }
        });

        const cleanupTouchGuard = attachTouchScrollGuard(sortable, hostEl);
        _cardSortables.push({ sortable, cleanupTouchGuard });
    }

    function initCardSortables() {
        const hosts = document.querySelectorAll(".kanban-sortable[data-col]");
        hosts.forEach(hostEl => createSortableForHost(hostEl));
    }

    function initColumnsSortable(columnsHostId) {
        _columnsHostEl = document.getElementById(columnsHostId);
        if (!_columnsHostEl) return;

        _columnsSortable = new Sortable(_columnsHostEl, {
            animation: 150,
            draggable: ".kanban-column",
            handle: ".kanban-column-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",

            delay: 80,
            delayOnTouchOnly: true,

            onEnd: async () => {
                // ✅ Neue Reihenfolge = echte Spaltennamen (data-col)
                const ordered = Array.from(_columnsHostEl.querySelectorAll(".kanban-column"))
                    .map(el => (el.getAttribute("data-col") || "").trim())
                    .filter(Boolean);

                if (ordered.length > 0) {
                    await safeInvoke("OnKanbanColumnsReordered", ordered);
                }
            }
        });
    }

    function doInitKanban(boardScrollId, columnsHostId, dotNetRef, sortMode) {
        if (!window.Sortable) {
            console.error("SortableJS ist nicht geladen. Bitte SortableJS als <script> einbinden.");
            return;
        }

        doDisposeKanban();

        _dotNet = dotNetRef;
        _boardEl = document.getElementById(boardScrollId);

        _sortMode = (sortMode || "Custom"); // ✅ wichtig: Mode übernehmen

        initCardSortables();

        // falls du später nicht mehr dispose/init machst:
        setCardsEnabled(isCustomMode());

        if (columnsHostId) {
            initColumnsSortable(columnsHostId);
        }
    }

    // API: initKanban(boardScrollId, columnsHostId, dotNetRef, sortMode?)
    window.todoUi.initKanban = function (boardScrollId, columnsHostId, dotNetRef, sortMode) {
        if (_isDragging) {
            // Blazor ruft initKanban während eines aktiven Drags auf (z.B. weil
            // _initialized=false gesetzt wurde, während _isDraggingFromSortable in C#
            // noch false war – Race-Window zwischen onStart und SetDraggingWithToken).
            // Den laufenden Drag NICHT abbrechen (würde Ghost entfernen → Karte
            // "verschwindet"). Init für nach dem Drag-Ende einreihen.
            _pendingInitArgs = { boardScrollId, columnsHostId, dotNetRef, sortMode };
            return;
        }
        doInitKanban(boardScrollId, columnsHostId, dotNetRef, sortMode);
    };

    // Rückwärtskompatibilität (falls noch irgendwo verwendet)
    window.todoUi.initSortableKanban = function (boardScrollId, dotNetRef) {
        if (!window.Sortable) {
            console.error("SortableJS ist nicht geladen. Bitte SortableJS als <script> einbinden.");
            return;
        }

        doDisposeKanban();

        _dotNet = dotNetRef;
        _boardEl = document.getElementById(boardScrollId);

        _sortMode = "Custom"; // initSortableKanban war immer Custom

        initCardSortables();
        setCardsEnabled(true);
    };

    // Optional: falls du später ohne komplettes Reinit umschalten willst
    window.todoUi.setKanbanSortMode = function (sortMode) {
        _sortMode = (sortMode || "Custom");
        setCardsEnabled(isCustomMode());
    };

    function doDisposeKanban() {
        destroyCards();
        destroyColumns();
        cleanupSortableGhosts();
        setDraggingWithToken(false, _dragToken);

        _dotNet = null;
        _boardEl = null;
        _sortMode = "Custom";
    }

    window.todoUi.disposeKanban = function () {
        if (_isDragging) {
            // Navigation/Dispose während aktivem Drag darf die nächste View nicht blockieren.
            forceResetDragState();
        }
        doDisposeKanban();
    };

    window.todoUi.disposeSortableKanban = function () {
        window.todoUi.disposeKanban();
    };

    // ── Blazor Server: Circuit-Reconnect ──────────────────────────────────────
    // Hängengebliebenen Drag-State zurücksetzen wenn der Blazor-Circuit wieder
    // verbunden ist. (Funktioniert nur in Blazor Server, nicht in MAUI Hybrid.)
    document.addEventListener("components-reconnect-state-changed", function (event) {
        if (event.detail && event.detail.state === "hide") {
            cleanupSortableGhosts();
            safeInvoke("OnCircuitReconnected");
        }
    });

    // ── MAUI / PWA: App-Vordergrund-Erkennung (visibilitychange) ──────────────
    // In MAUI Blazor Hybrid gibt es keinen Blazor-Circuit – der obige Reconnect-
    // Handler feuert nie. Wenn die App in den Hintergrund geht, kann SortableJS
    // keinen "onEnd"-Event mehr liefern. Dadurch bleibt _isDragging = true und
    // ShouldRender() = false → die gesamte Kanban-UI friert ein.
    //
    // Lösung: Sobald das Dokument wieder sichtbar wird (App-Resume, Tab-Wechsel,
    // Display-Wake), prüfen wir ob ein Drag hängt und setzen ihn erzwungen zurück.
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState !== "visible") return;

        if (_isDragging) {
            // Drag kann nach App-Backgrounding unmöglich noch aktiv sein.
            forceResetDragState();
        } else if (_preventNativeScrollForDrag) {
            unlockNativeScrollForDrag();
        }
    });

    // Falls ein Drag ohne onEnd endet, beim nächsten Pointer-Up defensiv zurücksetzen.
    document.addEventListener("pointerup", function () {
        scheduleDragStateCleanup(0);
    }, { capture: true, passive: true });

    document.addEventListener("touchend", function () {
        scheduleDragStateCleanup(0);
    }, { capture: true, passive: true });

    document.addEventListener("touchcancel", function () {
        scheduleDragStateCleanup(0);
    }, { capture: true, passive: true });
})();
