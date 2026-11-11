(() => {
    // theme.js owns the theme rules; the page calls these names over interop.
    window.conciergeChat = {
        loadTheme() {
            return window.conciergeTheme.load();
        },

        applyTheme(theme) {
            window.conciergeTheme.apply(theme);
        }
    };
})();
