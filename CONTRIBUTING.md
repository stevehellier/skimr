# Contributing to Skimr

Thanks for helping. This guide covers how work is tracked, the conventions every change follows,
and how to build and test locally. Security problems go through [SECURITY.md](SECURITY.md), never
a public issue.

## Licence

Skimr is licensed under the [GNU AGPL v3](LICENSE). By submitting a contribution you agree that it
is licensed under the same terms. No separate sign-off or agreement is needed. Only submit work you
have the right to license this way.

## How work is tracked

1. **Open an issue first**: bug, feature or chore. For anything bigger than a small fix, agree the
   approach in the issue before writing code; it saves rewrites.
2. **Branch** as `<type>/<slug>`, for example `bug/opml-empty-folder`. No issue numbers in branch names.
3. **Commits and PR titles** use `<type>: <imperative summary>`, under about 70 characters, no full
   stop: `bug: keep folder order after OPML import`. Types: `feat`, `bug`, `test`, `chore`.
4. **One pull request per issue.** Its description has these sections, in order:
   - **Approach**: what changed and why, ending with `Closes #N`.
   - **Evidence**: test results, and timings for anything on a hot path.
   - **Dependencies**: a table of packages added, updated or removed (or "None").
   - **Schema & Data Changes**: migrations, on both providers (or "None").
   - **Checklist**: builds with 0 warnings, tests pass on SQLite and PostgreSQL, CI green.

## Rules that must not be broken

These protect users' data and servers. A PR that breaks one won't be merged, however useful it is.

- All feed and extracted HTML passes through the sanitiser **before** it's stored. Never render
  unsanitised HTML; model output is text only.
- Every outbound HTTP request goes through the SSRF-guarded client (`IOutboundHttp` / `OutboundHttp`).
  Never `new HttpClient()`. Libraries that can fetch by themselves get text, never URLs.
- XML readers: `DtdProcessing = Ignore`, `XmlResolver = null`.
- No inline `<script>` or `style=""` in our markup: the Content-Security-Policy forbids them.
- Every reading or library service method takes the signed-in user's id and filters by it. Never
  trust ids from the browser or an API client.
- Anything anonymous says so explicitly (`[AllowAnonymous]` / `.AllowAnonymous()`).
- New settings are read and validated in `SkimrSettings.Load`, so a bad value stops startup with a
  clear message.
- No secrets in the repository.

The full list, with reasons, is in `CLAUDE.md`, and the design decisions (D1, D2, …) are in
[docs/PLAN.md](docs/PLAN.md) §4. A change that needs a new decision adds it there.

## Conventions

- .NET 10, nullable on, warnings as errors, file-scoped namespaces. Follow `.editorconfig`
  (final newline; LF for `*.sh`, `Dockerfile`, `*.service`).
- **No new NuGet package without discussing it in the issue first.** Versions live in
  `Directory.Packages.props`; approved packages are listed in `docs/PLAN.md` §5.
- Keep changes small: don't refactor or reformat code the change doesn't need to touch, and don't add
  an abstraction for a single use.
- Timestamps are UTC `DateTime`, taken from `TimeProvider`, never `DateTime.UtcNow` directly.
- List queries page by keyset (`PublishedAt`, `ArticleId`), never `OFFSET`.
- Every string from a feed is stored through `StoredText.Fit`.
- **Database changes** need a migration for each provider:
  ```sh
  dotnet tool restore
  dotnet ef migrations add <Name> --project src/Skimr.Infrastructure.Sqlite
  dotnet ef migrations add <Name> --project src/Skimr.Infrastructure.Postgres
  ```
  Migrations are additive. A SQLite migration that rebuilds the `Articles` table must recreate the
  search triggers (PLAN D51).

## Build and test

You need the .NET 10 SDK (see `global.json`) and Docker (for the PostgreSQL tests).

```sh
dotnet build                     # must report 0 warnings
dotnet test                      # unit, integration (SQLite + PostgreSQL), component and browser tests
dotnet run --project src/Skimr.Web
```

Once, for the browser (Playwright) tests:

```sh
pwsh tests/Skimr.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium
```

Testing expectations:

- **A bug fix comes with a test that fails without the fix.** Say in the PR that you saw it fail.
- xUnit v3 with AwesomeAssertions; hand-written fakes, no mocking library.
- Integration and API tests run on **both** SQLite and PostgreSQL (the `Sqlite…`/`Postgres…` test
  class pairs).
- Anything a user can reach through an id gets an isolation test: another user's id returns 404 or
  changes nothing.
- Changes to hot queries (nav counts, article lists, search, sync APIs): run
  `dotnet run -c Release --project tools/Skimr.Perf` and include the before/after numbers. The
  targets are under 100 ms for the nav and each page, and under 150 ms for a click, at 200k articles.

CI runs the whole suite, builds the multi-arch Docker image, smoke-tests it, and installs the Linux
build on a systemd runner. All of it must be green before a merge.

## Packaging changes

If you touch `deploy/`:

- `install.sh` must pass `shellcheck` and keep working on a minimal Debian (no curl, no ICU
  preinstalled). Test it in a container booted with systemd, or rely on the `install-linux` CI job.
- `install.ps1` must run on Windows PowerShell 5.1 as well as PowerShell 7. Check it with `-WhatIf`.
- Workflow changes should pass `actionlint`.
