(function () {
    "use strict";

    window.todoUi = window.todoUi || {};
    const instances = new WeakMap();

    function finishInteraction(instance, event, commit) {
        const active = instance.active;
        if (!active || (event && event.pointerId !== active.pointerId)) return;

        instance.active = null;
        // The bar position and width are rendered as inline styles by Blazor.
        // Removing `width` here also removed the authoritative rendered width on
        // an ordinary click. Blazor does not necessarily write an unchanged
        // attribute again, so the bar then collapsed to its CSS minimum while
        // the persisted dates remained correct. Restore the exact pre-gesture
        // values instead of mutating Blazor-owned DOM state permanently.
        if (active.initialInlineTransform) {
            active.shell.style.transform = active.initialInlineTransform;
        } else {
            active.shell.style.removeProperty("transform");
        }
        if (active.initialInlineWidth) {
            active.shell.style.width = active.initialInlineWidth;
        } else {
            active.shell.style.removeProperty("width");
        }
        active.shell.classList.remove("timeline-bar-shell--dragging");

        if (active.moved) {
            instance.suppressClickUntil = performance.now() + 350;
        }

        const complete = async () => {
            try {
                if (commit && active.moved && active.deltaDays !== 0) {
                    await instance.dotNetRef.invokeMethodAsync(
                        "OnTimelineDateChanged",
                        active.taskId,
                        active.mode,
                        active.deltaDays);
                }
            } finally {
                if (active.moved) {
                    try { await instance.dotNetRef.invokeMethodAsync("SetTimelineDragging", false); } catch (_) { }
                }
            }
        };
        void complete();
    }

    function applyPreview(active) {
        const deltaPixels = active.deltaDays * active.pixelsPerDay;
        if (active.mode === "resize-start") {
            active.shell.style.transform = `translateX(${deltaPixels}px)`;
            active.shell.style.width = `${Math.max(active.pixelsPerDay, active.initialWidth - deltaPixels)}px`;
        } else if (active.mode === "resize-end") {
            active.shell.style.width = `${Math.max(active.pixelsPerDay, active.initialWidth + deltaPixels)}px`;
        } else {
            active.shell.style.transform = `translateX(${deltaPixels}px)`;
        }
    }

    window.todoUi.initTimeline = function (host, dotNetRef, pixelsPerDay) {
        window.todoUi.disposeTimeline(host);
        if (!host || !dotNetRef || !Number.isFinite(pixelsPerDay) || pixelsPerDay <= 0) return;

        const instance = {
            host,
            dotNetRef,
            pixelsPerDay,
            active: null,
            suppressClickUntil: 0
        };

        instance.onPointerDown = event => {
            if (event.button !== 0 || instance.active) return;
            const interaction = event.target.closest("[data-timeline-interaction]");
            if (!interaction || !host.contains(interaction)) return;
            const shell = interaction.closest("[data-timeline-bar]");
            if (!shell) return;

            const taskId = shell.dataset.taskId;
            const mode = interaction.dataset.timelineInteraction;
            if (!taskId || !mode) return;

            const durationDays = Math.max(0, Number.parseInt(shell.dataset.durationDays || "0", 10) || 0);
            instance.active = {
                pointerId: event.pointerId,
                taskId,
                mode,
                shell,
                startX: event.clientX,
                initialWidth: shell.getBoundingClientRect().width,
                initialInlineWidth: shell.style.width,
                initialInlineTransform: shell.style.transform,
                pixelsPerDay: instance.pixelsPerDay,
                durationDays,
                deltaDays: 0,
                moved: false
            };
            try { interaction.setPointerCapture(event.pointerId); } catch (_) { }
        };

        instance.onPointerMove = event => {
            const active = instance.active;
            if (!active || event.pointerId !== active.pointerId) return;

            const movement = event.clientX - active.startX;
            // Pointer devices commonly jitter a few pixels during a regular click.
            // Never interpret that as a date mutation, especially on the compact
            // month scale where a single day is only a few pixels wide.
            if (!active.moved && Math.abs(movement) < 6) return;
            if (!active.moved) {
                active.moved = true;
                active.shell.classList.add("timeline-bar-shell--dragging");
                void dotNetRef.invokeMethodAsync("SetTimelineDragging", true);
            }

            let deltaDays = Math.round(movement / active.pixelsPerDay);
            if (active.mode === "resize-start") deltaDays = Math.min(deltaDays, active.durationDays);
            if (active.mode === "resize-end") deltaDays = Math.max(deltaDays, -active.durationDays);
            if (deltaDays === active.deltaDays) return;

            active.deltaDays = deltaDays;
            applyPreview(active);
            event.preventDefault();
        };

        instance.onPointerUp = event => finishInteraction(instance, event, true);
        instance.onPointerCancel = event => finishInteraction(instance, event, false);
        instance.onClickCapture = event => {
            if (performance.now() < instance.suppressClickUntil) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }
        };

        host.addEventListener("pointerdown", instance.onPointerDown);
        host.addEventListener("pointermove", instance.onPointerMove, { passive: false });
        host.addEventListener("pointerup", instance.onPointerUp);
        host.addEventListener("pointercancel", instance.onPointerCancel);
        host.addEventListener("click", instance.onClickCapture, true);
        instances.set(host, instance);
    };

    window.todoUi.centerTimelineToday = function (host, todayLeft) {
        if (!host || !Number.isFinite(todayLeft)) return false;
        const sidebar = host.querySelector(".timeline-meta");
        const sidebarWidth = sidebar ? sidebar.getBoundingClientRect().width : 0;
        const visibleTimelineWidth = Math.max(0, host.clientWidth - sidebarWidth);
        if (visibleTimelineWidth <= 0) return false;

        const target = Math.max(0, todayLeft - visibleTimelineWidth / 2);
        if (typeof host.scrollTo === "function") {
            host.scrollTo({ left: target, behavior: "smooth" });
        } else {
            host.scrollLeft = target;
        }

        for (const line of host.querySelectorAll(".timeline-today-line")) {
            line.classList.remove("timeline-today-line--highlight");
            void line.offsetWidth;
            line.classList.add("timeline-today-line--highlight");
        }
        return true;
    };

    window.todoUi.disposeTimeline = function (host) {
        if (!host) return;
        const instance = instances.get(host);
        if (!instance) return;

        if (instance.active) finishInteraction(instance, null, false);
        host.removeEventListener("pointerdown", instance.onPointerDown);
        host.removeEventListener("pointermove", instance.onPointerMove);
        host.removeEventListener("pointerup", instance.onPointerUp);
        host.removeEventListener("pointercancel", instance.onPointerCancel);
        host.removeEventListener("click", instance.onClickCapture, true);
        instances.delete(host);
    };
})();
