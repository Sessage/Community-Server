(function () {
    window.todoUi = window.todoUi || {};

    let resizeObserver = null;
    let animationFrame = 0;
    let targetOrigin = null;

    function disposeResizeObserver() {
        if (resizeObserver) {
            resizeObserver.disconnect();
            resizeObserver = null;
        }

        if (animationFrame) {
            cancelAnimationFrame(animationFrame);
            animationFrame = 0;
        }

        targetOrigin = null;
    }

    function postHeight() {
        animationFrame = 0;
        if (!targetOrigin || window.parent === window) return;

        const body = document.body;
        const root = document.documentElement;
        const height = Math.ceil(Math.max(
            body ? body.scrollHeight : 0,
            body ? body.offsetHeight : 0,
            root ? root.scrollHeight : 0,
            root ? root.offsetHeight : 0));

        if (!Number.isFinite(height) || height < 1) return;

        window.parent.postMessage({
            type: "sessage:form-height",
            version: 1,
            height: height
        }, targetOrigin);
    }

    function scheduleHeight() {
        if (animationFrame) return;
        animationFrame = requestAnimationFrame(postHeight);
    }

    window.todoUi.publicFormAccessibility = {
        initialize: function () {
            disposeResizeObserver();
            if (window.parent === window || !document.referrer) return;

            try {
                const referrer = new URL(document.referrer);
                if (referrer.origin === "null") return;
                targetOrigin = referrer.origin;
            } catch {
                return;
            }

            if (typeof ResizeObserver === "function") {
                resizeObserver = new ResizeObserver(scheduleHeight);
                if (document.body) resizeObserver.observe(document.body);
                if (document.documentElement) resizeObserver.observe(document.documentElement);
            }
            scheduleHeight();
        },

        dispose: disposeResizeObserver,

        focusElementById: function (id) {
            if (typeof id !== "string" || !id) return;
            const element = document.getElementById(id);
            if (!element || typeof element.focus !== "function") return;
            try {
                element.focus({ preventScroll: true });
            } catch {
                element.focus();
            }
            element.scrollIntoView({ block: "center", behavior: "smooth" });
        }
    };
})();
