(() => {
const storageKey = "sessage.theme";
const media = window.matchMedia("(prefers-color-scheme: dark)");
const normalize = value => value === "light" || value === "dark" ? value : "";
const readPreference = () => {
    try { return localStorage.getItem(storageKey); }
    catch { return ""; }
};
const storePreference = preference => {
    try {
        if (preference) localStorage.setItem(storageKey, preference);
        else localStorage.removeItem(storageKey);
    } catch {
        // Storage can be unavailable in hardened/private browser contexts.
    }
};
const apply = preference => {
    const normalized = normalize(preference);
    const resolved = normalized || (media.matches ? "dark" : "light");
    document.documentElement.dataset.theme = resolved;
    document.documentElement.style.colorScheme = resolved;
    document.getElementById("app-design-theme")?.setAttribute("mode", resolved);
    return normalized;
};

window.todoUi = window.todoUi || {};
window.todoUi.setThemePreference = preference => {
    const normalized = apply(preference);
    storePreference(normalized);
};
window.todoUi.refreshTheme = () => apply(readPreference());

apply(readPreference());
media.addEventListener?.("change", () => {
    if (!normalize(readPreference())) apply("");
});
        })();
