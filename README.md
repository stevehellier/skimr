# Skimr

A self-hosted RSS/Atom reader in the spirit of Feedly: folders, unread counts, keyboard-driven
reading, Read Later and boards, full-text search, full-article extraction, OPML, and sync with
mobile and desktop apps through the Google Reader and Fever APIs. Optional AI summaries and
"AI feeds" (a plain-English filter over your sources), off by default.

.NET 10, Blazor, SQLite by default or PostgreSQL. Runs in Docker or as a service on Linux,
Windows and macOS. Licensed under the [GNU AGPL v3](LICENSE).

- [Quick start (Docker)](#quick-start-docker)
- [Bare metal](#bare-metal-linux-windows-macos)
- [Configuration](#configuration)
- [Reverse proxy](#reverse-proxy)
- [Sync apps and the API](#sync-apps-and-the-api)
- [AI features](#ai-features)
- [Backup and restore](#backup-and-restore)
- [Upgrades](#upgrades)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

## Quick start (Docker)

No clone needed: download the Compose file and start it.

```sh
mkdir skimr && cd skimr
curl -fsSLO https://raw.githubusercontent.com/stevehellier/skimr/main/deploy/docker-compose.yml
docker compose up -d
docker compose logs skimr | grep "setup code"
```

Open <http://localhost:8080>, enter the setup code, and create the admin account. Then use
**Organise → Import an OPML file**, or **+ Add source** with a site or feed address. Articles
appear within a minute of adding a source.

- Port 8080 taken (or reserved by Windows)? Set `SKIMR_PORT=9480` in your shell or in a `.env` file
  next to `docker-compose.yml`.
- Everything persistent lives in the `skimr-data` volume (`/data` in the container).
- The image is `ghcr.io/stevehellier/skimr`, for `linux/amd64` and `linux/arm64` (Raspberry Pi 4/5).
  `latest` follows every release; pin one with `SKIMR_VERSION=0.1.1` (or `0.1` for that line's fixes).

**PostgreSQL** instead of SQLite:

```sh
curl -fsSLO https://raw.githubusercontent.com/stevehellier/skimr/main/deploy/docker-compose.postgres.yml
curl -fsSL https://raw.githubusercontent.com/stevehellier/skimr/main/deploy/example.env -o .env
# edit .env: set POSTGRES_PASSWORD (openssl rand -base64 24)
docker compose -f docker-compose.postgres.yml up -d
```

## Bare metal (Linux, Windows, macOS)

Each [release](https://github.com/stevehellier/skimr/releases) has self-contained single-file
builds (no .NET install needed): `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`.

### Linux (Debian, Ubuntu and similar, systemd)

```sh
tar xzf skimr-v0.1.0-linux-x64.tar.gz && cd skimr-v0.1.0-linux-x64
sudo ./install.sh                 # --port 9480 to use another port
```

The script:
- creates a `skimr` system user (no login shell);
- installs to `/opt/skimr` (root-owned, read-only to the service);
- keeps data in `/var/lib/skimr` and settings in `/etc/skimr/skimr.env` (mode 0640);
- installs ICU with apt if it's missing;
- enables a hardened systemd unit (`ProtectSystem=strict`, `NoNewPrivileges`, `PrivateTmp`,
  no capabilities, system-call filter);
- waits until Skimr is ready and prints the setup code.

`sudo ./install.sh --uninstall` removes the service and program but keeps your data and settings;
add `--purge` to delete those and the user too. Logs: `journalctl -u skimr`.

### Windows

In an elevated PowerShell ("Run as administrator"), from the unpacked `win-x64` folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass   # this window only: the script isn't signed
.\install.ps1                               # -Port 9480; -WhatIf to see what it would do
```

It installs to `C:\Program Files\Skimr` and keeps data in `C:\ProgramData\Skimr\data`, readable
only by the service, SYSTEM and Administrators. The service runs as the virtual account
`NT SERVICE\Skimr` and restarts on failure. Settings are the service's environment variables:
`.\install.ps1 -Setting @{ 'SKIMR__Retention__Days' = '90' }` adds or changes one and keeps the
rest. `.\install.ps1 -Uninstall` keeps data; add `-Purge` to delete it. Open the port in Windows
Firewall to reach Skimr from other machines.

### macOS

The `osx-arm64` build runs directly (`./Skimr`, then open <http://localhost:8080>). To start it at
login, follow the steps at the top of `local.skimr.plist`. The binary isn't signed, so remove the
quarantine flag first (`xattr -dr com.apple.quarantine`). If macOS still stops it on Apple
Silicon, sign it locally: `codesign --force --sign - ./Skimr`.

## Configuration

Settings come from `appsettings.json`, then environment variables `SKIMR__Section__Key`, then the
command line (`--Section:Key value`); later wins. Every value is checked at startup, and a bad one
stops the app with a message naming the key.

| Key | Default | Meaning |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Sqlite` or `Postgres` |
| `Database:ConnectionString` | `<data>/skimr.db` | Required for Postgres |
| `Database:AutoMigrate` | `true` | Apply database migrations at startup |
| `Storage:DataDirectory` | `data` (next to the app) | Database, keys, favicons, logs |
| `Server:BasePath` | empty | Serve under a sub-path, e.g. `/skimr` |
| `Server:KnownProxies` | empty | Reverse-proxy IPs whose `X-Forwarded-*` headers are trusted |
| `Server:KnownNetworks` | empty | The same, as CIDR ranges (e.g. a Docker network) |
| `Server:PublicUrl` | empty | Public address; enables instant updates from WebSub hubs |
| `Registration:DefaultMode` | `InviteOnly` | `Open`, `InviteOnly` or `Closed` (admins can change it later) |
| `Fetch:Enabled` | `true` | Background fetching |
| `Fetch:Concurrency` | `8` | Feeds fetched at once (1–64) |
| `Fetch:DefaultIntervalMinutes` | `30` | Refresh interval (5–1440) |
| `Fetch:TimeoutSeconds` | `30` | Per request (5–300) |
| `Fetch:MaxResponseMegabytes` | `10` | Largest feed or page fetched (1–100) |
| `Fetch:MaxRedirects` | `5` | 0–10 |
| `Fetch:UserAgent` | `Skimr/1.0 (self-hosted feed reader)` | |
| `Fetch:AllowPrivateNetworkFeeds` | `false` | Allow feeds on your LAN or localhost (cloud metadata addresses stay blocked) |
| `Content:AllowedEmbedHosts` | YouTube, Vimeo | Hosts whose iframes are kept in articles |
| `ImageProxy:Enabled` | `true` | Serve article images through Skimr (hides your IP from image hosts, fixes `http://` images) |
| `Retention:Enabled` | `true` | Remove old read articles |
| `Retention:Days` | `60` | Read, unsaved articles older than this go (7–3650); unread, saved and boarded ones stay |
| `Security:LoginAttemptsPerMinute` | `10` | Per client IP |
| `Security:ApiRequestsPerMinute` | `600` | Per user |
| `Ai:Provider` | `None` | `None`, `Anthropic` or `OpenAICompatible` |
| `Ai:BaseUrl` | Anthropic's API | Required for `OpenAICompatible`, e.g. `http://localhost:11434/v1` (Ollama) |
| `Ai:Model` | (none) | Required when AI is on |
| `Ai:ApiKey` | (none) | Required for Anthropic; set it as an environment variable, never in a file in git |
| `Ai:DailyCallsPerUser` | `500` | Model calls per user per UTC day |
| `Ai:TimeoutSeconds` | `120` | Per AI request |

The listening port is `ASPNETCORE_HTTP_PORTS` (8080 in Docker and in the install scripts).

## Reverse proxy

Put Skimr behind a proxy that terminates HTTPS (needed for installing it as an app and for
secure cookies), and tell Skimr which proxy to trust so it sees the real client IP and scheme.

**Caddy**, with Skimr on the same host:

```
reader.example.com {
    reverse_proxy localhost:8080
}
```

Set `SKIMR__Server__KnownProxies=127.0.0.1`. In Docker, use the proxy's network range instead,
e.g. `SKIMR__Server__KnownNetworks=172.16.0.0/12`.

**nginx**, under a sub-path (`SKIMR__Server__BasePath=/skimr`):

```nginx
location /skimr/ {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;       # Blazor uses a WebSocket
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

(`$connection_upgrade` is the usual `map $http_upgrade $connection_upgrade { default upgrade; '' close; }`.)

## Sync apps and the API

Create a token per app under **Account → API tokens**. Each one can be revoked on its own.

| Use | Server address | User name | Password |
|---|---|---|---|
| NetNewsWire ("FreshRSS" account), Reeder, ReadKit (Google Reader API) | `https://your-host/api/greader` | your Skimr user name | an API token |
| Fever apps (Reeder, Unread) | `https://your-host/api/fever` | your user name, **exact letter case** | an API token created in v0.1.0 or later |
| Scripts | `https://your-host/api/v1` | | `Authorization: Bearer <token>` |

The REST API covers everything the web UI does; its OpenAPI reference is at `/api/docs/` (sign
in first). In sync apps, folders appear as labels and Read Later as starred. Boards and AI feeds
are only in the web UI and REST API.

## AI features

Off unless the admin configures a provider **and** each user turns it on under **Settings**.
Only then is article text sent anywhere.

```sh
# Anthropic
SKIMR__Ai__Provider=Anthropic
SKIMR__Ai__Model=claude-haiku-5-5        # or claude-opus-5-5: better, about 40x the cost
SKIMR__Ai__ApiKey=sk-ant-...

# Ollama or LM Studio on your network (allowed even with private-network feeds off)
SKIMR__Ai__Provider=OpenAICompatible
SKIMR__Ai__BaseUrl=http://192.168.1.20:11434/v1
SKIMR__Ai__Model=llama3.1
```

- **Summarise** (on an article): generated once per article and model, shared by all users.
- **AI feeds** (nav → AI feeds): pick folders or sources and describe what to keep. New articles
  from those sources are checked every couple of minutes, about 20 per model call.
- Each model call counts toward `Ai:DailyCallsPerUser`; calls that fail because the provider is
  down don't.

## Backup and restore

Everything lives in the data directory: the SQLite database, the keys that sign cookies and
image links, favicons and logs. With PostgreSQL, the articles are in the database and the data
directory still holds the keys.

**Docker (SQLite):**

```sh
docker compose stop
docker run --rm -v skimr_skimr-data:/data -v "$PWD":/backup alpine tar czf /backup/skimr-backup.tgz -C /data .
docker compose start
```

Restore by extracting into an empty volume (`tar xzf ... -C /data`) before starting. The
PostgreSQL stack's volume is `skimr-postgres_skimr-data`.

**Linux service:** `sudo systemctl stop skimr`, copy `/var/lib/skimr` and `/etc/skimr`, start again.
**Windows:** stop the Skimr service and copy `C:\ProgramData\Skimr\data`.

**PostgreSQL:** `pg_dump -Fc skimr > skimr.dump`, plus the data directory (for the keys). Restore
with `pg_restore -d skimr skimr.dump`.

Copy the SQLite file only while Skimr is stopped: in WAL mode the `-wal` file holds recent writes.

## Upgrades

Back up first. Database migrations run automatically at startup (`Database:AutoMigrate`).

- **Docker:** `docker compose pull`, then `docker compose up -d` (with a pinned `SKIMR_VERSION`,
  change it first).
- **Linux:** unpack the new release and run `sudo ./install.sh` again. It keeps your data and settings.
- **Windows:** unpack the new release and run `.\install.ps1` again.

See [CHANGELOG.md](CHANGELOG.md) for what changed.

## Troubleshooting

- **Where's the setup code?** It's logged at startup until the first account exists:
  `docker compose ... logs skimr`, `journalctl -u skimr`, or the newest file in `<data>/logs`.
- **"Blocked: … resolves to a private or reserved address"** when adding a feed on your own
  network: set `Fetch:AllowPrivateNetworkFeeds=true`.
- **One `ERR Failed executing DbCommand … __EFMigrationsHistory`** on the first start with
  PostgreSQL: EF Core checking for a table that doesn't exist yet. Harmless.
- **Windows: "ports are not available" from Docker** for ports around 8080: Hyper-V reserves
  ranges (`netsh interface ipv4 show excludedportrange protocol=tcp`). Pick another `SKIMR_PORT`.
- **Linux: "Couldn't find a valid ICU package"**: install your distribution's `libicu`; `install.sh`
  does this on apt-based systems.

## Development

```sh
dotnet build          # 0 warnings required
dotnet test           # Postgres tests need Docker
dotnet run --project src/Skimr.Web
docker build -f deploy/Dockerfile -t ghcr.io/stevehellier/skimr:dev .   # local image; SKIMR_VERSION=dev to use it
```

Design notes, the data model and every decision are in [docs/PLAN.md](docs/PLAN.md). Test runs and
performance measurements are in `results/`. See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a
pull request, and [SECURITY.md](SECURITY.md) to report a vulnerability.
