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

// Links marked data-popup (Help) open in their own sized window, as in GS.Point3DWeb; if pop-ups are blocked they
// open as an ordinary tab. Modified clicks (new tab / window) are left to the browser.
document.addEventListener('click', e => {
    const link = e.target.closest && e.target.closest('a[data-popup]');
    if (!link || e.defaultPrevented || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0) return;
    if (window.open(link.href, link.target || '_blank', 'popup,' + link.dataset.popup)) e.preventDefault();
});

// Copy text to the clipboard (the findings' "Copy fix"); falls back to a hidden textarea where the clipboard API is
// not available (a page that isn't served over https or localhost).
window.gsCopy = async text => {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        const box = document.createElement('textarea');
        box.value = text;
        box.style.position = 'fixed';
        box.style.opacity = '0';
        document.body.appendChild(box);
        box.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch { /* nothing more to do */ }
        document.body.removeChild(box);
        return ok;
    }
};

// A sound and a browser notification when a run finishes or needs an answer, for people who walk away from a long
// run. Off until the user turns it on under Options (remembered by this browser).
window.gsNotify = {
    enabled: () => localStorage.getItem('gsNotify') === '1',
    // Turning it on asks the browser for permission to notify; the sound works either way.
    set: async on => {
        if (on && 'Notification' in window && Notification.permission === 'default') {
            try { await Notification.requestPermission(); } catch { /* the sound still works */ }
        }
        localStorage.setItem('gsNotify', on ? '1' : '0');
    },
    show: (title, body) => {
        if (localStorage.getItem('gsNotify') !== '1') return;
        try {
            const audio = new (window.AudioContext || window.webkitAudioContext)();
            [660, 880].forEach((freq, i) => {
                const osc = audio.createOscillator();
                const gain = audio.createGain();
                osc.frequency.value = freq;
                gain.gain.value = 0.08;
                osc.connect(gain).connect(audio.destination);
                osc.start(audio.currentTime + i * 0.22);
                osc.stop(audio.currentTime + i * 0.22 + 0.18);
            });
        } catch { /* no sound */ }
        if ('Notification' in window && Notification.permission === 'granted') {
            try { new Notification(title, { body }); } catch { /* not allowed here */ }
        }
    }
};

// Hands a text file to the browser as a download (the AI Summary's Markdown; type for another kind, such as JSON); the text is made in the page, so no request is needed.
window.gsDownload = (fileName, text, type) => {
    const url = URL.createObjectURL(new Blob([text], { type: type || 'text/markdown;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
};
