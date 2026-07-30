// wwwroot/js/todo-sortable-nav.js
// Requires SortableJS global "Sortable"
//
// Unterstützte DOM-Struktur (neu: unified host):
//  - Unified Host:     #nav-unified-host
//      Direkte Kinder mit .nav-mixed-item können sein:
//        .nav-group-card[data-groupid]   → Gruppe
//        .nav-list-item[data-listid]     → Root-Liste
//  - Gruppen-Listen:   .nav-group-lists-host[data-groupid]
//      Kinder:  .nav-list-item[data-listid]
//
// Drag-Handle: .nav-drag-handle  (für alle Elemente in beiden Containern)
//
// Blazor Callbacks (DotNet):
//  - OnNavMixedReordered(string[] orderedDescriptors)
//      descriptors: "group:<guid>" oder "list:<guid>", in neuer Reihenfolge
//  - OnNavGroupsReordered(string[] orderedGroupIds)   [Kompatibilitäts-Stub]
//  - OnNavListsReordered(string groupIdOrEmpty, string[] orderedListIds)
//  - OnNavListMoved(string listId, string fromGroupIdOrEmpty, string toGroupIdOrEmpty, string[] fromIds, string[] toIds)
//  - OnNavDragStarted()
//  - OnNavDragAborted()
//  - OnCircuitReconnected()

window.todoUi = window.todoUi || {};

(function () {
    let _dotNet = null;
    let _suppressNavClickUntil = 0;

    let _unifiedHost = null;
    let _unifiedSortable = null;
    let _rootHost = null;
    let _rootSortable = null;
    let _groupsHost = null;
    let _groupsSortable = null;

    const _groupListSortables = [];

    // Offline / Drag-Guard state
    let _isDragging = false;
    let _reinitPending = false;

    // Letzte Cursor-Y-Position während eines Drags (für Off-by-one-Korrektur oben)
    let _dragLastY = null;
    function _onDragPointerMove(e) {
        _dragLastY = e.clientY ?? e.touches?.[0]?.clientY ?? null;
    }

    // ── Drag-Watchdog ──────────────────────────────────────────────────────────
    let _dragWatchdogTimer = null;
    const DRAG_TIMEOUT_MS = 20_000;

    function startDragWatchdog() {
        clearDragWatchdog();
        _dragWatchdogTimer = setTimeout(function () {
            if (_isDragging) forceResetDragging();
        }, DRAG_TIMEOUT_MS);
    }

    function clearDragWatchdog() {
        if (_dragWatchdogTimer !== null) {
            clearTimeout(_dragWatchdogTimer);
            _dragWatchdogTimer = null;
        }
    }

    function forceResetDragging() {
        _isDragging = false;
        clearDragWatchdog();
        cleanupSortableGhosts();
        safeInvoke("OnNavDragAborted");
        checkPendingReinit();
    }

    function cleanupSortableGhosts() {
        document.querySelectorAll('.sortable-drag, .sortable-fallback, .sortable-ghost').forEach(el => {
            if (el.closest('#nav-unified-host, #nav-root-lists-host, #nav-groups-host, .nav-group-lists-host')) return;
            try { el.remove(); } catch { }
        });
    }

    function suppressNavClicksFor(ms = 300) {
        _suppressNavClickUntil = Date.now() + ms;
    }

    function shouldSuppressNavClick() {
        return Date.now() < _suppressNavClickUntil;
    }

    function onDocumentClickCapture(evt) {
        if (!shouldSuppressNavClick()) return;
        const navItem = evt.target?.closest?.(".nav-list-item, .nav-group-card");
        if (!navItem) return;
        evt.preventDefault();
        evt.stopPropagation();
    }

    function safeInvoke(method, ...args) {
        if (!_dotNet) return Promise.resolve();
        try {
            return _dotNet.invokeMethodAsync(method, ...args)
                .catch(e => console.error("[todoUi] DotNet invoke failed:", method, e));
        } catch (e) {
            console.error("[todoUi] DotNet invoke sync failed:", method, e);
            return Promise.resolve();
        }
    }

    // Gibt die direkt im unified host liegenden Root-Listen-IDs zurück
    // (nicht die in Gruppen verschachtelten).
    function getRootListIds(hostEl) {
        if (!hostEl) return [];
        return Array.from(hostEl.querySelectorAll(":scope > [data-listid]"))
            .map(el => (el.getAttribute("data-listid") || "").trim())
            .filter(Boolean);
    }

    function getGroupIds(hostEl) {
        if (!hostEl) return [];
        return Array.from(hostEl.querySelectorAll(":scope > [data-groupid]"))
            .map(el => (el.getAttribute("data-groupid") || "").trim())
            .filter(Boolean);
    }

    // Gibt alle [data-listid]-Kinder eines Gruppen-Listen-Hosts zurück.
    function getGroupListIds(hostEl) {
        if (!hostEl) return [];
        return Array.from(hostEl.querySelectorAll("[data-listid]"))
            .map(el => (el.getAttribute("data-listid") || "").trim())
            .filter(Boolean);
    }

    // Gibt IDs abhängig vom Container zurück.
    function getIds(hostEl) {
        if (!hostEl) return [];
        if (hostEl === _unifiedHost) return getRootListIds(hostEl);
        return getGroupListIds(hostEl);
    }

    // Baut die Descriptor-Liste für den unified host: "group:<id>" / "list:<id>"
    function getUnifiedDescriptors() {
        if (_unifiedHost) {
            return Array.from(_unifiedHost.querySelectorAll(":scope > .nav-mixed-item"))
                .map(el => {
                    const gId = (el.getAttribute("data-groupid") || "").trim();
                    const lId = (el.getAttribute("data-listid") || "").trim();
                    return gId ? `group:${gId}` : lId ? `list:${lId}` : null;
                })
                .filter(Boolean);
        }

        if (!_rootHost && !_groupsHost) return [];

        return [
            ...getRootListIds(_rootHost).map(id => `list:${id}`),
            ...getGroupIds(_groupsHost).map(id => `group:${id}`)
        ];
    }

    function getPointerClientY(evt) {
        return _dragLastY
            ?? evt?.originalEvent?.clientY
            ?? evt?.originalEvent?.changedTouches?.[0]?.clientY
            ?? null;
    }

    function normalizeDropPosition(hostEl, draggedEl, itemSelector, evt) {
        if (!hostEl || !draggedEl) return;

        const mouseY = getPointerClientY(evt);
        if (mouseY === null) return;

        const items = Array.from(hostEl.querySelectorAll(`:scope > ${itemSelector}`));
        const currentIdx = items.indexOf(draggedEl);
        if (currentIdx < 0) return;

        const previousItem = currentIdx > 0 ? items[currentIdx - 1] : null;
        if (previousItem) {
            const previousRect = previousItem.getBoundingClientRect();
            if (mouseY < previousRect.top + previousRect.height / 2) {
                hostEl.insertBefore(draggedEl, previousItem);
                return;
            }
        }

        const nextItem = currentIdx < items.length - 1 ? items[currentIdx + 1] : null;
        if (nextItem) {
            const nextRect = nextItem.getBoundingClientRect();
            if (mouseY > nextRect.top + nextRect.height / 2) {
                hostEl.insertBefore(draggedEl, nextItem.nextSibling);
            }
        }
    }

    // ── Sortable teardown ──────────────────────────────────────────────────────

    function destroySortables() {
        try { if (_unifiedSortable) _unifiedSortable.destroy(); } catch { }
        _unifiedSortable = null;
        _unifiedHost = null;

        try { if (_rootSortable) _rootSortable.destroy(); } catch { }
        _rootSortable = null;
        _rootHost = null;

        try { if (_groupsSortable) _groupsSortable.destroy(); } catch { }
        _groupsSortable = null;
        _groupsHost = null;

        _groupListSortables.forEach(s => { try { s.destroy(); } catch { } });
        _groupListSortables.length = 0;

        document.removeEventListener("click", onDocumentClickCapture, true);
        cleanupSortableGhosts();
    }

    function destroyAll() {
        destroySortables();
        _dotNet = null;
    }

    function reinitSortables() {
        destroySortables();
        if (!_dotNet) return;
        document.addEventListener("click", onDocumentClickCapture, true);
        initUnifiedHostSortable();
        initSplitRootSortable();
        initSplitGroupsSortable();
        initGroupListsSortables();
    }

    function checkPendingReinit() {
        if (!_reinitPending || _isDragging) return;
        _reinitPending = false;
        reinitSortables();
    }

    // ── Unified Host Sortable ──────────────────────────────────────────────────
    // Verwaltet Gruppen (.nav-group-card) und Root-Listen (.nav-list-item)
    // gemeinsam in einem Container. Gruppen können nicht in Gruppen gezogen werden.

    function initUnifiedHostSortable() {
        _unifiedHost = document.getElementById("nav-unified-host");
        if (!_unifiedHost) return;

        _unifiedSortable = new Sortable(_unifiedHost, {
            // Gleiche SortableJS-Gruppe wie die Gruppen-Listen-Hosts →
            // Root-Listen können in Gruppen gezogen werden und umgekehrt.
            group: {
                name: "navlists",
                pull: true,
                put: function (to, from, dragEl) {
                    // Gruppenarten dürfen nur im unified host bleiben,
                    // nicht in Gruppen-Listen-Hosts gezogen werden.
                    // Hier: unified host darf alles aufnehmen.
                    return true;
                }
            },
            animation: 150,
            draggable: ".nav-mixed-item",
            handle: ".nav-drag-handle, .nav-list-handle, .nav-group-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            fallbackOnBody: true,
            swapThreshold: 1.0,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            delay: 80,
            delayOnTouchOnly: true,

            onStart: () => {
                _isDragging = true;
                _dragLastY = null;
                document.addEventListener('pointermove', _onDragPointerMove, { passive: true });
                startDragWatchdog();
                suppressNavClicksFor(500);
                safeInvoke("OnNavDragStarted");
            },

            // Wenn ein List-Item aus einer Gruppe in den unified host gezogen wird:
            // .nav-mixed-item hinzufügen, damit der unified host es als eigenes Item erkennt.
            onAdd: (evt) => {
                if (evt.item && !evt.item.classList.contains('nav-mixed-item')) {
                    evt.item.classList.add('nav-mixed-item');
                }
            },

            onEnd: async (evt) => {
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                clearDragWatchdog();
                suppressNavClicksFor(300);

                // Off-by-one-Korrektur: bei forceFallback kann das Element
                // auf Index 1 landen, obwohl der Cursor über Position 0 war.
                normalizeDropPosition(_unifiedHost, evt.item, ".nav-mixed-item", evt);
                _dragLastY = null;

                cleanupSortableGhosts();

                const item = evt.item;
                const listId = (item?.getAttribute("data-listid") || "").trim();
                const groupId = (item?.getAttribute("data-groupid") || "").trim();

                if (!listId && !groupId) {
                    await safeInvoke("OnNavDragAborted");
                    checkPendingReinit();
                    return;
                }

                const fromEl = evt.from;
                const toEl = evt.to;

                if (toEl === _unifiedHost) {
                    if (groupId) {
                        // Gruppe wurde im unified host umsortiert
                        await safeInvoke("OnNavMixedReordered", getUnifiedDescriptors());
                    } else if (listId && fromEl === _unifiedHost) {
                        // Root-Liste innerhalb des unified host umsortiert
                        await safeInvoke("OnNavMixedReordered", getUnifiedDescriptors());
                    } else if (listId && fromEl !== _unifiedHost) {
                        // Liste aus einer Gruppe in den unified host (Root) gezogen.
                        // → OnNavListMovedMixed: übergibt unified descriptors, damit die
                        //   globalen Positionen nach MoveListAsync korrekt repariert werden.
                        const fromGroup = (fromEl?.getAttribute("data-groupid") || "").trim();
                        const fromIds = getGroupListIds(fromEl);
                        const toIds = getRootListIds(_unifiedHost);
                        const descriptors = getUnifiedDescriptors();
                        await safeInvoke("OnNavListMovedMixed", listId, fromGroup, "", fromIds, toIds, descriptors);
                    }
                } else if (toEl !== _unifiedHost && listId) {
                    // Root-Liste in eine Gruppe gezogen.
                    // Callback vom onEnd des unified host (source).
                    // → OnNavListMovedMixed: unified descriptors NACH dem Drop (Liste fehlt in unified)
                    const toGroup = (toEl?.getAttribute("data-groupid") || "").trim();
                    const fromIds = getRootListIds(_unifiedHost);
                    const toIds = getGroupListIds(toEl);
                    const descriptors = getUnifiedDescriptors();
                    await safeInvoke("OnNavListMovedMixed", listId, "", toGroup, fromIds, toIds, descriptors);
                }

                checkPendingReinit();
            },

            onUnchoose: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            },

            onCancel: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            }
        });
    }

    // ── Gruppen-Listen-Sortables ───────────────────────────────────────────────
    // Verwalten die Listen innerhalb einer Gruppe.
    // Gruppen-Cards (.nav-group-card) dürfen NICHT in Gruppen-Listen-Hosts gezogen werden.

    function initSplitRootSortable() {
        if (_unifiedHost) return;

        _rootHost = document.getElementById("nav-root-lists-host");
        if (!_rootHost) return;

        _rootSortable = new Sortable(_rootHost, {
            group: {
                name: "navlists",
                pull: true,
                put: function (to, from, dragEl) {
                    return !dragEl.classList.contains('nav-group-card');
                }
            },
            animation: 150,
            draggable: ".nav-list-item",
            handle: ".nav-list-handle, .nav-drag-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            fallbackOnBody: true,
            swapThreshold: 1.0,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            delay: 80,
            delayOnTouchOnly: true,

            onStart: () => {
                _isDragging = true;
                _dragLastY = null;
                document.addEventListener('pointermove', _onDragPointerMove, { passive: true });
                startDragWatchdog();
                suppressNavClicksFor(500);
                safeInvoke("OnNavDragStarted");
            },

            onEnd: async (evt) => {
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                clearDragWatchdog();
                suppressNavClicksFor(300);
                normalizeDropPosition(_rootHost, evt.item, ".nav-list-item", evt);
                _dragLastY = null;
                cleanupSortableGhosts();

                const listId = (evt.item?.getAttribute("data-listid") || "").trim();
                if (!listId) {
                    await safeInvoke("OnNavDragAborted");
                    checkPendingReinit();
                    return;
                }

                if (evt.from === _rootHost && evt.to === _rootHost) {
                    await safeInvoke("OnNavListsReordered", "", getRootListIds(_rootHost));
                } else {
                    const fromGroup = (evt.from?.getAttribute("data-groupid") || "").trim();
                    await safeInvoke(
                        "OnNavListMovedMixed",
                        listId,
                        fromGroup,
                        "",
                        getGroupListIds(evt.from),
                        getRootListIds(_rootHost),
                        getUnifiedDescriptors());
                }

                checkPendingReinit();
            },

            onUnchoose: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            },

            onCancel: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            }
        });
    }

    function initSplitGroupsSortable() {
        if (_unifiedHost) return;

        _groupsHost = document.getElementById("nav-groups-host");
        if (!_groupsHost) return;

        _groupsSortable = new Sortable(_groupsHost, {
            animation: 150,
            draggable: ".nav-group-card",
            handle: ".nav-group-handle, .nav-drag-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            fallbackOnBody: true,
            swapThreshold: 1.0,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            delay: 80,
            delayOnTouchOnly: true,

            onStart: () => {
                _isDragging = true;
                _dragLastY = null;
                document.addEventListener('pointermove', _onDragPointerMove, { passive: true });
                startDragWatchdog();
                suppressNavClicksFor(500);
                safeInvoke("OnNavDragStarted");
            },

            onEnd: async (evt) => {
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                clearDragWatchdog();
                suppressNavClicksFor(300);
                normalizeDropPosition(_groupsHost, evt.item, ".nav-group-card", evt);
                _dragLastY = null;
                cleanupSortableGhosts();
                await safeInvoke("OnNavGroupsReordered", getGroupIds(_groupsHost));
                checkPendingReinit();
            },

            onUnchoose: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            },

            onCancel: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            }
        });
    }

    function createListSortable(hostEl) {
        const canManageHost = hostEl.dataset.canmanage !== "false";
        return new Sortable(hostEl, {
            group: {
                name: "navlists",
                pull: canManageHost,
                put: function (to, from, dragEl) {
                    // Keine Gruppen-Cards in Gruppen-Listen-Hosts
                    return canManageHost && !dragEl.classList.contains('nav-group-card');
                }
            },
            animation: 150,
            draggable: ".nav-list-item",
            handle: ".nav-drag-handle, .nav-list-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            fallbackOnBody: true,
            swapThreshold: 1.0,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            delay: 80,
            delayOnTouchOnly: true,

            onStart: () => {
                _isDragging = true;
                _dragLastY = null;
                document.addEventListener('pointermove', _onDragPointerMove, { passive: true });
                startDragWatchdog();
                suppressNavClicksFor(500);
                safeInvoke("OnNavDragStarted");
            },

            // Wenn ein Item aus dem unified host in eine Gruppe gezogen wird:
            // .nav-mixed-item entfernen (es ist jetzt ein Gruppen-Listen-Item).
            onAdd: (evt) => {
                if (evt.item && evt.item.classList.contains('nav-mixed-item')) {
                    evt.item.classList.remove('nav-mixed-item');
                }
            },

            onEnd: async (evt) => {
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                clearDragWatchdog();
                suppressNavClicksFor(300);

                // Off-by-one-Korrektur: bei forceFallback kann das Element
                // auf Index 1 landen, obwohl der Cursor über Position 0 war.
                const hostEl = evt.to;
                normalizeDropPosition(hostEl, evt.item, "[data-listid]", evt);
                _dragLastY = null;

                cleanupSortableGhosts();

                const item = evt.item;
                const listId = (item?.getAttribute("data-listid") || "").trim();
                if (!listId) {
                    await safeInvoke("OnNavDragAborted");
                    checkPendingReinit();
                    return;
                }

                const fromGroup = (evt.from?.getAttribute("data-groupid") || "").trim();
                const toGroup = (evt.to?.getAttribute("data-groupid") || "").trim();

                if (fromGroup === toGroup) {
                    // Innerhalb derselben Gruppe umsortiert – keine Änderung an unified host
                    await safeInvoke("OnNavListsReordered", toGroup, getGroupListIds(evt.to));
                } else if (toGroup === "" || evt.to === _unifiedHost || evt.to === _rootHost) {
                    // Gruppe → Root: unified descriptors mitschicken, damit globale
                    // Positionen nach MoveListAsync/ApplyListOrderAsync korrekt bleiben.
                    const fromIds = getGroupListIds(evt.from);
                    const toIds = getRootListIds(evt.to === _rootHost ? _rootHost : _unifiedHost);
                    const descriptors = getUnifiedDescriptors();
                    await safeInvoke("OnNavListMovedMixed", listId, fromGroup, "", fromIds, toIds, descriptors);
                } else {
                    // Gruppe → andere Gruppe: unified host ändert sich nicht,
                    // kein Bedarf für globale Reorder.
                    const fromIds = getGroupListIds(evt.from);
                    const toIds = getGroupListIds(evt.to);
                    await safeInvoke("OnNavListMoved", listId, fromGroup, toGroup, fromIds, toIds);
                }

                checkPendingReinit();
            },

            onUnchoose: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            },

            onCancel: () => {
                if (!_isDragging) return;
                _isDragging = false;
                document.removeEventListener('pointermove', _onDragPointerMove);
                _dragLastY = null;
                clearDragWatchdog();
                safeInvoke("OnNavDragAborted");
                checkPendingReinit();
            }
        });
    }

    function initGroupListsSortables() {
        const hosts = document.querySelectorAll(".nav-group-lists-host[data-groupid]");
        hosts.forEach(h => {
            _groupListSortables.push(createListSortable(h));
        });
    }

    // ── Öffentliche API ────────────────────────────────────────────────────────

    window.todoUi.initNavSortable = function (dotNetRef) {
        if (!window.Sortable) {
            console.error("SortableJS ist nicht geladen. Bitte SortableJS als <script> einbinden.");
            return;
        }

        if (_isDragging) {
            _dotNet = dotNetRef;
            _reinitPending = true;
            return;
        }

        destroyAll();
        _dotNet = dotNetRef;

        document.addEventListener("click", onDocumentClickCapture, true);
        initUnifiedHostSortable();
        initSplitRootSortable();
        initSplitGroupsSortable();
        initGroupListsSortables();
    };

    window.todoUi.disposeNavSortable = function () {
        if (_isDragging) {
            _reinitPending = true;
            return;
        }
        destroyAll();
    };

    // ── MAUI / PWA: App-Vordergrund-Erkennung ─────────────────────────────────
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "visible" && _isDragging) {
            forceResetDragging();
        }
    });

    // ── Circuit reconnect detection ────────────────────────────────────────────
    (function setupCircuitReconnectHandler() {
        let wasDisconnected = false;

        function onReconnectStateChanged(event) {
            const state = event.detail?.state;
            if (state === 'show') {
                wasDisconnected = true;
            } else if (state === 'hide' && wasDisconnected) {
                wasDisconnected = false;
                if (!_isDragging && _dotNet) {
                    safeInvoke("OnCircuitReconnected");
                }
            }
        }

        function attachListener() {
            const modal = document.getElementById('components-reconnect-modal');
            if (modal) {
                modal.addEventListener('components-reconnect-state-changed', onReconnectStateChanged);
            }
        }

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', attachListener, { once: true });
        } else {
            attachListener();
        }
    })();
})();
