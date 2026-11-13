// Reader behaviour that needs the browser: keyboard shortcuts, mark-read-on-scroll,
// infinite scroll, focus and dialogs. Loaded as a module (no inline script: the CSP forbids it).

let dotnet = null;
let keyHandler = null;
let scrollObserver = null;
let endObserver = null;
let pendingRead = new Set();
let flushTimer = null;
let gPressedAt = 0;

const typing = (el) =>
  el instanceof HTMLElement &&
  (el.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(el.tagName));

export function init(dotnetRef) {
  dispose();
  dotnet = dotnetRef;

  keyHandler = (e) => {
    if (e.defaultPrevented || typing(e.target) || e.ctrlKey || e.altKey || e.metaKey) return;
    // An open dialog owns the keyboard (the browser closes it on Escape); don't also act on the page.
    if (document.querySelector("dialog[open]")) return;

    // "/" focuses search here, without a round trip; preventDefault stops Firefox's find bar.
    if (e.key === "/") {
      const box = document.getElementById("search-box");
      if (box) {
        e.preventDefault();
        box.focus();
        box.select();
      }
      return;
    }

    let key = e.key;
    if (key === "A" && e.shiftKey) key = "shift+a";
    else if (e.shiftKey && key !== "?") return;

    // Two-key "g then x" navigation: the second key must follow within a second.
    if (Date.now() - gPressedAt < 1000 && ["a", "t", "s"].includes(key)) {
      gPressedAt = 0;
      e.preventDefault();
      dotnet.invokeMethodAsync("OnShortcut", "g" + key);
      return;
    }
    if (key === "g") {
      gPressedAt = Date.now();
      return;
    }

    // Enter on a focused button or link already activates it; only map Enter elsewhere.
    if (key === "Enter" && e.target instanceof HTMLElement && e.target.closest("button, a")) return;

    if (["j", "k", "o", "Enter", "v", "s", "m", "shift+a", "?", "Escape"].includes(key)) {
      if (key !== "Escape") e.preventDefault();
      dotnet.invokeMethodAsync("OnShortcut", key);
    }
  };
  document.addEventListener("keydown", keyHandler);
}

/// Watches list items; anything scrolled up past the top of the viewport is reported as read.
export function watchScroll(enabled) {
  scrollObserver?.disconnect();
  scrollObserver = null;
  if (!enabled) return;

  scrollObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      const passedAbove = !entry.isIntersecting && entry.boundingClientRect.bottom <= (entry.rootBounds?.top ?? 0);
      const id = entry.target.dataset.articleId;
      if (passedAbove && id && entry.target.classList.contains("unread")) pendingRead.add(Number(id));
    }
    if (pendingRead.size > 0) {
      clearTimeout(flushTimer);
      flushTimer = setTimeout(flushRead, 400);
    }
  });
  observeItems();
}

export function observeItems() {
  if (scrollObserver) {
    document.querySelectorAll(".article-item[data-article-id]").forEach((el) => scrollObserver.observe(el));
  }
  const end = document.getElementById("list-end");
  endObserver?.disconnect();
  if (end) {
    endObserver = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) dotnet?.invokeMethodAsync("OnNearEnd");
    }, { rootMargin: "400px" });
    endObserver.observe(end);
  }
}

function flushRead() {
  const ids = [...pendingRead];
  pendingRead = new Set();
  if (ids.length > 0) dotnet?.invokeMethodAsync("OnScrolledPast", ids);
}

export function focus(selector) {
  const el = document.querySelector(selector);
  if (el) {
    el.focus({ preventScroll: false });
    el.scrollIntoView({ block: "nearest" });
  }
}

export function showDialog(id) {
  document.getElementById(id)?.showModal();
}

export function closeDialog(id) {
  document.getElementById(id)?.close();
}

export function openOriginal(url) {
  if (/^https?:\/\//i.test(url)) window.open(url, "_blank", "noopener,noreferrer");
}

/// Theme is per device: a plain cookie the server reads to render <html data-theme> without a flash.
export function setTheme(theme) {
  const path = new URL(document.baseURI).pathname;
  document.cookie = `skimr.theme=${encodeURIComponent(theme)}; path=${path}; max-age=31536000; samesite=lax`;
  document.documentElement.dataset.theme = theme;
}

export function getTheme() {
  return document.documentElement.dataset.theme || "system";
}

export function dispose() {
  if (keyHandler) document.removeEventListener("keydown", keyHandler);
  keyHandler = null;
  scrollObserver?.disconnect();
  endObserver?.disconnect();
  scrollObserver = endObserver = null;
  clearTimeout(flushTimer);
  dotnet = null;
}
