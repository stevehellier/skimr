# Phase 3 auth and multi-user: test results (2026-11-13)

## dotnet test (local, Windows, SDK 10.0.401, Docker Desktop for Postgres 17 via Testcontainers)

```nRunning tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64)
Running tests from C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64)
C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (14s 297ms)
C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 05s 813ms)

Test run summary: Passed!
  C:\Users\Steve\Projects\Skimr\tests\Skimr.UnitTests\bin\Debug\net10.0\Skimr.UnitTests.dll (net10.0|x64) passed (14s 297ms)
  C:\Users\Steve\Projects\Skimr\tests\Skimr.IntegrationTests\bin\Debug\net10.0\Skimr.IntegrationTests.dll (net10.0|x64) passed (1m 05s 813ms)

  total: 252
  failed: 0
  succeeded: 252
  skipped: 0
  duration: 1m 06s 724ms
```

252 passed, 0 failed: 169 unit and 83 integration. Each auth test gets its own empty database (a new SQLite file, or a new Postgres database in the shared container) and runs on both providers.

## What the auth tests prove (each on SQLite and Postgres)

| Requirement | Test |
|---|---|
| Pages redirect to the wizard until setup; health and API don't | `Before_setup_pages_redirect_to_the_wizard_but_health_and_api_do_not` |
| Wrong setup code creates nothing | `Setup_rejects_a_wrong_code` |
| Setup creates a signed-in admin, stores the mode, then closes; the code is single-use | `Setup_creates_a_signed_in_admin_and_then_closes` |
| Two simultaneous setups create one admin | `Concurrent_setup_submissions_create_exactly_one_admin` |
| 5 wrong passwords lock the account, even against the right password | `Five_wrong_passwords_lock_the_account` |
| `ReturnUrl` can't redirect off-site (`https://`, `//`, `/\`) | `Return_url_never_leaves_the_site` (3 cases) |
| Closed, invite-only and open registration behave as configured; an invite works once | `Closed_registration…`, `Invite_only…`, `Open_registration…` |
| One invite used twice at once makes one account | `One_invite_used_by_two_people_at_once_creates_one_account` |
| Token works on the API until revoked; only the hash is stored | `Token_from_the_account_page_works_on_the_api_until_revoked` |
| The API ignores the sign-in cookie | `The_api_ignores_the_sign_in_cookie` |
| Disabled accounts' tokens and expired tokens stop working at once | `Tokens_stop_working_when_the_account_is_disabled_or_the_token_expires` |
| Non-admins can't open or post to the admin page | `Non_admins_cannot_open_or_post_to_the_admin_page` |
| The last admin can't be removed; nobody can act on themselves | `The_last_admin_cannot_be_removed_and_nobody_can_act_on_themselves` |
| Disabling a user ends their open session | `Disabling_a_user_ends_their_session` |
| Login POSTs are rate-limited (429); page views aren't | `RateLimitTests` (SQLite) |

## Docker (local, linux/amd64)

A fresh container reached `healthy`; `docker logs` showed `No accounts exist yet. Open /setup in your browser and enter this setup code: CBE3-8WKZ-YQBZ`; `GET /` returned 302 to `/setup`; `/setup` rendered the form.

## Pre-merge review fixes (each test was seen failing before its fix)

| Defect | Test that now covers it |
|---|---|
| The API rate limit ran before the bearer token was authenticated, so it was per IP, not per user | `Api_rate_limit_is_per_user_not_per_address`: Alice gets 429 after 10 calls; Bob still gets 200 |
| A temporary lockout (5 wrong passwords) also rejected that user's API tokens and open sessions, so anyone knowing a user name could keep their sync apps offline | `A_temporary_lockout_does_not_break_the_users_tokens`. Only *disabled* accounts are rejected now |
| A forged sign-out (no antiforgery token) returned 500 | `Sign_out_needs_the_antiforgery_token_and_ends_the_session`: forged = 400 and the user stays signed in; real = signed out |
| The Docker image had no `_framework/` folder (no `blazor.web.js`), and had been missing it since Phase 1. The cached restore ran before the `.razor` sources were copied in, and `--no-restore` publish reused it | The CI Docker smoke test now fetches every script and stylesheet on `/setup` from the published image and needs a 200 for each. Locally after the fix: all 4 assets returned 200, including `_framework/blazor.web.whefm3rrpu.js` |
| Scoped CSS wasn't served in the test host | `Signed_out_visitors_can_load_the_login_page_assets` (the test factory enables static web assets) |