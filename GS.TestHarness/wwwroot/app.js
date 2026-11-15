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
