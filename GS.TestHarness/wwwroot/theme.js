/* Copyright(C) 2026  Rob Morgan (robert.morgan.e@gmail.com)

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published
    by the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

// The colour theme: dark (the default look, no data-theme), day, dusk, night, or custom (the user's own seven colours, see
// app.css and Components/Shared/ThemeEditor.razor). The browser keeps the choice and the custom colours. Loaded in the
// <head>, so the theme is on before anything is drawn and there is no flash of the wrong one.
(function () {
    const root = document.documentElement;
    const CUSTOM = ['bg', 'text', 'accent', 'warn', 'error', 'fail', 'info'];

    function stored(key) {
        try { return localStorage.getItem(key); } catch { return null; }
    }

    function getCustom() {
        try {
            const value = JSON.parse(stored('gsCustomTheme'));
            return value && CUSTOM.every(k => typeof value[k] === 'string') ? value : null;
        } catch { return null; }
    }

    // A light background wants the browser's own controls (scrollbars, inputs) light too.
    function isLight(hex) {
        const n = parseInt((hex || '#000000').slice(1), 16);
        const lin = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
        return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255) > 0.4;
    }

    function applyCustom() {
        const colours = getCustom();
        if (!colours) return;
        for (const k of CUSTOM) root.style.setProperty('--c-' + k, colours[k]);
        root.style.colorScheme = isLight(colours.bg) ? 'light' : 'dark';
    }

    function clearCustom() {
        for (const k of CUSTOM) root.style.removeProperty('--c-' + k);
        root.style.removeProperty('color-scheme');
    }

    function get() {
        return stored('gsTheme') || (window.matchMedia && matchMedia('(prefers-color-scheme: light)').matches ? 'day' : 'dark');
    }

    function set(name) {
        try { localStorage.setItem('gsTheme', name); } catch { /* not remembered */ }
        clearCustom();
        if (name === 'dark') delete root.dataset.theme;
        else root.dataset.theme = name;
        if (name === 'custom') applyCustom();
    }

    window.gsTheme = {
        get,
        set,
        getCustom,
        // Keeps the colours and, if the custom theme is on, shows them now.
        setCustom: colours => {
            try { localStorage.setItem('gsCustomTheme', JSON.stringify(colours)); } catch { /* not remembered */ }
            if (root.dataset.theme === 'custom') applyCustom();
        }
    };

    set(get());
})();
