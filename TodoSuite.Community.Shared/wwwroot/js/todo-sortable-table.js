window.todoUi = window.todoUi || {};

(function () {
    const instances = new WeakMap();
    // Modulweit monoton, damit ein Sortable-Reinit nicht wieder bei Token 1 beginnt
    // und dadurch vom bestehenden Blazor-Komponentenstatus als veraltet verworfen wird.
    let taskDragToken = 0;

    function readKeys(host) {
        return Array.from(host.children)
            .map(el => el.getAttribute("data-column-key"))
            .filter(Boolean);
    }

    function readTaskIds(host) {
        return Array.from(host.children)
            .filter(el => el.getAttribute("data-task-reorderable") === "true")
            .map(el => el.getAttribute("data-taskid"))
            .filter(Boolean);
    }

    function isFixedColumn(el) {
        return el?.getAttribute("data-sortable-column") !== "true";
    }

    window.todoUi.initSortableTableColumns = function (host, dotNetRef) {
        if (!host || !window.Sortable) return;

        if (instances.has(host)) {
            try { instances.get(host).destroy(); } catch { }
            instances.delete(host);
        }

        const sortable = new Sortable(host, {
            animation: 150,
            draggable: "[data-column-key][data-sortable-column='true']",
            handle: ".table-column-drag-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            filter: "button,input,select,textarea,a,[data-nodrag='true']",
            preventOnFilter: false,
            onMove: (evt) => !isFixedColumn(evt.related),
            onEnd: async () => {
                if (!dotNetRef) return;
                try {
                    await dotNetRef.invokeMethodAsync("OnTableColumnsReordered", readKeys(host));
                } catch (e) {
                    console.error("[table] DotNet invoke failed", e);
                }
            }
        });

        instances.set(host, sortable);
    };

    window.todoUi.disposeSortableTableColumns = function (host) {
        if (!host || !instances.has(host)) return;
        try { instances.get(host).destroy(); } catch { }
        instances.delete(host);
    };

    window.todoUi.initSortableTableTasks = function (host, dotNetRef) {
        if (!host || !window.Sortable) return;

        if (instances.has(host)) {
            try { instances.get(host).destroy(); } catch { }
            instances.delete(host);
        }

        let draggingActive = false;

        async function setDraggingAsync(value, token) {
            if (!dotNetRef) return;
            try {
                await dotNetRef.invokeMethodAsync("SetTableTaskDraggingWithToken", value, token);
            } catch {
                try { await dotNetRef.invokeMethodAsync("SetTableTaskDragging", value); } catch { }
            }
        }

        function cleanupDragVisuals() {
            try { window.todoUi.cleanupSortableGhosts?.(); } catch { }
        }

        async function cancelDragAsync() {
            if (!draggingActive) return;
            const token = taskDragToken;
            draggingActive = false;
            cleanupDragVisuals();
            await setDraggingAsync(false, token);
        }

        const sortable = new Sortable(host, {
            animation: 150,
            draggable: "[data-taskid][data-task-reorderable='true']",
            handle: ".table-task-drag-handle",
            forceFallback: true,
            fallbackTolerance: 3,
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            dragClass: "sortable-drag",
            filter: "button,input,select,textarea,a,[data-nodrag='true']",
            preventOnFilter: false,

            // Nicht async: SortableJS wartet den Promise eines Start-Handlers nicht ab.
            // Der Token verhindert, dass ein verspätetes true ein bereits verarbeitetes
            // Drag-Ende wieder überschreibt.
            onStart: function () {
                taskDragToken++;
                draggingActive = true;
                setDraggingAsync(true, taskDragToken);
            },

            onEnd: async () => {
                const ids = readTaskIds(host);
                if (!draggingActive) return;

                const token = taskDragToken;
                draggingActive = false;
                cleanupDragVisuals();

                if (!dotNetRef) return;
                try {
                    // Drag-Ende und DOM-Snapshot bewusst atomar an Blazor übergeben.
                    // Der Parent darf erst nach gesetztem optimistischem Stand rendern.
                    await dotNetRef.invokeMethodAsync("FinishTableTaskDragWithOrder", token, ids);
                } catch (e) {
                    try { await setDraggingAsync(false, token); } catch { }
                    console.error("[table tasks] DotNet invoke failed", e);
                }
            },

            onCancel: async () => {
                await cancelDragAsync();
            }
        });

        instances.set(host, sortable);
    };

    window.todoUi.disposeSortableTableTasks = function (host) {
        if (!host || !instances.has(host)) return;
        try { instances.get(host).destroy(); } catch { }
        instances.delete(host);
    };
})();
