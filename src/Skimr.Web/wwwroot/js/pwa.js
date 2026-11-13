// Registers the service worker under the app's base path (works behind /skimr and the like).
if ("serviceWorker" in navigator) {
  const base = new URL("./", document.baseURI);
  navigator.serviceWorker.register(new URL("sw.js", base), { scope: base.pathname }).catch(() => {
    // Not over HTTPS (or localhost): installing isn't possible, and the app works without it.
  });
}
