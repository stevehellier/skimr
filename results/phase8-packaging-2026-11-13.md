# Phase 8: packaging and docs (2026-11-13)

## Full test run (Windows, SQLite and Postgres 17)

```
total: 628
  failed: 0
  succeeded: 627
  skipped: 1
  duration: 4m 01s 736ms
```

The one skip is `QuickStartTests`, which runs only against a live deployment (results below).

## Acceptance

| Criterion | Evidence | Result |
|---|---|---|
| Compose quick start under 2 minutes | `results/phase8-quickstart-2026-11-13.md`: SQLite 9.4 s from wizard to first article (18 s from `up`), Postgres 24.7 s; cold build 87 s | Pass (first build is extra: the registry is private) |
| `sudo ./install.sh` on Debian/Ubuntu | `results/phase8-install-linux-2026-11-13.md`: Debian 12 with systemd; also the `install-linux` CI job on ubuntu-latest | Pass after the ICU fix |
| Windows script | Parsed and dry-run (`-WhatIf`) in PowerShell 7 and Windows PowerShell 5.1 | **Elevated run pending (user)** |
| Single-file builds | win-x64 run locally (health, setup page, all static assets); linux-x64 run under systemd | Pass; linux-arm64 and osx-arm64 build in the release workflow only |
| Tag builds multi-arch image + 4 artefacts | `release.yml`, actionlint clean | **Runs when v0.1.0 is tagged** |

## Bugs found by the acceptance runs

1. After an OPML import the All view stayed on "All caught up" while the nav showed 20 unread: only an
   empty single-source view checked back for the first fetch. Fixed; bUnit regression test seen failing first.
2. Minimal Debian has no ICU, so the .NET runtime aborted. `install.sh` now installs `libicuNN` with apt.
3. Compose projects took their name from the `deploy` folder (volume `deploy_skimr-data`); now pinned to
   `skimr` / `skimr-postgres` so the backup commands in the README are right.
