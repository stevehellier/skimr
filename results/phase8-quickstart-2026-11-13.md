# Phase 8 quick-start acceptance (2026-11-13)

Windows 10 + Docker Desktop, image built from this checkout (the registry is private, so compose builds locally).
Flow: `tests/Skimr.E2ETests/QuickStartTests.cs` in headless Chromium against the running container:
setup wizard, OPML import (Rust Blog and .NET Blog, real feeds over the internet), first article open.

## SQLite (`deploy/docker-compose.yml`)

| Step | Time |
|---|---|
| Cold image build (`build --no-cache`, base images already local) | 87 s |
| Rebuild with layer cache | 53 s |
| `up -d` to healthy | 8 s |
| Setup done | 5.0 s |
| OPML imported | 6.2 s |
| First article open | 9.4 s |

From `up -d` to reading: about 18 s. From a cold build: about 1 min 45 s.

The first run failed: the nav showed 20 unread but the All list stayed on "All caught up", because only an
empty single-source view checked back for its first fetch. Fixed in `Reader.razor` (any empty feed view
checks back; the periodic nav refresh reloads an empty list), with a bUnit regression test seen failing first.

## PostgreSQL 17 (`deploy/docker-compose.postgres.yml`)

| Step | Time |
|---|---|
| `up -d` to healthy (waits for Postgres) | 20 s |
| Setup done | 13.9 s |
| OPML imported | 18.3 s |
| First article open | 24.7 s |

The first Postgres run failed at the import: the file was set before Blazor's `InputFile` script attached
(a test race; a person can't pick a file that fast). The test now sets the file again if no result appears.

## Notes

- On this machine ports 7783-8382 are reserved by Windows (Hyper-V), so the runs used `SKIMR_PORT=9480/9481`.
- EF logs one `ERR Failed executing DbCommand` on a fresh Postgres database: the first-start probe for the
  migrations history table. Harmless, but it looks alarming; documented in the README troubleshooting section.

## Public image (after the repo and package went public)

Only `deploy/docker-compose.yml` in an empty folder, Docker with an empty config (no registry login),
`docker compose up -d` pulling `ghcr.io/stevehellier/skimr:latest` (= 0.1.1):

| Step | Time |
|---|---|
| `up -d` to healthy | 9 s (image layers already cached from an anonymous pull of 0.1.1; a fresh machine downloads the image first: 252 MB unpacked) |
| Setup done | 10.7 s |
| OPML imported | 11.9 s |
| First article open | 15.1 s |
