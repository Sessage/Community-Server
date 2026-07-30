// wwwroot/js/todo-sortable-dashboard.js
// Requires SortableJS global "Sortable"

window.todoUi = window.todoUi || {};

(function () {
    let _sortable = null;

    /**
     * Initializes (or re-initializes) SortableJS on the dashboard widget container.
     * @param {HTMLElement} containerEl  - The container holding [data-widget-id] children.
     * @param {object}      dotNetRef    - DotNetObjectReference for Blazor callbacks.
     */
    window.todoUi.initDashboardSortable = function (containerEl, dotNetRef) {
        if (!containerEl) return;

        if (_sortable) {
            _sortable.destroy();
            _sortable = null;
        }

        _sortable = new Sortable(containerEl, {
            animation: 150,
            handle: '.dashboard-drag-handle',
            ghostClass: 'dashboard-widget-ghost',
            chosenClass: 'dashboard-widget-chosen',
            dragClass: 'dashboard-widget-drag',
            forceFallback: false,
            onEnd: function () {
                const items = containerEl.querySelectorAll('[data-widget-id]');
                const order = Array.from(items).map(el => el.dataset.widgetId);
                dotNetRef.invokeMethodAsync('OnWidgetOrderChanged', order)
                    .catch(function (err) { console.warn('dashboard sortable callback failed', err); });
            }
        });
    };

    /**
     * Destroys the active SortableJS dashboard instance.
     */
    window.todoUi.disposeDashboardSortable = function () {
        if (_sortable) {
            _sortable.destroy();
            _sortable = null;
        }
    };
})();
