# Focus-return flake (#27), 2026-11-14

## CI failure (run 43508800067, main)

`ReaderE2ETests.cs:195`: `ActiveElementIdAsync(page)` was `""`, expected `"article-1"` ("closing returns focus to the
article in the list"). The test read `document.activeElement` once, with no retry.

## Red, then green (temporary init script delaying every `HTMLElement.focus()` by 500 ms; not committed)

- Old code: **failed** at the first one-off read (line 168/169, "opening an article moves focus to it"), with the same
  failure shape as CI: an empty active element.
- New code (all three reads replaced with `Assertions.Expect(...).ToBeFocusedAsync()`): **passed** with the delay.

## After removing the delay

`dotnet build`: 0 warnings, 0 errors.
E2E suite: total 4, failed 0, succeeded 3, skipped 1 (QuickStart, needs a deployment).
