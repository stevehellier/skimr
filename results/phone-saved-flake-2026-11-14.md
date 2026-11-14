# Phone Saved list flake (#23), 2026-11-14

## CI failure (run 43517420666, attempt 1)

`ReaderE2ETests.cs:282`: `Locator(".article-item")` expected count 1, "14 × locator resolved to 0 elements"
over the default 5 s. Re-run passed.

## CPU ruled out

The phone page was throttled with `Emulation.setCPUThrottlingRate`, and the time from `GotoAsync` to the
list appearing was measured:

| Throttle | Items after |
|---|---|
| 1× | 212 ms, 339 ms, 321 ms |
| 6× | 750 ms |
| 20× | 894 ms |

Even at 20× the list shows in under 1 s, so a slow CPU can't explain 5 s of nothing. The likely
cause is a slow start for the new context's circuit (the WebSocket or negotiate step); this is unconfirmed.

## Red, then green (temporary `RouteAsync("**/_blazor/negotiate*")` delay, not committed)

- 6 s delay, original assertion: **failed**, the same way as CI ("Locator expected to have count '1'. But was: '0'").
- 6 s delay, new code (wait up to 20 s for `html[data-reader-ready]`, then the count): **passed**.
- 30 s delay, new code: failed with a diagnostic message: `Phone Saved list didn't load. Url: http://127.0.0.1:12487/saved;
  reader ready: False; body: Skip to content ...`, and wrote `phone-list-failed.png` (CI uploads
  screenshots with `if: always()`).

## Full suite (delay removed)

`dotnet build`: 0 warnings, 0 errors.
`dotnet test`: total 628, failed 0, succeeded 627, skipped 1 (QuickStart, needs a deployment), 3m 44s.

## Not covered

Run 43508800067 (main) failed in the same test at line 195: focus didn't return to `article-1` after
closing. This is a separate flake and isn't in this change.
