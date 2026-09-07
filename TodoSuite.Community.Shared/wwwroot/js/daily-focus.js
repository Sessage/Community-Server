export function timeZone() { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; }

// Read the previous device-only plan once, so upgrading does not discard today's choices.
export function legacy(user, date) {
    try {
        const state = JSON.parse(localStorage.getItem(`todosuite.myday.${user}`));
        return state?.date === date && Array.isArray(state.ids)
            ? state.ids.filter(id => typeof id === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) : [];
    } catch { return []; }
}
export function clearLegacy(user) { localStorage.removeItem(`todosuite.myday.${user}`); }

export function watch(callback, resetsAtUtc) {
    let deadline = Date.parse(resetsAtUtc), timer, disposed = false, pending = false;
    const schedule = () => {
        clearTimeout(timer);
        if (!disposed) timer = setTimeout(refresh, deadline <= Date.now() ? 10000 : Math.max(50, Math.min(10000, deadline - Date.now() + 20)));
    };
    const refresh = async () => {
        if (disposed || pending) return;
        pending = true;
        try { await callback.invokeMethodAsync('RefreshDay', Date.now() >= deadline); }
        catch { /* Reconnection or next foreground event retries the server read. */ }
        finally {
            pending = false;
            // If offline at midnight, retry at the normal interval, not every 50 ms.
            schedule();
        }
    };
    const visible = () => { if (document.visibilityState !== 'hidden') refresh(); };
    document.addEventListener('visibilitychange', visible);
    window.addEventListener('focus', refresh);
    window.addEventListener('online', refresh);
    schedule();
    return {
        update(value) { deadline = Date.parse(value); if (!pending) schedule(); },
        dispose() {
            disposed = true; clearTimeout(timer);
            document.removeEventListener('visibilitychange', visible);
            window.removeEventListener('focus', refresh);
            window.removeEventListener('online', refresh);
        }
    };
}
