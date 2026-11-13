# Phase 2 domain and fetching: test results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17 via Testcontainers)

```
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (16s 218ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (35s 714ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (35s 714ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (16s 218ms)

  total: 189
  failed: 0
  succeeded: 189
  skipped: 0
  duration: 36s 680ms
```

189 passed, 0 failed: 156 unit and 33 integration. The integration tests are 5 smoke, 3 ingest and 7 WebSub callback tests, each run on SQLite and on Postgres, plus 3 SSRF tests through the app's real DI registration.

## What the unit tests cover

| Area | Tests |
|---|---|
| Parser | 17 against 16 hand-written fixtures in `tests/Skimr.UnitTests/Fixtures/feeds`: bad dates, missing GUIDs, CDATA, Windows-1252, mislabelled UTF-8, BOM + control chars, HTML entities, relative URLs, RSS 0.91 DOCTYPE, Atom xml:base/xhtml, RDF, JSON Feed 1.0/1.1, XXE, billion laughs |
| Dedup key | 5 |
| Sanitiser | 15 XSS vectors + 9 behaviour tests |
| SSRF IP policy | 30 addresses (public, private, metadata, IPv4-mapped, NAT64, 6to4) |
| Outbound HTTP | 15 against a local Kestrel: loopback blocked, redirect to metadata, redirect to a private host, mixed DNS answer (rebinding), size limit (with and without Content-Length), redirect limit, timeout, Retry-After, conditional GET, non-http schemes |
| Refresh schedule / refresher / scheduler | backoff, cap, max-age, Retry-After, jitter; ingest, fan-out, dedup, edits keep read state, 304 keeps items seen, errors recorded; concurrency bound, no double-queueing, unexpected errors postpone and are recorded |
| Discovery, WebSub, settings, stored text | alternate links, common paths, input normalisation; subscribe/renew, https-only hubs, HMAC signatures; new config keys; control-character stripping, surrogate-safe truncation |

## Pre-merge review fixes, and proof the NUL test can fail

- NUL (`&#0;` in XML, `\u0000` in JSON) was reaching Postgres text columns. I disabled `StoredText.Fit`'s stripping and re-ran `Nul_and_control_characters_are_stripped_before_storing`: all 4 cases failed, and Postgres threw `22021: invalid byte sequence for encoding "UTF8": 0x00`. With the fix restored, all 4 pass.
- A database error during refresh is now recorded in the feed's `LastError` and `ConsecutiveFailures`, not just logged.
- WebSub no longer sends its secret to `http://` hubs.
