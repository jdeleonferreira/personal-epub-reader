// Guarda la app para que abra sin conexión. Los libros viven en la carpeta local (o en IndexedDB sin servidor).
const CACHE = "atril-1.1.0";
const SHELL = ["./", "index.html", "lib/jszip.min.js", "lib/epub.min.js", "manifest.webmanifest", "icon-192.png", "icon-512.png", "icon.svg"];

self.addEventListener("install", e => e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener("activate", e => e.waitUntil(
  caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())
));
self.addEventListener("fetch", e => {
  const req = e.request;
  if (req.method !== "GET" || new URL(req.url).pathname.startsWith("/api/")) return;
  // Red primero (para recibir cambios), caché si no hay conexión
  e.respondWith(
    fetch(req).then(res => {
      if (res.ok && new URL(req.url).origin === location.origin) { const copy = res.clone(); caches.open(CACHE).then(c => c.put(req, copy)); }
      return res;
    }).catch(() => caches.match(req).then(r => r || caches.match("index.html")))
  );
});
