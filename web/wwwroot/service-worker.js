// Makes dingoConfig installable as an app, and says something useful when it cannot be reached.
//
// dingoConfig is a Blazor Server app: every screen is drawn by the server over a live
// connection, so there is nothing to cache for offline use. All this does is stay out of
// the way, except when opening a page fails outright - typically the phone is not on the
// dash's WiFi - where it shows offline.html instead of the browser's own error page.
const CACHE = "dingoconfig-offline-v1";
const OFFLINE = ["offline.html", "pwa/app-192.png"];

self.addEventListener("install", event => {
    event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(OFFLINE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(keys.filter(key => key !== CACHE).map(key => caches.delete(key))))
            .then(() => self.clients.claim()));
});

self.addEventListener("fetch", event => {
    if (event.request.mode !== "navigate") return;
    event.respondWith(fetch(event.request).catch(() => caches.match("offline.html")));
});
