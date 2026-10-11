// Remembers the layout the user picked (phone or desktop) and loads the page again in it
export function setView(view) {
    document.cookie = `dc-view=${view}; path=/; max-age=31536000; samesite=lax`;
    location.reload();
}
