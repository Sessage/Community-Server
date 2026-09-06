window.todoUi = window.todoUi || {};

window.todoUi.enableGlobalSearchShortcut = function (element) {
    window.todoUi.disableGlobalSearchShortcut();

    const handler = function (event) {
        if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== "k") return;
        event.preventDefault();
        element?.focus();
        element?.select();
    };

    document.addEventListener("keydown", handler, true);
    window.todoUi.__globalSearchShortcut = handler;
};

window.todoUi.disableGlobalSearchShortcut = function () {
    const handler = window.todoUi.__globalSearchShortcut;
    if (!handler) return;
    document.removeEventListener("keydown", handler, true);
    delete window.todoUi.__globalSearchShortcut;
};
