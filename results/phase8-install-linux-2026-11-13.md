# Phase 8: `install.sh` on Debian 12 with systemd (2026-11-13)

Debian 12 container booted with systemd (`--privileged --cgroupns=host`), minimal image (no curl, no ICU),
linux-x64 single-file release folder staged from `dotnet publish -p:PublishProfile=linux-x64`.

| Check | Result |
|---|---|
| First run on minimal Debian | **Failed**: .NET aborted, no ICU. Fixed: install.sh installs the distro's `libicuNN` (+ tzdata) with apt when missing |
| `sudo ./install.sh` | Running and ready in 16 s (including the ICU install); prints the setup code |
| `/setup`, `/manifest.webmanifest` | 200 |
| `systemd-analyze security skimr.service` | Overall exposure 1.5 OK |
| Process user | `skimr` |
| Permissions | `/opt/skimr` root:root 755; `/var/lib/skimr` skimr:skimr 750; `/etc/skimr` root:skimr 750; `skimr.env` root:skimr 640 |
| Data directory | `keys/`, `logs/`, `skimr.db` (+ WAL) under `/var/lib/skimr` |
| Re-run (upgrade) | "Keeping existing settings"; "upgraded and running"; `--port` ignored when settings exist |
| `--uninstall` | Service and `/opt/skimr` removed; data and settings kept |
| Reinstall after uninstall | Existing data and settings reused |
| `--uninstall --purge` | Data, settings and the `skimr` user removed |
| shellcheck | Clean |

The same install, upgrade and uninstall run in CI on `ubuntu-latest` (a VM with real systemd) on every push.
