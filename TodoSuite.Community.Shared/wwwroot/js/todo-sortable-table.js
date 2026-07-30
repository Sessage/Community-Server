window.todoUi = window.todoUi || {};

(function () {
    const instances = new WeakMap();

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
            onEnd: async () => {
                if (!dotNetRef) return;
                try {
                    await dotNetRef.invokeMethodAsync("OnTableTasksReordered", readTaskIds(host));
                } catch (e) {
                    console.error("[table tasks] DotNet invoke failed", e);
                }
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
