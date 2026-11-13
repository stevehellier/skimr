# Phase 5 power features: test results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17, Playwright Chromium)

```nRunning tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64) passed (15s 403ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (27s 308ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64) passed (48s 485ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 39s 808ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 39s 808ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (27s 308ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64) passed (48s 485ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64) passed (15s 403ms)

  total: 492
  failed: 0
  succeeded: 492
  skipped: 0
  duration: 1m 40s 688ms
```

492 passed, 0 failed: 273 unit, 197 integration (SQLite and Postgres), 19 component (bUnit), 3 E2E (Playwright).

## Acceptance criteria

| Criterion | Evidence | Result |
|---|---|---|
| Search on both providers | `PowerFeatureTests`: title and body match, markup not indexed, filters (source, folder, dates, read state), prefix `*`, the index follows edits and deletes; Postgres plan uses `IX_Articles_Search` (`EXPLAIN`) | Pass |
| Hostile search input | 14 inputs (quotes, `NEAR(`, `AND`, `-`, `title:`, SQL, symbols, Unicode) × 2 providers: none throw; FTS5/tsquery builders unit-tested | Pass |
| Search isolation | Another user finds nothing, even with the owner's source or folder ids as filters | Pass |
| Boards | Collect, nav counts, board view, uniqueness, privacy (can't add others' articles or use others' boards) | Pass |
| Retention never removes saved or boarded | Old read / unread / saved / boarded / recent cases on both providers; articles still in the feed and boarded-after-unsubscribe kept | Pass |
| OPML round-trip | Export → import into another user reproduces sources and folders; nested folders flatten; `xmlUrl` in any case, no `type`, `text` or `title`; idempotent; XXE can't read files; non-OPML reported | Pass |
| Full text through the SSRF guard | Extract + sanitise + cache, search covers the full text, other users can't trigger it, errors reported (404, PDF, unreadable, blocked), Windows-1252 via `<meta charset>`. SmartReader is fed text; its source was read to confirm it only downloads when given a bare URL | Pass |
| Image proxy refuses non-images and private addresses | HTML served as `image/png` → empty 404; tampered signature → 404 without a fetch; 169.254.169.254, 127.0.0.1 and 10.0.0.1 → empty 404 through the real guard; signed-out → redirect; SVG gets a sandbox CSP | Pass |
| CSP in both proxy modes | On: `img-src 'self' data:`; off: `img-src 'self' data: https:` | Pass |
| Favicons | Declared icon found, identical icon stored once, HTML-as-favicon rejected, failures remembered, served from the app | Pass |
| In a real browser (Playwright) | `/` focuses search, query and changed query, hostile query, add to a new board and open it, an `http://` image loads through the proxy (`naturalWidth` 1), service worker registered at the right scope, manifest served, cache holds only the offline page and fingerprinted files, zero console errors | **Pass, real browser** |
| Performance (500 sources / 200k articles) | `results/phase5-perf-2026-11-13.md`, two conditions. Worst case (no statistics, straight after a bulk load): nav 29 ms, pages ≤ 28 ms, per click 31 ms, search ≤ 81 ms. Steady state (after the startup `PRAGMA optimize`): nav 35 ms, pages ≤ 5.5 ms, per click 33 ms, search ≤ 51 ms | Pass |

## Problems found and fixed during the phase

1. **Retention scanned the whole table per batch.** EF turned `Take` + `ExecuteDelete` on a composite key into a correlated `EXISTS`. It now reads keys, then deletes by primary key, user by user.
2. **The search SQL triggered SQLite table-rebuild warnings.** It sat in a migration that also rebuilt `Feeds`; it now has its own migration.
3. **Postgres prefix search.** `websearch_to_tsquery` ignores `*`; prefix terms now use a sanitised `to_tsquery`.
4. **CSP precedence.** Blazor sets its own weaker `frame-ancestors 'self'` CSP on component pages, so the app's policy must always win, except where an endpoint opts in to a stricter one (SVG images).
5. **The search cap dropped filtered matches.** The 5,000-candidate cap ran before the user and filter scoping. It now applies only to unfiltered searches (`The_candidate_cap_never_hides_matches_from_a_filtered_search`, which failed on both providers before the fix).
6. **Favicons could hold fetch workers** for 30 s per request after an OPML import. They now use a 5 s per-request timeout and try at most 3 URLs.
7. **The perf run used ANALYZE, which real installs never run.** It is re-measured without statistics (still within target) and with the app's new startup `PRAGMA optimize`. A sampled `analysis_limit` misled the planner (85 ms Read Later count), so the optimize is full; it takes 0.5 s at 200k articles.
8. **Performance, measured then fixed.** See the perf file: nav 311 → 36 ms, one source 454 → 1.8 ms, common-word search 363 → 11 ms.

Screenshots: `results/phase5-e2e/`.
