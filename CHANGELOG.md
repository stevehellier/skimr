# Changelog

All notable changes to Skimr. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/) (0.x: the API and data format may still change).

## [0.1.1] - 2026-11-13

### Added
- `SECURITY.md`: supported versions, how to report a vulnerability privately, and what's in scope.
- `CONTRIBUTING.md`: workflow, conventions, and how to build and test. Both now ship in the release archives.

No code changes: the application is the same as 0.1.0.

## [0.1.0] - 2026-11-13

The first release.

### Reading
- RSS 0.9x/1.0/2.0, Atom and JSON Feed, with feed discovery from a site address.
- Folders, unread counts, Today and Read Later, and boards (your own collections that survive
  unsubscribing and retention).
- Title-only, magazine and card layouts remembered per view; newest or oldest first.
- Keyboard shortcuts (`j`/`k`, `m`, `s`, `v`, `/`, `Shift+A`), mark read on open, on scroll, or
  only by hand; mark all read with an age limit.
- Full-text search with source, folder, date and read-state filters (SQLite FTS5, PostgreSQL).
- "Load full article" extraction, optionally always on per source.
- OPML import (nested folders flattened to "Parent / Child") and export.
- Favicons, an image proxy (on by default), light and dark themes, an installable app (PWA, online only).

### Sync and API
- REST API under `/api/v1` covering everything the web UI does, with OpenAPI and Scalar docs at `/api/docs/`.
- Google Reader API at `/api/greader` (NetNewsWire, Reeder, ReadKit).
- Fever API at `/api/fever`.
- Per-app API tokens, stored hashed; sync apps use a token as their password, never your account password.

### Accounts and security
- One-time setup code for the first admin; open, invite-only or closed registration; disable users.
- Every piece of feed HTML is sanitised before it's stored; strict Content-Security-Policy with no inline script.
- Every outbound fetch is SSRF-guarded (private networks opt-in, cloud metadata always blocked), with
  size and redirect limits; XML parsed with DTDs ignored.
- Login and API rate limits, account lockout, forwarded-header and base-path support.

### Optional AI (off by default)
- Article summaries and AI feeds (a plain-English rule over chosen sources) with Anthropic or any
  OpenAI-compatible server (Ollama, LM Studio). Nothing is sent unless the admin configures a provider
  and the user turns it on; daily per-user limit.

### Running it
- Docker image (linux/amd64, linux/arm64) on a chiseled, non-root runtime, with health checks;
  Compose files for SQLite and PostgreSQL 17.
- Self-contained single-file builds for linux-x64, linux-arm64, win-x64 and osx-arm64.
- `install.sh` (hardened systemd service), `install.ps1` (Windows service) and a launchd example.
- Retention of old read articles, and SQLite statistics refreshed at startup and after retention.

[0.1.1]: https://github.com/stevehellier/skimr/releases/tag/v0.1.1
[0.1.0]: https://github.com/stevehellier/skimr/releases/tag/v0.1.0
