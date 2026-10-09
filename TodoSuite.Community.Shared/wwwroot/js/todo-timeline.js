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

// Document-ready timeline export: draw the complete projection, independent of scroll position.
(() => {
    const ui = window.todoUi = window.todoUi || {};
    ui.openTimelineExport = dialog => { if (!dialog.open) dialog.showModal(); };
    ui.closeTimelineExport = dialog => dialog.close();
    ui.renderTimelineExport = (canvas, model) => {
        const width = 1000, scale = model.pixelWidth / width, rowHeight = 44, top = 122;
        const height = 160 + model.rows.length * rowHeight;
        const pixelHeight = Math.ceil(height * scale);
        if (model.pixelWidth * pixelHeight > 40000000 || pixelHeight > 16384) throw new Error('Image too large');
        canvas.width = model.pixelWidth;
        canvas.height = pixelHeight;
        const ctx = canvas.getContext('2d');
        if (!ctx) throw new Error('Canvas unavailable');
        ctx.scale(scale, scale);
        if (!model.transparentBackground) { ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, width, height); }
        const titleWidth = 260;
        const assigneeWidth = model.showAssignees ? 130 : 0;
        const chartX = 20 + titleWidth + assigneeWidth;
        const chartWidth = width - chartX - 20;
        const dayWidth = chartWidth / model.days;
        const text = (value, x, y, maxWidth, color = '#334155', font = '14px Arial, sans-serif') => {
            ctx.font = font; ctx.fillStyle = color;
            value = String(value || '').replace(/[\r\n\t]/g, ' ');
            if (ctx.measureText(value).width > maxWidth) {
                while (value.length && ctx.measureText(value + '…').width > maxWidth) value = value.slice(0, -1);
                value += '…';
            }
            ctx.fillText(value, x, y);
        };
        text(model.title, 20, 36, width - 40, '#0f172a', 'bold 22px Arial, sans-serif');
        text(model.subtitle + (model.pageCount > 1 ? ` · ${model.pageNumber} / ${model.pageCount}` : ''), 20, 60, width - 40, '#64748b', '12px Arial, sans-serif');
        if (!model.transparentBackground) { ctx.fillStyle = '#f1f5f9'; ctx.fillRect(20, 88, width - 40, 34); }
        text(model.taskLabel, 30, 110, titleWidth - 20, '#334155', 'bold 11px Arial, sans-serif');
        if (model.showAssignees) text(model.assigneeLabel, 20 + titleWidth + 6, 110, assigneeWidth - 12, '#334155', 'bold 11px Arial, sans-serif');
        model.rows.forEach((row, index) => {
            const y = top + index * rowHeight;
            if (!model.transparentBackground) {
                ctx.fillStyle = row.group ? '#e2e8f0' : index % 2 ? '#f8fafc' : '#ffffff';
                ctx.fillRect(20, y, width - 40, rowHeight);
            }
        });
        if (model.showWeekends && dayWidth >= 3) {
            ctx.fillStyle = '#94a3b81a';
            for (let day = 0; day < model.days; day++) {
                const weekday = (model.startWeekday + day) % 7;
                if (weekday === 0 || weekday === 6) ctx.fillRect(chartX + day * dayWidth, top, dayWidth, model.rows.length * rowHeight);
            }
        }
        ctx.strokeStyle = '#e2e8f0'; ctx.lineWidth = .5;
        const labelStride = Math.max(1, Math.ceil(65 / (chartWidth / model.ticks.length)));
        model.ticks.forEach((tick, index) => {
            const x = chartX + tick.start * dayWidth;
            ctx.beginPath(); ctx.moveTo(x, 88); ctx.lineTo(x, height - 38); ctx.stroke();
            if (index % labelStride === 0) {
                const last = model.ticks[Math.min(model.ticks.length - 1, index + labelStride - 1)];
                if ((last.end - tick.start) * dayWidth >= 40)
                    text(tick.label, x + 4, 110, (last.end - tick.start) * dayWidth - 8, '#475569', '11px Arial, sans-serif');
            }
        });
        model.rows.forEach((row, index) => {
            const y = top + index * rowHeight;
            text(row.title, 30, y + (model.showDates && !row.group ? 19 : 27), row.group ? width - 60 : titleWidth - 20, row.done ? '#64748b' : '#0f172a', row.group ? 'bold 14px Arial, sans-serif' : '14px Arial, sans-serif');
            if (row.group) return;
            if (model.showDates) text(row.dates, 30, y + 35, titleWidth - 20, '#64748b', '10px Arial, sans-serif');
            if (model.showAssignees) text(row.assignee, 20 + titleWidth + 6, y + 27, assigneeWidth - 12, '#475569', '11px Arial, sans-serif');
            ctx.save(); ctx.beginPath(); ctx.rect(chartX, y, chartWidth, rowHeight); ctx.clip();
            ctx.fillStyle = row.color;
            if (row.milestone) {
                const x = chartX + (row.start + .5) * dayWidth;
                ctx.beginPath(); ctx.moveTo(x, y + 12); ctx.lineTo(x + 10, y + 22); ctx.lineTo(x, y + 32); ctx.lineTo(x - 10, y + 22); ctx.closePath(); ctx.fill();
            } else {
                const x = chartX + row.start * dayWidth, barWidth = Math.max(2, (row.end - row.start) * dayWidth);
                ctx.beginPath(); ctx.roundRect(x, y + 11, barWidth, 22, 4); ctx.fill();
                if (barWidth > 70) text(row.title, x + 6, y + 26, barWidth - 12, '#ffffff', '10px Arial, sans-serif');
            }
            ctx.restore();
            ctx.strokeStyle = '#e2e8f0'; ctx.beginPath(); ctx.moveTo(20, y + rowHeight); ctx.lineTo(width - 20, y + rowHeight); ctx.stroke();
        });
        if (model.showToday && model.today >= 0 && model.today < model.days) {
            const x = chartX + (model.today + .5) * dayWidth;
            ctx.strokeStyle = '#dc2626'; ctx.lineWidth = 1.5; ctx.setLineDash([4, 3]);
            ctx.beginPath(); ctx.moveTo(x, 122); ctx.lineTo(x, height - 38); ctx.stroke(); ctx.setLineDash([]);
            text(model.todayLabel, Math.min(x + 4, width - 65), height - 16, 60, '#dc2626', '10px Arial, sans-serif');
        }
        return canvas.height;
    };
    ui.downloadTimelineExport = async (canvas, filename) => {
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        if (!blob) throw new Error('PNG encoding failed');
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url; link.download = filename.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').slice(0, 220).replace(/\.png$/i, '') + '.png';
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 30000);
    };
})();

(() => {
    const ui = window.todoUi;
    const safeName = name => name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').slice(0, 180).replace(/\.png$/i, '');
    const save = (blob, filename) => {
        const url = URL.createObjectURL(blob), link = document.createElement('a');
        link.href = url; link.download = filename;
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 30000);
    };
    // ZIP STORE keeps already compressed PNGs intact and needs no external libraries.
    const crcTable = Array.from({ length: 256 }, (_, value) => {
        for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
        return value >>> 0;
    });
    const crc32 = bytes => {
        let crc = 0xffffffff;
        for (const byte of bytes) crc = crcTable[(crc ^ byte) & 255] ^ (crc >>> 8);
        return (crc ^ 0xffffffff) >>> 0;
    };
    ui.downloadTimelineExportPages = async (models, filename) => {
        if (!models.length || models.length > 100) throw new Error('Invalid image count');
        const base = safeName(filename), canvas = document.createElement('canvas');
        const locals = [], central = [];
        let offset = 0, centralSize = 0;
        const now = new Date();
        const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | (now.getSeconds() >> 1);
        const dosDate = ((Math.max(1980, Math.min(2107, now.getFullYear())) - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
        try {
            for (const model of models) {
                ui.renderTimelineExport(canvas, model);
                const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
                if (!blob) throw new Error('PNG encoding failed');
                const name = base + (model.pageCount > 1 ? `-${String(model.pageNumber).padStart(3, '0')}` : '') + '.png';
                if (models.length === 1) { save(blob, name); return; }
                if (offset + blob.size > 200000000) throw new Error('Archive too large');
                const bytes = new Uint8Array(await blob.arrayBuffer()), encodedName = new TextEncoder().encode(name), crc = crc32(bytes);
                const local = new Uint8Array(30 + encodedName.length), l = new DataView(local.buffer);
                l.setUint32(0, 0x04034b50, true); l.setUint16(4, 20, true); l.setUint16(6, 0x800, true);
                l.setUint16(10, dosTime, true); l.setUint16(12, dosDate, true); l.setUint32(14, crc, true);
                l.setUint32(18, bytes.length, true); l.setUint32(22, bytes.length, true); l.setUint16(26, encodedName.length, true);
                local.set(encodedName, 30); locals.push(local, bytes);
                const directory = new Uint8Array(46 + encodedName.length), d = new DataView(directory.buffer);
                d.setUint32(0, 0x02014b50, true); d.setUint16(4, 20, true); d.setUint16(6, 20, true); d.setUint16(8, 0x800, true);
                d.setUint16(12, dosTime, true); d.setUint16(14, dosDate, true); d.setUint32(16, crc, true);
                d.setUint32(20, bytes.length, true); d.setUint32(24, bytes.length, true); d.setUint16(28, encodedName.length, true);
                d.setUint32(42, offset, true); directory.set(encodedName, 46);
                central.push(directory); centralSize += directory.length; offset += local.length + bytes.length;
            }
            const end = new Uint8Array(22), e = new DataView(end.buffer);
            e.setUint32(0, 0x06054b50, true); e.setUint16(8, models.length, true); e.setUint16(10, models.length, true);
            e.setUint32(12, centralSize, true); e.setUint32(16, offset, true);
            save(new Blob([...locals, ...central, end], { type: 'application/zip' }), base + '.zip');
        } finally { canvas.width = 1; canvas.height = 1; }
    };
})();