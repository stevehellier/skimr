# Phase 4 reader UI: test results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17, Playwright Chromium)

```
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64) passed (15s 074ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (17s 233ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64) passed (31s 595ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 19s 575ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.ComponentTests\bin\Debug\net10.0\Skimr.ComponentTests.dll (net10.0|x64) passed (15s 074ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 19s 575ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.E2ETests\bin\Debug\net10.0\Skimr.E2ETests.dll (net10.0|x64) passed (31s 595ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (17s 233ms)

  total: 359
  failed: 0
  succeeded: 359
  skipped: 0
  duration: 1m 20s 590ms
```

359 passed, 0 failed: 234 unit, 106 integration (SQLite and Postgres), 18 component (bUnit), 1 E2E (Playwright).

## Acceptance criteria

| Criterion | How it was checked | Result |
|---|---|---|
| Nav unread counts, article list | bUnit: `NavPanelTests` (6), `ArticleListTests` (6), `ReaderPageTests` (6, the whole page on real services) | Pass |
| Every operation scoped to its user | `Another_user_cannot_read_or_change_anything_of_mine`: 5 reads and 11 writes by user B on user A's ids, on SQLite and Postgres | Pass |
| Back-fill, concurrent subscribe, unsubscribe keeps saved, keyset paging, folder/today counts | `ReaderServiceTests` (11 × 2 providers) | Pass |
| A real feed fetched inside the running app (Phase 2 gap) | `EndToEndFetchTests`: subscribe, scheduler fetches over real HTTP, article appears sanitised | Pass |
| Live circuit runs with no CSP violations | Playwright: every browser console error fails the test; the interactive nav rendering proves the WebSocket connected | **Pass, real browser** (headless Chromium) |
| Keyboard-only use | Playwright: j, k, m, s, ?, Esc, Shift+A, g s; focus moves to the article heading on open and back to the list item on close | **Pass, real browser** |
| Phone width | Playwright at 390×844: nav hidden, drawer opens and closes, article full-screen, no horizontal overflow | **Pass, real browser** |
| Theme persists | Playwright: dark survives a reload (cookie + server-rendered `data-theme`) | **Pass, real browser** |
| AA contrast | `ContrastTests`: 21 pairings × 2 themes computed from `app.css` tokens (table below) | Pass |
| No inline style or script in components | `CspGuardTests` over every `.razor` file | Pass |

Not checked: a screen reader pass (NVDA/VoiceOver), and real phone hardware (only Chromium's mobile emulation was used).

## Bugs the E2E test found (each fixed and now covered)

1. Articles from a newly added source never appeared without a manual reload. Navigating to the new source creates a new page instance, and the old one held the polling task. Empty source views now poll for up to 30 s.
2. Pressing Escape to close a dialog also closed the open article. While a dialog is open, it now owns the keyboard.
3. On a phone the nav drawer covered its own toggle, so it couldn't be closed. A Close button was added inside the drawer.

## Contrast (WCAG 2.2 AA: text 4.5:1, input borders and focus ring 3:1)

| Pairing | Needs | Light | Dark |
|---|---|---|---|
| `--text` on `--bg` | 4.5:1 | 16.88:1 | 14.90:1 |
| `--text` on `--surface` | 4.5:1 | 15.34:1 | 13.43:1 |
| `--text` on `--selected` | 4.5:1 | 14.28:1 | 10.76:1 |
| `--unread` on `--bg` | 4.5:1 | 16.88:1 | 16.30:1 |
| `--read` on `--bg` | 4.5:1 | 5.60:1 | 7.47:1 |
| `--read` on `--selected` | 4.5:1 | 4.74:1 | 5.40:1 |
| `--muted` on `--bg` | 4.5:1 | 6.95:1 | 8.01:1 |
| `--muted` on `--surface` | 4.5:1 | 6.31:1 | 7.22:1 |
| `--muted` on `--selected` | 4.5:1 | 5.88:1 | 5.79:1 |
| `--accent` on `--bg` | 4.5:1 | 6.50:1 | 8.87:1 |
| `--accent` on `--surface` | 4.5:1 | 5.90:1 | 8.00:1 |
| `--accent-text` on `--accent` | 4.5:1 | 6.50:1 | 8.79:1 |
| `--danger` on `--bg` | 4.5:1 | 6.69:1 | 7.59:1 |
| `--text` on `--ok-bg` | 4.5:1 | 14.69:1 | 11.17:1 |
| `--text` on `--error-bg` | 4.5:1 | 14.20:1 | 12.18:1 |
| `--control-border` on `--bg` | 3:1 | 3.98:1 | 4.99:1 |
| `--focus` on `--bg` | 3:1 | 6.61:1 | 8.60:1 |

## Screenshots (headless Chromium)

`results/phase4-e2e/`: `desktop-list`, `desktop-reading`, `desktop-dark`, `phone-list`, `phone-nav`, `phone-reading`.
