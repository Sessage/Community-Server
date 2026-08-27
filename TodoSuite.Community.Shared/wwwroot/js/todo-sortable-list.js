// wwwroot/js/todo-sortable-list.js
// Requires SortableJS global "Sortable"

window.todoUi = window.todoUi || {};

(function () {
    // ── Globaler Pointer-Tracker (einmalig, geteilt zwischen list- und kanban-Modul) ──
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

    const _instances = new Map();

    // ── Modulweiter Drag-State ─────────────────────────────────────────────────
    // Verfolgt ob irgendeine Listeninstanz gerade zieht. Wird vom
    // visibilitychange-Handler und dem Watchdog-Timer verwendet.
    let _isDragging = false;
    let _dragToken = 0;
    let _activeDotNetRef = null;   // dotNetRef der aktuell ziehenden Instanz
    let _dragWatchdogTimer = null;
    let _pendingResetTimer = null;
    let _pendingIdleAction = null;
    let _preventNativeScrollForDrag = false;
    // Watchdog deaktiviert:
    // Ein fixer Timeout bricht legitime lange Drag-Gesten ab und lässt Karten
    // scheinbar "verschwinden". Cleanup erfolgt über reguläre Sortable-Events,
    // visibilitychange und dispose.
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

    function scheduleDragStateCleanup(delayMs = 350) {
        if (!_isDragging && !_preventNativeScrollForDrag) return;

        setTimeout(function () {
            const pointerStillDown = window._todoUiPointerTracker?.isDown();
            if (_isDragging && !hasActiveDragVisuals() && !pointerStillDown) {
                forceResetDragging();
            } else if (!_isDragging && _preventNativeScrollForDrag && !hasActiveDragVisuals() && !pointerStillDown) {
                unlockNativeScrollForDrag();
            }
        }, delayMs);
    }

    function startDragWatchdog() {
        if (!ENABLE_DRAG_WATCHDOG) return;
        clearDragWatchdog();
        _dragWatchdogTimer = setTimeout(function () {
            if (_isDragging) {
                forceResetDragging();
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
            if (_isDragging) forceResetDragging();
        }, 1500);
    }

    function clearPendingReset() {
        if (_pendingResetTimer !== null) {
            clearTimeout(_pendingResetTimer);
            _pendingResetTimer = null;
        }
    }

    // Setzt den Drag-State vollständig zurück und informiert Blazor.
    // Aufgerufen vom Watchdog-Timer und vom visibilitychange-Handler.
    function forceResetDragging() {
        _isDragging = false;
        unlockNativeScrollForDrag();
        clearDragWatchdog();
        clearPendingReset();
        window.todoUi.cleanupSortableGhosts();
        if (_activeDotNetRef) {
            try { _activeDotNetRef.invokeMethodAsync("SetDraggingWithToken", false, _dragToken); } catch {
                try { _activeDotNetRef.invokeMethodAsync("SetDragging", false); } catch { }
            }
            _activeDotNetRef = null;
        }
        runPendingIdleAction();
    }

    function runPendingIdleAction() {
        if (_isDragging || !_pendingIdleAction) return;
        const action = _pendingIdleAction;
        _pendingIdleAction = null;
        try { action(); } catch { }
    }

    function isPointerStillDown(evt) {
        // Primär: globaler Tracker ist unabhängig von SortableJS-internen Events.
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
        return !!document.querySelector('.sortable-chosen, .sortable-drag, .sortable-fallback, [data-taskid].sortable-chosen, [data-taskid].sortable-drag, [data-taskid].sortable-fallback');
    }

    // Liest nur direkte Task-Karten aus dem Listen-Host.
    // Verhindert, dass temporäre/nestede data-taskid-Knoten die Reihenfolge verfälschen.
    function getHostTaskIds(hostEl) {
        if (!hostEl) return [];
        const ids = [];
        for (const el of Array.from(hostEl.children)) {
            if (!el || el.nodeType !== 1 || !el.hasAttribute("data-taskid")) continue;
            const id = (el.getAttribute("data-taskid") || "").trim();
            if (id) ids.push(id);
        }
        return ids;
    }

    function attachTouchScrollGuard(sortable, rootEl) {
        // Do not toggle Sortable's disabled option during an active touch gesture.
        // On iOS/WKWebView that breaks Sortable's fallback drop-target calculation:
        // dragging still starts, but the ghost/insert preview stops updating. Native
        // scrolling is handled by CSS touch-action plus Sortable's touch delay.
        const onPointerDown = (ev) => {
            if (ev.pointerType !== "touch") return;
            if (!ev.target?.closest?.("[data-taskid]")) return;
            try { sortable.option("disabled", false); } catch { }
        };

        rootEl.addEventListener("pointerdown", onPointerDown, { capture: true, passive: true });

        return () => {
            rootEl.removeEventListener("pointerdown", onPointerDown, true);
        };
    }

    window.todoUi.initSortableList = function (listEl, scrollEl, dotNetRef) {
        if (!window.Sortable) {
            console.error("SortableJS ist nicht geladen. Bitte SortableJS als <script> einbinden.");
            return;
        }

        if (!listEl) return;
        if (_instances.has(listEl)) return;

        let draggingActive = false;

        async function setDraggingAsync(value, token) {
            if (!dotNetRef) return;
            try { await dotNetRef.invokeMethodAsync("SetDraggingWithToken", value, token); }
            catch {
                try { await dotNetRef.invokeMethodAsync("SetDragging", value); } catch { }
            }
        }

        async function finishDragAsync() {
            if (!draggingActive) return;
            // ✅ Token VOR dem Reset snapshotten – verhindert, dass ein späteres onStart
            // mit höherem Token den SetDraggingWithToken(false)-Aufruf blockiert.
            const token = _dragToken;
            draggingActive = false;
            _isDragging = false;
            unlockNativeScrollForDrag();
            _activeDotNetRef = null;
            clearDragWatchdog();
            clearPendingReset();

            // Defensive cleanup: in MAUI/WebView fallback ghosts may remain and block pointer input.
            window.todoUi.cleanupSortableGhosts();
            await setDraggingAsync(false, token);
            runPendingIdleAction();
        }

        const sortable = new Sortable(listEl, {
            animation: 150,
            draggable: "[data-taskid]",
            // WebView2 (MAUI Windows) verhält sich mit nativer HTML5-DnD unzuverlässig.
            // Fallback nutzt Pointer-Events und funktioniert konsistent in App + Browser.
            forceFallback: true,
            fallbackTolerance: 14,
            delay: 520,
            delayOnTouchOnly: true,
            touchStartThreshold: 14,
            filter: "input, select, textarea, button, a, [data-nodrag='true']",
            preventOnFilter: true,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            fallbackOnBody: true,
            swapThreshold: 0.65,

            scroll: true,
            scrollSensitivity: 70,
            scrollSpeed: 18,

            onChoose: function () {
                // iPhone/WKWebView: chosenClass (blauer Rahmen) kann sichtbar sein,
                // bevor Sortable onStart feuert. Ab diesem Moment darf iOS vertikale
                // Bewegung nicht mehr als Seiten-Scroll übernehmen.
                lockNativeScrollForDrag();
            },

            onMove: function (evt, originalEvent) {
                // Solange Pointer-Events laufen, ist der Drag gesund.
                // Damit der Watchdog nicht mitten in einem langsamen/langen Drag feuert,
                // verlängern wir bei jeder Bewegung das Timeout.
                if (draggingActive) startDragWatchdog();

                // optional: eigener helper
                if (window.todoUi && window.todoUi.autoScrollY && scrollEl && originalEvent) {
                    window.todoUi.autoScrollY(scrollEl, originalEvent.clientY);
                }
                return true;
            },

            // ✅ WICHTIG: onStart DARF NICHT async sein.
            // SortableJS wartet nicht auf den zurückgegebenen Promise. Wenn onStart async
            // ist, kann es passieren, dass setDraggingAsync(true) NACH dem bereits
            // abgeschlossenen setDraggingAsync(false) aus onEnd bei Blazor ankommt und
            // dort _isDragging dauerhaft auf true lässt → UI hängt.
            onStart: function () {
                _dragToken++;
                draggingActive = true;
                _isDragging = true;
                lockNativeScrollForDrag();
                _activeDotNetRef = dotNetRef;
                startDragWatchdog();
                setDraggingAsync(true, _dragToken); // fire-and-forget – kein await!
            },

            onEnd: async function () {
                // IDs sofort und nur als direkte Host-Kinder aus dem DOM lesen –
                // vor jeglichem Blazor-Rendering.
                const ids = getHostTaskIds(listEl);

                // Lokalen JS-Drag-State bereinigen (synchron, kein await nötig).
                if (!draggingActive) return;
                const token = _dragToken;
                draggingActive = false;
                _isDragging = false;
                unlockNativeScrollForDrag();
                _activeDotNetRef = null;
                clearDragWatchdog();
                clearPendingReset();
                window.todoUi.cleanupSortableGhosts();

                // Drag-Ende und neue Reihenfolge in einem einzigen Blazor-Aufruf übergeben.
                // Dadurch ist _pendingOrder bereits gesetzt, BEVOR ApplyDraggingStateAsync(false)
                // via OnDraggingChanged den Parent-Re-render auslöst.
                // Früher: finishDragAsync() → OnDraggingChanged → Parent rendert mit alter
                // Reihenfolge (SPRUNG) → OnListReordered setzt _pendingOrder (zu spät).
                // Jetzt:  FinishListDragWithOrder setzt _pendingOrder DANN ruft
                //         ApplyDraggingStateAsync(false) → Parent rendert mit _pendingOrder ✓
                if (dotNetRef) {
                    try {
                        await dotNetRef.invokeMethodAsync("FinishListDragWithOrder", token, ids);
                    } catch (e) {
                        // Fallback: zumindest den Drag-State zurücksetzen
                        try { await setDraggingAsync(false, token); } catch { }
                        console.error("FinishListDragWithOrder fehlgeschlagen", e);
                    }
                }

                runPendingIdleAction();
            },

            onUnchoose: function () {
                // Wichtig: onUnchoose kann vor onEnd auftreten (Browser/WebView Timing).
                // Wenn wir hier ein Drag-Ende signalisieren, kann Blazor/Parent zu früh
                // mit der alten Reihenfolge rendern -> sichtbares "Zurückspringen".
                // Deshalb niemals über onUnchoose beenden; onEnd/onCancel übernehmen das.
                if (!draggingActive) unlockNativeScrollForDrag();
            },

            onCancel: async function (evt) {
                // Drag wurde z.B. durch Escape oder OS-Geste abgebrochen.
                if (!draggingActive) {
                    unlockNativeScrollForDrag();
                    return;
                }

                if (isPointerStillDown(evt) || hasActiveDragVisuals()) return;
                await finishDragAsync();
            }
        });

        const cleanupTouchGuard = attachTouchScrollGuard(sortable, listEl);
        _instances.set(listEl, { sortable, cleanupTouchGuard });
    };

    window.todoUi.reinitSortableList = function (hostEl, scrollEl, dotNetRef) {
        if (_isDragging) {
            // Blazor fordert einen Re-Init während eines aktiven Drags an.
            // Den laufenden Drag NICHT abbrechen (würde Ghost entfernen → Karte
            // "verschwindet"). Re-Init für nach dem Drag-Ende einreihen.
            _pendingIdleAction = () => window.todoUi.reinitSortableList(hostEl, scrollEl, dotNetRef);
            return;
        }

        // Destroy the instance for the given element (if found by reference).
        try { window.todoUi.disposeSortableList(hostEl); } catch { }

        // When Blazor changes @key on the host element, the old DOM node is replaced by a
        // new one. The old SortableJS instance is stored under the old element reference and
        // would never be destroyed by the lookup above. Sweep the map and destroy every
        // instance whose element is no longer attached to the document.
        _instances.forEach((entry, el) => {
            if (!document.contains(el)) {
                try { entry.cleanupTouchGuard?.(); } catch { }
                try { entry.sortable?.destroy(); } catch { }
                _instances.delete(el);
            }
        });

        // Remove any ghost / fallback elements that SortableJS may have left on <body>
        // (e.g. when a touch drag was cancelled by the OS or interrupted in MAUI).
        window.todoUi.cleanupSortableGhosts();

        window.todoUi.initSortableList(hostEl, scrollEl, dotNetRef);
    };

    window.todoUi.disposeSortableList = function (listEl) {
        if (_isDragging) {
            // Navigation während Drag: sofort resetten statt bis Watchdog zu warten.
            forceResetDragging();
        }

        const entry = _instances.get(listEl);
        if (entry) {
            try { entry.cleanupTouchGuard?.(); } catch { }
            try { entry.sortable?.destroy(); } catch { }
            _instances.delete(listEl);
        }

        // Also remove any orphaned drag-ghost elements from <body> so they can never
        // bleed over into a different list view that is rendered afterwards.
        window.todoUi.cleanupSortableGhosts();
    };

    // Removes stale SortableJS ghost / fallback elements from the document body.
    // SortableJS appends these to <body> when forceFallback + fallbackOnBody are active.
    // Under normal circumstances SortableJS cleans them up itself, but interrupted drags
    // (e.g. OS gesture take-over, MAUI WebView quirks) can leave orphans behind.
    window.todoUi.cleanupSortableGhosts = function () {
        // Remove only body-level fallback clones.
        // Important: never remove generic ".sortable-*" elements globally because on some
        // WebView/Sortable edge-cases those classes can temporarily be attached to the real
        // task element itself, which would delete the actual card while dragging.
        document.querySelectorAll('body > [data-taskid].sortable-drag, body > [data-taskid].sortable-fallback, body > [data-taskid].sortable-ghost').forEach(el => {
            try { el.remove(); } catch { }
        });

        // Also clear stale drag classes on real task cards without removing cards.
        document.querySelectorAll('[data-taskid]').forEach(el => {
            try {
                el.classList.remove('sortable-ghost', 'sortable-chosen', 'sortable-drag', 'sortable-fallback');
            } catch { }
        });
    };

    // ── MAUI / PWA: App-Vordergrund-Erkennung (visibilitychange) ──────────────
    // In MAUI Blazor Hybrid und PWAs kann die App in den Hintergrund gehen während
    // ein Drag aktiv ist. SortableJS liefert dann kein onEnd mehr, daher bleibt
    // _isDragging in Blazor auf true. Sobald das Dokument wieder sichtbar ist,
    // setzen wir den Drag-State erzwungen zurück.
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState !== "visible") return;

        if (_isDragging) {
            forceResetDragging();
        } else if (_preventNativeScrollForDrag) {
            unlockNativeScrollForDrag();
        }
    });

    // Falls Browser/WebView den Drag ohne onEnd beendet, räumen wir nach Pointer-Up auf.
    // Wichtig: nicht sofort bei t=0 resetten, sonst kann ein legitimes onEnd (das kurz
    // verzögert eintrifft) zuvor überholt werden -> sichtbares Zurückspringen.
    document.addEventListener("pointerup", function () {
        scheduleDragStateCleanup(350);
    }, { capture: true, passive: true });

    document.addEventListener("touchend", function () {
        scheduleDragStateCleanup(350);
    }, { capture: true, passive: true });

    document.addEventListener("touchcancel", function () {
        scheduleDragStateCleanup(0);
    }, { capture: true, passive: true });

    /* =========================================================
     * ✅ NEU: Browser Notifications (Erinnerungen im offenen Browser)
     * ========================================================= */

    window.todoUi.requestNotificationPermission = async function () {
        if (!("Notification" in window)) {
            console.warn("Browser-Benachrichtigungen werden nicht unterstützt.");
            return "unsupported";
        }

        if (Notification.permission === "granted") return "granted";
        if (Notification.permission === "denied") return "denied";

        try {
            return await Notification.requestPermission();
        } catch (e) {
            console.warn("Notification.requestPermission() fehlgeschlagen:", e);
            return "error";
        }
    };

    window.todoUi.getNotificationPermission = function () {
        if (!("Notification" in window)) return "unsupported";
        return Notification.permission; // "granted" | "denied" | "default"
    };


    window.todoUi.downloadFileFromBase64 = function (fileName, mimeType, base64) {
        try {
            const binary = atob(base64 || "");
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                bytes[i] = binary.charCodeAt(i);
            }

            const blob = new Blob([bytes], { type: mimeType || "application/octet-stream" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = fileName || "download";
            a.style.display = "none";
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (e) {
            console.error("Dateidownload fehlgeschlagen", e);
        }
    };

    window.todoUi.downloadFileFromStream = async function (fileName, mimeType, contentStreamReference) {
        let url = null;
        try {
            const buffer = await contentStreamReference.arrayBuffer();
            const blob = new Blob([buffer], { type: mimeType || "application/octet-stream" });
            url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = fileName || "download";
            a.style.display = "none";
            document.body.appendChild(a);
            a.click();
            a.remove();
        } catch (e) {
            console.error("Dateidownload fehlgeschlagen", e);
            throw e;
        } finally {
            if (url) setTimeout(() => URL.revokeObjectURL(url), 1000);
        }
    };
    window.todoUi.showNotification = function (title, body) {
        if (!("Notification" in window)) return;
        if (Notification.permission !== "granted") return;

        try {
            new Notification(title || "Erinnerung", { body: body || "" });
        } catch (e) {
            console.warn("Notification konnte nicht angezeigt werden:", e);
        }
    };
})();
