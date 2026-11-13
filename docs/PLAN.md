# Skimr — Plan

Status: **draft, awaiting approval**. Nothing in Phase 1 starts until this is approved.

Agreed 2026-11-13: GitHub repo is private; PWA is installable but online-only (offline reading out of scope for now); the 2-minute target is timed from `compose up` with the image already pulled.

## 1. Solution layout

```
Skimr.sln
global.json                        pins SDK 10.0.4xx (11.0 is also installed on the dev box)
Directory.Build.props              net10.0, Nullable, TreatWarningsAsErrors, analyzers
Directory.Packages.props           central package versions
.editorconfig / .gitattributes     LF for *.sh, Dockerfile, *.service; final newline
src/
  Skimr.Domain/                    entities, value objects, enums. No package references.
  Skimr.Application/               use cases, interfaces (IFeedFetcher, IFeedParser, IHtmlSanitizer,
                                   ISearchService, ISummariser, ISafeHttpClient, IClock ...), DTOs
  Skimr.Infrastructure/            SkimrDbContext + config, fetch/parse/discover, scheduler,
                                   sanitiser, SSRF guard, SmartReader, favicons, OPML, AI clients
  Skimr.Infrastructure.Sqlite/     SQLite migrations, FTS5 search impl, design-time factory
  Skimr.Infrastructure.Postgres/   Postgres migrations, tsvector search impl, design-time factory
  Skimr.Web/                       Blazor (Interactive Server), Minimal APIs, GReader + Fever,
                                   auth, headers/CSP, health, Serilog, DI composition, CLI flags
tests/
  Skimr.UnitTests/                 parsing (+ Fixtures/feeds/*), dedup, scheduling, backoff,
                                   sanitiser, SSRF guard, OPML round-trip, discovery
  Skimr.IntegrationTests/          WebApplicationFactory; every API test runs on SQLite and
                                   Postgres (Testcontainers) via a provider fixture
  Skimr.ComponentTests/            bUnit: article list, nav unread counts, reader pane
  Skimr.PerfTests/                 seeds 500 sources / 200k articles, times key queries.
                                   Not in the default CI test run; run on demand + nightly.
deploy/
  Dockerfile, docker-compose.yml, docker-compose.postgres.yml
  install.sh, skimr.service, install-windows.ps1, com.skimr.plist
  Caddyfile.example, nginx.conf.example
.github/workflows/ci.yml, release.yml
```

Change from your layout: two provider projects. Separate migrations per provider need separate
migration assemblies, and search (FTS5 vs `tsvector`) is provider-specific, so both live there.

## 2. Data model

`Feed` and `Article` are **global** — a feed is fetched once however many users subscribe.
Everything else is **per user**.

| Entity | Key fields | Notes |
|---|---|---|
| `Feed` | Id (long), Url (unique), SiteUrl, Title, IconId, ETag, LastModified, NextFetchAt, LastSuccessAt, LastErrorAt, LastError, ConsecutiveFailures, HubUrl, WebSub lease/secret | Health status shown in UI comes from here |
| `Article` | Id (long), FeedId, DedupKey, Guid, Url, Title, Author, ContentHtml (sanitised), Snippet, ThumbnailUrl, PublishedAt, UpdatedAt, ContentHash, LastSeenAt, FullTextHtml, FullTextFetchedAt | Unique (FeedId, DedupKey). Long IDs double as GReader item IDs |
| `Icon` | Id, Hash, MimeType, FileName | Stored under `<data>/icons/` |
| `SkimrUser` | Identity user (Guid key) + Settings (owned: theme, mark-read mode, AI opt-in) | |
| `Subscription` | Id, UserId, FeedId, CustomTitle, RefreshMinutes, AlwaysFullText | Unique (UserId, FeedId) |
| `Folder` | Id, UserId, Name, SortOrder | Unique (UserId, Name) |
| `SubscriptionFolder` | SubscriptionId, FolderId, SortOrder | Many-to-many: a source in several folders |
| `UserArticle` | UserId, ArticleId, FeedId*, PublishedAt*, IsRead, ReadAt, IsSaved, SavedAt | *Denormalised for indexes. See decision D1 |
| `Board` / `BoardArticle` | Board: Id, UserId, Name, SortOrder. BoardArticle: BoardId, ArticleId, AddedAt | Article can sit in several boards |
| `ViewPreference` | UserId, ViewKey ("today", "folder:12"...), Layout, Sort, UnreadOnly | Remembers layout per view |
| `ApiToken` | Id, UserId, Name, Prefix, TokenHash (SHA-256), Scopes, CreatedAt, LastUsedAt, ExpiresAt | Plain token shown once |
| `Invite` | Id, TokenHash, Email?, CreatedBy, ExpiresAt, UsedAt | |
| `AppSetting` | Key, Value | Admin runtime settings (registration mode) that override config defaults |
| `AiFeed` / `AiFeedMatch` / `ArticleSummary` | Phase 7 | |

### Indexes that carry the perf target
- `UserArticle` PK (UserId, ArticleId)
- `UserArticle` (UserId, IsRead, FeedId) — unread counts per source in one grouped, index-only query; folder counts are sums of their sources
- `UserArticle` (UserId, FeedId, PublishedAt, ArticleId) — source views, keyset paging
- `UserArticle` (UserId, PublishedAt, ArticleId) — All/Today
- `UserArticle` (UserId, IsSaved, SavedAt) — Read Later
- `Article` (FeedId, DedupKey) unique; `Article` (LastSeenAt) for retention; `Feed` (NextFetchAt) for the scheduler

All list paging is keyset on (PublishedAt, ArticleId), never OFFSET.

## 3. API surface

REST, all under `/api/v1`, cookie or `Authorization: Bearer <token>`. OpenAPI JSON + Scalar UI at `/api/docs`.

| Area | Endpoints |
|---|---|
| Me / tokens | `GET /me`, `GET/POST /tokens`, `DELETE /tokens/{id}` |
| Discovery | `POST /discover {url}` → candidate feeds |
| Subscriptions | `GET/POST /subscriptions`, `GET/PATCH/DELETE /subscriptions/{id}`, `PUT /subscriptions/{id}/folders`, `POST /subscriptions/{id}/refresh` |
| Folders | `GET/POST /folders`, `PATCH/DELETE /folders/{id}`, `PUT /folders/order` |
| Articles | `GET /articles?view=&unreadOnly=&sort=&cursor=&limit=`, `GET /articles/{id}`, `PATCH /articles/{id} {read,saved}`, `PATCH /articles {ids,read,saved}`, `POST /articles/mark-read {view,olderThan}`, `POST /articles/{id}/full-text` |
| Counts | `GET /counts` |
| Boards | `GET/POST /boards`, `PATCH/DELETE /boards/{id}`, `PUT/DELETE /boards/{id}/articles/{articleId}` |
| Search | `GET /search?q=&feed=&folder=&from=&to=&read=` |
| OPML | `GET /opml`, `POST /opml` |
| Settings | `GET/PATCH /settings`, `PUT /views/{key}` |
| Admin | `GET/POST /admin/users`, `PATCH/DELETE /admin/users/{id}`, `POST /admin/invites`, `GET/PATCH /admin/settings`, `GET /admin/feeds` (health) |
| AI | `POST /articles/{id}/summary`, `GET/POST/PATCH/DELETE /ai-feeds` |

Non-API routes: `/health`, `/health/ready`, `/icons/{id}`, `/img/{sig}/{b64url}` (image proxy), `/websub/{feedId}`.

**Google Reader** (`/api/greader`): `accounts/ClientLogin`, `reader/api/0/token`, `user-info`,
`subscription/list|edit|quickadd|export|import`, `tag/list`, `rename-tag`, `disable-tag`, `unread-count`,
`stream/contents/{streamId}`, `stream/items/ids`, `stream/items/contents`, `edit-tag`, `mark-all-as-read`.
Folders map to `user/-/label/<name>`, Read Later to `starred`. Boards are not exposed (no GReader concept).

**Fever** (`/api/fever`, required): groups, feeds, feeds_groups, favicons, items, unread_item_ids, saved_item_ids, mark.

## 4. Decisions

| # | Decision | Why |
|---|---|---|
| D1 | **Per-user rows** (`UserArticle`) created on ingest for each subscriber, and back-filled (last 200 items) on subscribe | Unread counts become an index-only grouped count. The alternative (anti-join against a "read" table) scans every article in every subscribed feed — too slow at 200k. Cost: rows = users × articles, fine for self-hosted scale |
| D2 | Timestamps stored as UTC `DateTime`, not `DateTimeOffset` | EF Core SQLite can't order or compare `DateTimeOffset` server-side |
| D3 | Retention deletes the user's read, unsaved, unboarded `UserArticle` rows older than N days. An `Article` is deleted only once it has no `UserArticle` rows **and** wasn't seen in the feed for N days | Otherwise a purged item still present in the feed XML comes back as unread |
| D4 | One scheduler loop (30 s tick) picks due feeds into a bounded `Channel`; N workers (default 8) consume. Feed interval = min of its subscribers' intervals, floor 5 min, raised by `Cache-Control: max-age` / `Retry-After`. Backoff: interval × 2^failures, cap 24 h, ±10 % jitter | Shared feed, one fetch; no Hangfire/Quartz needed |
| D5 | Dedup key: GUID → link → SHA-256(title + date) | As specified |
| D6 | All outbound HTTP goes through one `SocketsHttpHandler` with `ConnectCallback` that resolves the host, checks every resolved IP, and connects to the checked IP; redirects followed manually (max 5), each hop re-checked | Checking in `ConnectCallback` closes the DNS-rebinding gap that pre-resolve checks leave |
| D7 | API tokens: 32 random bytes, stored as SHA-256 | High-entropy tokens don't need a slow hash |
| D8 | GReader `ClientLogin` takes **username + an API token** (app password), not the account password | A stolen client config can't log into the web UI and can be revoked alone |
| D9 | Fever key = md5(username:token). Stored as SHA-256 of that md5 | Fever protocol forces md5 on the client; we still don't store what the client sends |
| D10 | Image proxy URLs are HMAC-signed with a key from Data Protection | Prevents an open proxy |
| D11 | CSP: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:` (`'self' data:` only when image proxy on); `frame-src` = embed allow-list; `frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'`. Feed `style` attributes are stripped | No inline script or style anywhere in our own markup. Server-rendered HTML checked in Phase 1 (no inline script/style). Live circuit under this CSP verified in Phase 4, the first phase with interactive components |
| D12 | AI: provider configured by admin (off by default). Each user must also opt in before any of their content leaves the server | "Never send content unless the user has enabled it" |
| D13 | AI clients use plain `HttpClient` against the OpenAI-compatible and Anthropic Messages APIs; no SDK packages | Two small JSON calls; avoids two dependencies |
| D14 | Docker `HEALTHCHECK` runs `Skimr --healthcheck` (the app binary probes `/health`) | Chiseled images have no shell or curl |
| D15 | Assertions: **AwesomeAssertions** | Apache-2.0 fork with the FluentAssertions API; Shouldly would mean a different style |
| D16 | SQLite: WAL mode, busy timeout 5 s; fetch workers write in short transactions | SQLite allows one writer |
| D17 | WebSub only when `PublicUrl` is configured | The hub needs a reachable callback URL |
| D18 | Fever is required (Phase 6b), not optional | Agreed 2026-11-13 |
| D19 | No HTTPS redirection in the app; HSTS only on HTTPS requests | Most installs sit behind a TLS-terminating proxy or run plain HTTP on a LAN; forcing a redirect breaks both |
| D20 | Docker `HEALTHCHECK` probes `/health/ready`, not `/health` | An app that can't reach its database isn't usable, and compose `depends_on: service_healthy` needs that signal |
| D21 | Data Protection keys persisted to `<data>/keys` | Cookies survive restarts and container rebuilds; backup stays "copy one folder". Keys aren't encrypted at rest; filesystem permissions protect them |
| D22 | Tests run on Microsoft.Testing.Platform (opted in via `global.json`) | .NET 10 SDK no longer runs xUnit v3 through VSTest |
| D23 | Serilog registered with `preserveStaticLogger: true` | Several hosts in one test process otherwise share one static logger and leave log files locked |
| D24 | `AllowPrivateNetworkFeeds` opens private, loopback, link-local and CGNAT ranges, but cloud metadata IPs (169.254.169.254, 169.254.170.2, 100.100.100.200, fd00:ec2::254) and non-unicast ranges stay blocked | A home-lab flag shouldn't also expose cloud credentials |
| D25 | XML is read with `DtdProcessing = Ignore` + `XmlResolver = null`, not `Prohibit` | RSS 0.91 feeds carry the Netscape DOCTYPE and `Prohibit` rejects them. `Ignore` still never resolves external entities or expands internal ones: tests prove an external-entity feed can't read a local file and billion-laughs doesn't expand |
| D26 | Broken XML gets one repair pass: encoding fallback (UTF-8 label on Windows-1252 bytes), stray BOM/whitespace before the prolog, invalid control characters, bare `&`, HTML-only entities; unknown entities become literal text | Real feeds break all of these; the repair uses the same safe reader settings |
| D27 | RSS 0.9x is relabelled `version="2.0"` before parsing; RSS `<description>` is treated as HTML | The Syndication RSS formatter rejects other versions and labels descriptions as plain text |
| D28 | All settings, including new `Fetch:*` and `Content:*` keys, are validated in the pre-host loader, not `IOptions.ValidateOnStart` | `ValidateOnStart` fires after migrations and prints a stack trace instead of the every-key-at-once list |
| D29 | `Subscription.UserId` and `UserArticle.UserId` have no foreign key yet | Users arrive with Identity in Phase 3, which adds the constraint |

## 5. Dependencies (all pre-approved by the stack; listed so later PRs don't stall)

| Package | Licence | Used for |
|---|---|---|
| Microsoft.EntityFrameworkCore.Sqlite / .Design | MIT | SQLite provider, migrations |
| Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL | Postgres provider |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | MIT | Auth |
| Microsoft.AspNetCore.OpenApi | MIT | OpenAPI document |
| Scalar.AspNetCore | MIT | `/api/docs` UI |
| Microsoft.Extensions.Hosting.WindowsServices | MIT | Windows Service |
| System.ServiceModel.Syndication | MIT | RSS/Atom parsing |
| Serilog.AspNetCore, Serilog.Sinks.File | Apache-2.0 | Logging |
| SmartReader | Apache-2.0 | Full-text extraction |
| HtmlSanitizer | MIT | Sanitising |
| AngleSharp | MIT | HTML parsing for discovery, plain text and snippets (also an HtmlSanitizer dependency) |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Tests |
| AwesomeAssertions | Apache-2.0 | Assertions |
| bunit | MIT | Component tests |
| Microsoft.AspNetCore.Mvc.Testing | MIT | `WebApplicationFactory` |
| Testcontainers.PostgreSql | MIT | Postgres integration tests |

No mocking library: hand-written fakes (`FakeClock`, `FakeHttpHandler`). Anything beyond this list gets asked about first.

## 6. Phases and acceptance criteria

Each phase: one GitHub issue, one `<type>/<slug>` branch, one PR, merged when CI is green.

| # | Phase | Done when |
|---|---|---|
| 1 | **Skeleton** — solution, projects, DI, config binding + startup validation, Serilog, health checks, SQLite + Postgres wiring, first migration per provider, auto-migrate flag, Dockerfile, CI | `dotnet build` has 0 warnings; tests pass; app starts on SQLite and on Postgres (Testcontainers); `/health` and `/health/ready` return 200; `docker build` + `docker run` report healthy; CI green |
| 2 | **Domain and fetching** — entities, parsers (RSS 0.9x/2.0, RDF, Atom, JSON Feed), discovery, scheduler, conditional GET, backoff, dedup, sanitiser, SSRF guard, WebSub | Fixture-based parser tests incl. bad dates, missing GUIDs, CDATA, mixed encodings, relative URLs; unit tests for dedup, backoff, scheduler, sanitiser, SSRF (incl. redirect-to-private and DNS rebinding); an external-entity fixture cannot read a local file and billion-laughs does not expand (D25); ingest runs on both providers |
| 3 | **Auth and multi-user** — Identity, setup wizard, registration modes, invites, admin user management, API tokens, lockout, rate limits | Integration tests: wizard runs once only; closed registration blocks sign-up; token auth works and revocation sticks; lockout and rate limit trigger |
| 4 | **Core UI** — nav + unread counts, add/organise sources and folders, three layouts, reader pane, mark-read modes, Read Later, keyboard shortcuts, themes, responsive | bUnit tests for article list and nav counts; manual walk-through on desktop + mobile width; keyboard-only use works; AA contrast checked; a live interactive circuit runs with no CSP violations in a real browser console |
| 5 | **Power features** — Boards, search (FTS5 / tsvector), full text, OPML, retention, favicons, image proxy, PWA | OPML round-trip test with nested folders; search tests on both providers; retention never removes saved/boarded; perf run: 500 sources / 200k articles, counts and first page each < 100 ms on SQLite (results saved under `results/`) |
| 6 | **APIs** — REST complete + Scalar, Google Reader; 6b Fever | API integration tests on both providers incl. a scripted GReader sync flow; manual sync from NetNewsWire |
| 7 | **AI (optional)** — `ISummariser`, OpenAI-compatible + Anthropic, summaries, AI feeds | Tests with a fake provider; no outbound call when the user hasn't opted in |
| 8 | **Packaging and docs** — compose files, install.sh, systemd, Windows script, launchd, publish profiles, README, CHANGELOG, release workflow | Fresh Debian VM: `sudo ./install.sh` works; Windows script works; compose quick start under 2 minutes after image pull; tag builds multi-arch image + 4 single-file artefacts |

## 7. Risks

- **PWA + Interactive Server**: the app needs a live SignalR connection. The service worker caches the shell so the app installs and starts fast, but nothing works offline, and flaky mobile connections show the reconnect banner.
- **"2 minutes on a fresh machine"** depends on image pull speed; I'll measure from `compose up` with the image already pulled.
- **SQLite at 200k × N users**: fine for one or a few users. Many heavy users → recommend Postgres in the docs.
- **First-start log noise on Postgres**: EF logs one `ERR` querying `__EFMigrationsHistory` before creating it. Harmless; document in README.
- **arm64 image**: built in CI with QEMU; slow (~10–15 min) but no Pi needed for building.
