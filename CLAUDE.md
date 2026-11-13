# Skimr

Self-hosted RSS/Atom reader modelled on Feedly. .NET 10, Blazor Web App (Interactive Server), Minimal APIs, EF Core (SQLite default, Postgres optional). Runs in Docker or on bare metal (Linux, Windows, macOS).

Full plan, data model and API surface: `docs/PLAN.md`.

## Status

Phase 5 (Power features) done: boards, search (FTS5 / tsvector), full-text extraction, OPML import/export, retention, favicons, image proxy, installable PWA; 200k-article performance targets met. Next: Phase 6 (APIs).

## Architecture

- `Skimr.Domain` — entities only, no package references.
- `Skimr.Infrastructure` — EF Core, fetching, parsing, sanitising, SSRF guard, extraction, AI clients. Interfaces only where a test needs a fake (`IOutboundHttp`, `IFeedRefresher`).
- Search is maintained by database triggers (SQLite FTS5) or an expression index (Postgres), never by app code. A migration that rebuilds the `Articles` table must recreate the `ArticleSearch_*` triggers (PLAN D51). Search tests need a migrated database; `EnsureCreated` has no FTS table.
- Boolean filters on `UserArticles` use partial indexes (`NOT "IsRead"`, `"IsSaved"`); filter text must match the SQL EF generates or SQLite won't use them (D57). Check plans with `tools/Skimr.Perf` after changing hot queries.
- Any outbound fetch (feeds, full text, favicons, images) goes through `IOutboundHttp`; libraries that can fetch by themselves (SmartReader) get text, never URLs.
- Every string from a feed is stored through `StoredText.Fit` (strips control chars Postgres rejects, respects column length).
- `Skimr.Infrastructure.Sqlite` / `.Postgres` — per-provider migrations and search.
- `Skimr.Web` — Blazor UI, REST + Google Reader + Fever APIs, auth, hosting, DI composition.

`Feed` and `Article` are global (fetched once). Per-user state lives in `UserArticle` rows created on ingest for each subscriber.

## Rules that must not be broken

- All feed and extracted HTML passes through `IHtmlSanitizer` **before** it is stored. Never render unsanitised HTML.
- All outbound HTTP (feeds, full text, favicons, images, WebSub, AI) goes through the SSRF-guarded client. Never `new HttpClient()`.
- XML readers: `DtdProcessing = Ignore`, `XmlResolver = null` (PLAN D25). Never `DtdProcessing.Parse`, never a resolver.
- No inline `<script>` or `style=""` in our markup — the CSP forbids them.
- New config keys are read and validated in `SkimrSettings.Load` (PLAN D28), never `IOptions.ValidateOnStart`.
- No secrets in the repo. `.env` must be in `.gitignore` before one is created.
- API tokens stored as SHA-256 only; the plain token is shown once. `/api` accepts bearer tokens only, never cookies (PLAN D33).
- Every page needs a signed-in user (fallback policy). Anything anonymous must say so explicitly: `[AllowAnonymous]` or `.AllowAnonymous()`.
- Reader pages are Interactive Server with `prerender: false` (PLAN D42): load data in `OnInitialized/OnParametersSet`, start JS in `OnAfterRenderAsync(firstRender)`. UI services use `IDbContextFactory`, one context per call (D46).
- Every reading/library service method takes the signed-in user id and filters by it; never trust ids from the browser.
- Interactive pages include `<SessionWatch />`; `SignedOutCircuitGuard` blocks circuits whose user was disabled (PLAN D49). Event handlers that insert must ignore re-entry while busy, and services must treat a unique-key violation as "already exists" (D50).
- Account forms are static SSR. Blazor form names must be unique per page; for per-row actions use one named form plus `<button form="id" name="Model.Command" value="...">`.

## Conventions

- `net10.0`, nullable on, warnings as errors, file-scoped namespaces. Follow `.editorconfig` (final newline, LF for `*.sh`, `Dockerfile`, `*.service`).
- Package versions live in `Directory.Packages.props`. No new package without asking (see PLAN §5).
- Timestamps are UTC `DateTime` (EF SQLite can't order `DateTimeOffset`). Get time from `IClock`/`TimeProvider`, never `DateTime.UtcNow` directly.
- List queries page by keyset on (PublishedAt, ArticleId), never OFFSET.
- Tests: xUnit v3 + AwesomeAssertions, hand-written fakes, no mocking library. API integration tests run on both providers.
- Save test output to `results/<test-name>-<yyyy-MM-dd>.md` before moving on.
- Git: one GitHub issue + `<type>/<slug>` branch + PR per phase. Commit/PR title `<type>: <imperative summary>`. No AI attribution trailers.

## Build, test, run

Shell is PowerShell 7.

```powershell
dotnet build                                   # 0 warnings required
dotnet test                                    # Microsoft.Testing.Platform; Postgres tests need Docker running
pwsh tests/Skimr.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium  # once, for the E2E tests
dotnet run -c Release --project tools/Skimr.Perf   # 500 sources / 200k articles timing run; writes results/phase5-perf-<date>.md
dotnet tool restore                            # pins dotnet-ef
dotnet run --project src/Skimr.Web             # SQLite under ./data by default

# Migrations (one set per provider)
dotnet ef migrations add <Name> --project src/Skimr.Infrastructure.Sqlite
dotnet ef migrations add <Name> --project src/Skimr.Infrastructure.Postgres

docker build -f deploy/Dockerfile -t skimr:dev .
docker compose -f deploy/docker-compose.yml up -d  # compose files arrive in Phase 8
```

Config order: `appsettings.json` → env vars `SKIMR__Section__Key` → command line. Everything persistent lives under the data directory.

## Decisions

See `docs/PLAN.md` §4 (D1–D59). Add new decisions there, not here.
