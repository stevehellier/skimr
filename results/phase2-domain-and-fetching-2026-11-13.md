# Phase 2 domain and fetching: test results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17 via Testcontainers)

```
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (17s 074ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (38s 600ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (17s 074ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (38s 600ms)

  total: 178
  failed: 0
  succeeded: 178
  skipped: 0
  duration: 39s 476ms
```

178 passed, 0 failed: 152 unit and 26 integration (5 smoke, 1 ingest and 7 WebSub callback tests, each run on SQLite and on Postgres).

## What the unit tests cover

| Area | Tests |
|---|---|
| Parser | 17 against 16 hand-written fixtures in `tests/Skimr.UnitTests/Fixtures/feeds`: bad dates, missing GUIDs, CDATA, Windows-1252, mislabelled UTF-8, BOM + control chars, HTML entities, relative URLs, RSS 0.91 DOCTYPE, Atom xml:base/xhtml, RDF, JSON Feed 1.0/1.1, XXE, billion laughs |
| Dedup key | 5 |
| Sanitiser | 15 XSS vectors + 9 behaviour tests |
| SSRF IP policy | 30 addresses (public, private, metadata, IPv4-mapped, NAT64, 6to4) |
| Outbound HTTP | 15 against a local Kestrel: loopback blocked, redirect to metadata, redirect to a private host, mixed DNS answer (rebinding), size limit (with and without Content-Length), redirect limit, timeout, Retry-After, conditional GET, non-http schemes |
| Refresh schedule / refresher / scheduler | backoff, cap, max-age, Retry-After, jitter; ingest, fan-out, dedup, edits keep read state, 304 keeps items seen, errors recorded; concurrency bound, no double-queueing, unexpected errors postpone |
| Discovery, WebSub, settings | alternate links, common paths, input normalisation; subscribe/renew, HMAC signatures; new config keys |
