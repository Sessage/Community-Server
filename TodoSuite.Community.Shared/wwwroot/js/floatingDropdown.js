/**
 * floatingDropdown.js
 *
 * JS helpers for the FloatingDropdown Blazor component.
 * Provides:
 *   - getPosition(element)          – reads getBoundingClientRect + viewport height
 *   - registerCloseHandlers(id, ref) – outside-click + ESC close detection
 *   - unregisterCloseHandlers(id)   – cleanup
 *   - updatePosition(element)       – optional live repositioning
 */
window.floatingDropdown = (function () {
    'use strict';

    // Map<string, { onDocClick, onKeyDown }> keyed by overlay ID
    const activeHandlers = new Map();

    /**
     * Returns the bounding rect of an element plus the viewport size.
     * Used by FloatingDropdown.razor to position the panel.
     */
    function getPosition(element) {
        if (!element) return null;
        const rect = element.getBoundingClientRect();
        return {
            top: rect.top,
            bottom: rect.bottom,
            left: rect.left,
            right: rect.right,
            width: rect.width,
            height: rect.height,
            viewportHeight: window.innerHeight,
            viewportWidth: window.innerWidth
        };
    }

    /**
     * Registers document-level click and keydown listeners that tell Blazor
     * to close the dropdown when the user clicks outside or presses ESC.
     *
     * Uses a short setTimeout so the click that *opened* the dropdown does not
     * immediately re-trigger the close handler.
     *
     * Clicks inside an element with class "floating-dropdown-panel" are ignored,
     * as are clicks on the anchor element itself (ToggleAsync handles those).
     */
    function registerCloseHandlers(dropdownId, dotnetRef, anchorElement) {
        // Remove any stale handler with the same ID
        unregisterCloseHandlers(dropdownId);

        setTimeout(function () {
            var onDocClick = function (e) {
                // Ignore clicks inside the floating panel itself
                if (e.target && e.target.closest && e.target.closest('.floating-dropdown-panel')) {
                    return;
                }
                // Ignore clicks on the trigger anchor – ToggleAsync handles those
                if (anchorElement && anchorElement.contains(e.target)) {
                    return;
                }
                dotnetRef.invokeMethodAsync('OnOutsideClick');
            };

            var onKeyDown = function (e) {
                if (e.key === 'Escape') {
                    dotnetRef.invokeMethodAsync('OnEscapeKey');
                }
            };

            document.addEventListener('click', onDocClick, true);
            document.addEventListener('keydown', onKeyDown, true);

            activeHandlers.set(dropdownId, { onDocClick: onDocClick, onKeyDown: onKeyDown });
        }, 100);
    }

    /**
     * Removes previously registered close handlers for a given overlay ID.
     */
    function unregisterCloseHandlers(dropdownId) {
        var handlers = activeHandlers.get(dropdownId);
        if (!handlers) return;

        document.removeEventListener('click', handlers.onDocClick, true);
        document.removeEventListener('keydown', handlers.onKeyDown, true);
        activeHandlers.delete(dropdownId);
    }

    return {
        getPosition: getPosition,
        registerCloseHandlers: registerCloseHandlers,
        unregisterCloseHandlers: unregisterCloseHandlers
    };
})();
