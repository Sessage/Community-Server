window.todoUi = window.todoUi || {};

(function () {
    const instances = new Map();

    async function safeInvoke(dotNetRef, methodName, ...args) {
        try {
            await dotNetRef.invokeMethodAsync(methodName, ...args);
        } catch { }
    }

    function getOrder(el) {
        if (!el) return [];

        return Array.from(el.querySelectorAll("[data-form-field-id]"))
            .map(x => x.getAttribute("data-form-field-id"))
            .filter(Boolean);
    }

    window.todoUi.initFormDesignerSortable = function (el, dotNetRef) {
        if (!el || !window.Sortable || instances.has(el)) return;

        const sortable = new Sortable(el, {
            animation: 150,
            handle: "[data-form-field-handle]",
            draggable: "[data-form-field-id]",
            ghostClass: "sortable-ghost",
            chosenClass: "sortable-chosen",
            onStart: function () {
                safeInvoke(dotNetRef, "SetFormDesignerDragging", true);
            },
            onEnd: async function () {
                await safeInvoke(dotNetRef, "OnFormDesignerDragEnded", getOrder(el));
            },
            onUnchoose: function () {
                safeInvoke(dotNetRef, "SetFormDesignerDragging", false);
            }
        });

        instances.set(el, sortable);
    };

    window.todoUi.disposeFormDesignerSortable = function (el) {
        const sortable = instances.get(el);
        if (!sortable) return;

        try { sortable.destroy(); } catch { }
        instances.delete(el);
    };

    window.todoUi.getFormDesignerOrder = function (el) {
        return getOrder(el);
    };
})();
