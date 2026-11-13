// Skimr service worker (PLAN D55). The app is online-only (a live server connection), so this
// only makes it installable and fast to start:
// - page navigations always go to the network; offline you get a small offline page;
// - fingerprinted static files (immutable: their name changes with their content) are cached;
// - nothing else is cached: no signed-in pages, API, image proxy, icons or Blazor traffic.
const CACHE = "skimr-static-v1";
const OFFLINE = "offline.html";
const scope = new URL(self.registration.scope).pathname;
// e.g. app.x69e5qmbf9.css, blazor.web.whefm3rrpu.js
const fingerprinted = /\.[a-z0-9]{8,}\.(css|js|png|svg|woff2?)$/i;

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.add(OFFLINE)));
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key))))
  );
  self.clients.claim();
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || !url.pathname.startsWith(scope)) return;

  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    return;
  }

  if (!fingerprinted.test(url.pathname)) return;

  event.respondWith(
    caches.open(CACHE).then(async (cache) => {
      const hit = await cache.match(request);
      if (hit) return hit;
      const response = await fetch(request);
      if (response.ok) cache.put(request, response.clone());
      return response;
    })
  );
});
