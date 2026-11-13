# Phase 1 skeleton: test and smoke results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17 via Testcontainers)

```
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (1s 837ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (11s 414ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (11s 414ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (1s 837ms)

  total: 19
  failed: 0
  succeeded: 19
  skipped: 0
  duration: 11s 987ms
```

19 passed, 0 failed: 9 unit (settings validation; one theory has 4 cases) and 10 integration (5 smoke tests × SQLite and Postgres).

## Docker (local, linux/amd64)

- `docker build -f deploy/Dockerfile -t skimr:dev .`: succeeded, image 252 MB (chiseled-extra base)
- `docker run`: health status reached `healthy`; `GET /health/ready`: 200
- `Skimr --healthcheck`: exit 0; with a 404 URL: exit 1

## Real host on Postgres (outside WebApplicationFactory)

`dotnet run` against a throwaway `postgres:17-alpine` with no `skimr` database: the database was created, migrations applied, and `/health/ready` returned 200.

- Before the fix, EF logged `ERR An error occurred using the connection to database 'skimr'` while it probed a database that didn't exist yet. Fixed: startup now calls `IRelationalDatabaseCreator.ExistsAsync`/`CreateAsync` first.
- One `ERR Failed executing DbCommand ... FROM "__EFMigrationsHistory"` remains, on first start only. EF queries the history table before creating it. It's harmless and will be documented in the README (Phase 8). Not suppressed, because a log filter would also hide real EF errors.
