/**
 * imageCropper.js
 * Einfacher Canvas-basierter quadratischer Bildzuschnitt (128×128 px Output).
 * Wird über Blazor JS-Interop gesteuert.
 */
window.imageCropper = (() => {
    // State pro Instanz (wir unterstützen nur eine Instanz gleichzeitig)
    let state = null;

    /**
     * Initialisiert den Cropper.
     * @param {string} canvasId - ID des Haupt-Canvas-Elements (Vorschau + Auswahl)
     * @param {string} fileInputId - ID des file-Input-Elements
     * @param {string} previewCanvasId - ID des Preview-Canvas (128×128)
     * @param {object} dotNetHelper - DotNet-Objekt für Callbacks
     */
    function init(canvasId, fileInputId, previewCanvasId, dotNetHelper) {
        cleanup();

        const canvas = document.getElementById(canvasId);
        const fileInput = document.getElementById(fileInputId);
        const previewCanvas = document.getElementById(previewCanvasId);

        if (!canvas || !fileInput || !previewCanvas) return;

        const ctx = canvas.getContext('2d');
        const previewCtx = previewCanvas.getContext('2d');

        state = {
            canvas, ctx, fileInput, previewCanvas, previewCtx, dotNetHelper,
            image: null,
            // Bild-Rendering-Bereich auf dem Canvas
            imgX: 0, imgY: 0, imgW: 0, imgH: 0,
            // Auswahl-Quadrat (Koordinaten auf dem Canvas)
            sel: { x: 0, y: 0, size: 0 },
            dragging: false, resizing: false,
            dragStart: null,
            // Welcher Eckpunkt wird gerade gezogen? 'tl','tr','bl','br' oder null
            resizeHandle: null,
            handleSize: 16,
        };

        fileInput.addEventListener('change', onFileChange);
        canvas.addEventListener('mousedown', onMouseDown);
        canvas.addEventListener('mousemove', onMouseMove);
        canvas.addEventListener('mouseup', onMouseUp);
        canvas.addEventListener('mouseleave', onMouseUp);
        // Touch-Support
        canvas.addEventListener('touchstart', onTouchStart, { passive: false });
        canvas.addEventListener('touchmove', onTouchMove, { passive: false });
        canvas.addEventListener('touchend', onTouchEnd);
    }

    function cleanup() {
        if (!state) return;
        state.fileInput.removeEventListener('change', onFileChange);
        state.canvas.removeEventListener('mousedown', onMouseDown);
        state.canvas.removeEventListener('mousemove', onMouseMove);
        state.canvas.removeEventListener('mouseup', onMouseUp);
        state.canvas.removeEventListener('mouseleave', onMouseUp);
        state.canvas.removeEventListener('touchstart', onTouchStart);
        state.canvas.removeEventListener('touchmove', onTouchMove);
        state.canvas.removeEventListener('touchend', onTouchEnd);
        state = null;
    }

    function onFileChange(e) {
        const file = e.target.files[0];
        if (!file) return;
        if (!file.type.startsWith('image/')) {
            state.dotNetHelper.invokeMethodAsync('OnError', 'Bitte wähle eine Bilddatei aus.');
            return;
        }

        const reader = new FileReader();
        reader.onload = (ev) => {
            const img = new Image();
            img.onload = () => {
                state.image = img;
                layoutImage();
                initSelection();
                draw();
                updatePreview();
                state.dotNetHelper.invokeMethodAsync('OnImageLoaded');
            };
            img.src = ev.target.result;
        };
        reader.readAsDataURL(file);
    }

    /** Berechnet, wie das Bild zentriert/skaliert auf den Canvas passt (object-fit: contain). */
    function layoutImage() {
        if (!state.image) return;
        const cw = state.canvas.width;
        const ch = state.canvas.height;
        const iw = state.image.naturalWidth;
        const ih = state.image.naturalHeight;

        const scale = Math.min(cw / iw, ch / ih);
        state.imgW = iw * scale;
        state.imgH = ih * scale;
        state.imgX = (cw - state.imgW) / 2;
        state.imgY = (ch - state.imgH) / 2;
    }

    /** Initialisiert die Auswahl als möglichst großes Quadrat zentriert auf dem Bild. */
    function initSelection() {
        const size = Math.min(state.imgW, state.imgH);
        state.sel = {
            x: state.imgX + (state.imgW - size) / 2,
            y: state.imgY + (state.imgH - size) / 2,
            size,
        };
    }

    function draw() {
        if (!state.image) return;
        const { ctx, canvas, imgX, imgY, imgW, imgH, sel, handleSize } = state;
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // Bild zeichnen
        ctx.drawImage(state.image, imgX, imgY, imgW, imgH);

        // Abdunkelung außerhalb der Auswahl
        ctx.save();
        ctx.fillStyle = 'rgba(0,0,0,0.5)';
        ctx.beginPath();
        ctx.rect(0, 0, canvas.width, canvas.height);
        ctx.rect(sel.x, sel.y, sel.size, sel.size);
        ctx.fill('evenodd');
        ctx.restore();

        // Auswahl-Rahmen
        ctx.save();
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2;
        ctx.setLineDash([]);
        ctx.strokeRect(sel.x, sel.y, sel.size, sel.size);
        ctx.restore();

        // Raster (3×3)
        ctx.save();
        ctx.strokeStyle = 'rgba(255,255,255,0.4)';
        ctx.lineWidth = 1;
        const third = sel.size / 3;
        for (let i = 1; i < 3; i++) {
            ctx.beginPath();
            ctx.moveTo(sel.x + third * i, sel.y);
            ctx.lineTo(sel.x + third * i, sel.y + sel.size);
            ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(sel.x, sel.y + third * i);
            ctx.lineTo(sel.x + sel.size, sel.y + third * i);
            ctx.stroke();
        }
        ctx.restore();

        // Eck-Handles
        const hs = handleSize;
        ctx.save();
        ctx.fillStyle = '#ffffff';
        const handles = getHandles();
        handles.forEach(h => {
            ctx.fillRect(h.x - hs / 2, h.y - hs / 2, hs, hs);
        });
        ctx.restore();
    }

    function getHandles() {
        const { sel } = state;
        return [
            { id: 'tl', x: sel.x, y: sel.y },
            { id: 'tr', x: sel.x + sel.size, y: sel.y },
            { id: 'bl', x: sel.x, y: sel.y + sel.size },
            { id: 'br', x: sel.x + sel.size, y: sel.y + sel.size },
        ];
    }

    function hitHandle(x, y) {
        const hs = state.handleSize;
        return getHandles().find(h => Math.abs(x - h.x) < hs && Math.abs(y - h.y) < hs);
    }

    function hitSelection(x, y) {
        const { sel } = state;
        return x >= sel.x && x <= sel.x + sel.size && y >= sel.y && y <= sel.y + sel.size;
    }

    function canvasPos(e) {
        const rect = state.canvas.getBoundingClientRect();
        const scaleX = state.canvas.width / rect.width;
        const scaleY = state.canvas.height / rect.height;
        return {
            x: (e.clientX - rect.left) * scaleX,
            y: (e.clientY - rect.top) * scaleY,
        };
    }

    function onMouseDown(e) {
        if (!state.image) return;
        const pos = canvasPos(e);
        const handle = hitHandle(pos.x, pos.y);
        if (handle) {
            state.resizing = true;
            state.resizeHandle = handle.id;
            state.dragStart = pos;
        } else if (hitSelection(pos.x, pos.y)) {
            state.dragging = true;
            state.dragStart = { x: pos.x - state.sel.x, y: pos.y - state.sel.y };
        }
    }

    function onMouseMove(e) {
        if (!state.image) return;
        const pos = canvasPos(e);
        if (state.dragging) {
            moveSel(pos.x - state.dragStart.x, pos.y - state.dragStart.y);
            draw();
            updatePreview();
        } else if (state.resizing) {
            resizeSel(pos);
            draw();
            updatePreview();
        } else {
            // Cursor anpassen
            const handle = hitHandle(pos.x, pos.y);
            if (handle) {
                state.canvas.style.cursor = getCursorForHandle(handle.id);
            } else if (hitSelection(pos.x, pos.y)) {
                state.canvas.style.cursor = 'move';
            } else {
                state.canvas.style.cursor = 'default';
            }
        }
    }

    function onMouseUp() {
        state.dragging = false;
        state.resizing = false;
        state.resizeHandle = null;
    }

    function onTouchStart(e) {
        e.preventDefault();
        if (e.touches.length === 1) onMouseDown(e.touches[0]);
    }
    function onTouchMove(e) {
        e.preventDefault();
        if (e.touches.length === 1) onMouseMove(e.touches[0]);
    }
    function onTouchEnd(e) {
        e.preventDefault();
        onMouseUp();
    }

    function getCursorForHandle(id) {
        return { tl: 'nw-resize', tr: 'ne-resize', bl: 'sw-resize', br: 'se-resize' }[id] || 'default';
    }

    function moveSel(newX, newY) {
        const { imgX, imgY, imgW, imgH, sel } = state;
        sel.x = Math.max(imgX, Math.min(imgX + imgW - sel.size, newX));
        sel.y = Math.max(imgY, Math.min(imgY + imgH - sel.size, newY));
    }

    function resizeSel(pos) {
        const { imgX, imgY, imgW, imgH, sel, resizeHandle } = state;
        const minSize = 32;
        let { x, y, size } = sel;
        const right = x + size;
        const bottom = y + size;

        if (resizeHandle === 'br') {
            const newSize = Math.min(pos.x - x, pos.y - y, imgX + imgW - x, imgY + imgH - y);
            size = Math.max(minSize, newSize);
        } else if (resizeHandle === 'tl') {
            const maxSize = Math.min(right - imgX, bottom - imgY);
            const deltaX = right - pos.x;
            const deltaY = bottom - pos.y;
            const newSize = Math.min(deltaX, deltaY, maxSize);
            if (newSize >= minSize) {
                size = newSize;
                x = right - size;
                y = bottom - size;
            }
        } else if (resizeHandle === 'tr') {
            const newSize = Math.min(pos.x - x, bottom - pos.y, imgX + imgW - x, bottom - imgY);
            if (newSize >= minSize) {
                size = newSize;
                y = bottom - size;
            }
        } else if (resizeHandle === 'bl') {
            const newSize = Math.min(right - pos.x, pos.y - y, right - imgX, imgY + imgH - y);
            if (newSize >= minSize) {
                size = newSize;
                x = right - size;
            }
        }

        sel.x = x;
        sel.y = y;
        sel.size = size;
    }

    /** Aktualisiert die 128×128-Vorschau. */
    function updatePreview() {
        if (!state.image) return;
        const { previewCtx, previewCanvas, sel, imgX, imgY, imgW, imgH, image } = state;

        // Berechne den Ausschnitt im Originalbild
        const scaleX = image.naturalWidth / imgW;
        const scaleY = image.naturalHeight / imgH;
        const srcX = (sel.x - imgX) * scaleX;
        const srcY = (sel.y - imgY) * scaleY;
        const srcSize = sel.size * scaleX; // quadratisch, also scaleX ≈ scaleY

        previewCtx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
        previewCtx.drawImage(image, srcX, srcY, srcSize, srcSize * (scaleY / scaleX), 0, 0, 128, 128);
    }

    /**
     * Gibt das zugeschnittene Bild als JPEG-Data-URL zurück.
     * Wird vom Blazor-Component aufgerufen.
     * @returns {string} Data-URL
     */
    function getCroppedImageDataUrl() {
        if (!state?.image) return null;

        const offscreen = document.createElement('canvas');
        offscreen.width = 128;
        offscreen.height = 128;
        const offCtx = offscreen.getContext('2d');

        const { sel, imgX, imgY, imgW, imgH, image } = state;
        const scaleX = image.naturalWidth / imgW;
        const scaleY = image.naturalHeight / imgH;
        const srcX = (sel.x - imgX) * scaleX;
        const srcY = (sel.y - imgY) * scaleY;
        const srcSize = sel.size * scaleX;

        offCtx.drawImage(image, srcX, srcY, srcSize, srcSize * (scaleY / scaleX), 0, 0, 128, 128);

        // JPEG, Qualität 0.85
        return offscreen.toDataURL('image/jpeg', 0.85);
    }

    /** Öffnet den nativen Datei-Dialog. */
    function triggerFileInput(fileInputId) {
        const el = document.getElementById(fileInputId);
        if (el) el.click();
    }

    return { init, cleanup, getCroppedImageDataUrl, triggerFileInput };
})();
