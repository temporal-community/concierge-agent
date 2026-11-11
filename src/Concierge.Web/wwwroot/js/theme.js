// Loaded synchronously in <head> so the stored brand and mode are on <html> before the first
// paint; applying them after Blazor's first interactive render flashed the default theme.
// ThemeChoice.cs mirrors normalize() for values coming back from the picker.
(() => {
    const storageKey = "hotel-zgy-theme";
    const brands = ["temporal", "deepgram"];
    const modes = ["light", "dark", "system"];
    const fallback = "temporal-system";

    function normalize(value) {
        // Values stored before brands existed were a bare mode, and the palette they chose is now Temporal's.
        if (modes.includes(value)) return `temporal-${value}`;
        const [brand, mode, extra] = typeof value === "string" ? value.split("-") : [];
        return extra === undefined && brands.includes(brand) && modes.includes(mode) ? `${brand}-${mode}` : fallback;
    }

    function load() {
        try {
            return normalize(localStorage.getItem(storageKey));
        } catch {
            // Storage blocked (private mode, site data disabled): render the default instead of failing.
            return fallback;
        }
    }

    function apply(value) {
        const theme = normalize(value);
        const [brand, mode] = theme.split("-");
        const root = document.documentElement;
        root.dataset.brand = brand;
        if (mode === "system") {
            root.removeAttribute("data-theme");
        } else {
            root.dataset.theme = mode;
        }

        try {
            localStorage.setItem(storageKey, theme);
        } catch {
            // The choice still applies to this page; it just won't survive a reload.
        }

        return theme;
    }

    window.conciergeTheme = { normalize, load, apply };
    apply(load());
})();
