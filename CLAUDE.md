# Skimr

Self-hosted RSS/Atom reader modelled on Feedly. .NET 10, Blazor Web App (Interactive Server), Minimal APIs, EF Core (SQLite default, Postgres optional). Runs in Docker or on bare metal (Linux, Windows, macOS).

Full plan, data model and API surface: `docs/PLAN.md`.

## Status

Phase 2 (Domain and fetching) done: entities, tolerant parsers, discovery, sanitiser, SSRF guard, refresher with conditional GET and backoff, bounded scheduler, WebSub. Next: Phase 3 (Auth and multi-user).

## Architecture

- `Skimr.Domain` — entities only, no package references.
- `Skimr.Application` — empty for now (PLAN D30). Depends on Domain only.
- `Skimr.Infrastructure` — EF Core, fetching, parsing, sanitising, SSRF guard, extraction, AI clients. Interfaces only where a test needs a fake (`IOutboundHttp`, `IFeedRefresher`).
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
- API tokens stored as SHA-256 only; the plain token is shown once.

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

See `docs/PLAN.md` §4 (D1–D31). Add new decisions there, not here.
