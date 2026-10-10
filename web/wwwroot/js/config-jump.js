// Takes the Configuration tab to one block: opens its section, scrolls its row into view and flashes it.

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

// Polls until fn returns something, or gives up after timeout ms
async function until(fn, timeout = 2000) {
    const end = performance.now() + timeout;
    for (;;) {
        const value = fn();
        if (value) return value;
        if (performance.now() > end) return null;
        await delay(40);
    }
}

const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

function flash(element) {
    element.classList.remove("cfg-jump");
    void element.offsetWidth; // restart the animation when the same row is picked again
    element.classList.add("cfg-jump");
    setTimeout(() => element.classList.remove("cfg-jump"), 2000);
}

export async function jumpTo(section, number) {
    // The tab has just been switched to: wait until the panel is on screen
    const panel = await until(() => {
        const p = document.querySelector(`.cfg-${section}`);
        return p && p.offsetParent ? p : null;
    });
    if (!panel) return;

    if (!panel.classList.contains("mud-panel-expanded")) {
        panel.querySelector(".mud-expand-panel-header")?.click();
        await delay(reducedMotion() ? 50 : 350); // let it open before measuring
    }

    let target = panel;
    if (number > 0) {
        const grid = await until(() => panel.querySelector(".mud-table-container"));
        if (grid) {
            // The first cell is the block number, maybe followed by a badge
            const pattern = new RegExp(`^\\s*${number}(?!\\d)`);
            const find = () => [...grid.querySelectorAll("tbody tr")]
                .find(tr => pattern.test(tr.cells[0]?.textContent ?? ""));

            let row = await until(find, 500);
            if (!row) {
                // The grid only renders the rows in view: scroll to where the row should be first
                const height = [...grid.querySelectorAll("tbody tr")].find(tr => tr.cells.length > 1)?.offsetHeight || 40;
                grid.scrollTop = (number - 1) * height;
                row = await until(find, 1000);
            }
            if (row) target = row;
        }
    }

    target.scrollIntoView({ behavior: reducedMotion() ? "auto" : "smooth", block: "center" });
    flash(target);
}
