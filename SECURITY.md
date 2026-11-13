# Security policy

## Supported versions

Skimr is pre-1.0. Only the latest release gets security fixes; upgrade to it before reporting.

| Version | Supported |
|---|---|
| 0.1.x (latest) | Yes |
| Older | No |

## Reporting a vulnerability

**Don't open a public issue, pull request or discussion for a security problem.**

- **Preferred:** use GitHub's private vulnerability reporting: the repository's **Security** tab →
  **Report a vulnerability**. GitHub offers this only on public repositories; while this one is
  private, use the next option.
- **While the repository is private:** contact the maintainer, stevehellier, directly on GitHub and
  ask for a private channel. Don't include details until you have one.

Please include:

- the Skimr version (shown in the release name or `CHANGELOG.md`) and how it's deployed (Docker,
  `install.sh`, Windows service, behind which reverse proxy);
- the database (SQLite or PostgreSQL) and any non-default settings that matter;
- steps to reproduce, and what an attacker gains;
- whether you've shared it with anyone else.

You'll get an acknowledgement, a fix or a reasoned decision, and credit in the release notes if you
want it. Please give a fix time to ship before disclosing publicly.

## Scope

In scope: anything that breaks the guarantees below on a default install, for example:

- one user reading or changing another user's articles, sources, boards, AI feeds or tokens;
- script running in the reader from feed, extracted or AI-generated content (stored XSS);
- an outbound request reaching a private, loopback or cloud-metadata address without the admin
  allowing it (SSRF), including through redirects, DNS rebinding, favicons, the image proxy or full-text extraction;
- XML external entity or DTD processing;
- signing in, using the API or a sync API (Google Reader, Fever) without valid credentials;
  API tokens readable from the database; cookies accepted on the API;
- article text sent to an AI provider for a user who hasn't turned AI on;
- privilege escalation on the host from the Docker image, the systemd unit or the Windows service.

Out of scope, or working as designed:

- LAN access that an admin enabled: `Fetch:AllowPrivateNetworkFeeds`, or a private `Ai:BaseUrl`
  (PLAN D24, D63);
- anything that needs an admin account, or write access to the server or its data directory;
- the `/api/docs` page's looser Content-Security-Policy (inline script is required by Scalar; PLAN D58);
- denial of service by volume, missing rate limits on anonymous pages that do no work, and
  self-XSS;
- reports from automated scanners without a working proof of concept;
- vulnerabilities in dependencies with no path to exploit them through Skimr (report those upstream).

## How Skimr is meant to be secure

The rules every change must keep (see `CLAUDE.md` and the decisions in `docs/PLAN.md` §4):

- All feed and extracted HTML is sanitised **before** it's stored; model output is shown as text only.
- Strict Content-Security-Policy: no inline script or style in our pages.
- Every outbound fetch goes through one SSRF-guarded client: private networks blocked unless enabled,
  cloud metadata always blocked, size, time and redirect limits.
- XML readers ignore DTDs and have no resolver.
- API tokens are stored as SHA-256 only and shown once; the REST API takes bearer tokens only.
- Every service call is filtered by the signed-in user; ids from the client are never trusted.
- Login and API rate limits, account lockout, and disabled accounts' sessions and tokens stop at once.

## Deploying safely

- Put Skimr behind HTTPS (a reverse proxy) and set `Server:KnownProxies` or `Server:KnownNetworks`
  so forwarded headers are trusted only from your proxy.
- Keep registration `InviteOnly` or `Closed` unless you mean to run an open server.
- Keep secrets (database password, `Ai:ApiKey`) in environment variables or `/etc/skimr/skimr.env`
  (mode 0640), never in a file in git.
- Back up the data directory: it holds the keys that sign cookies and image links.
