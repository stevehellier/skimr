#!/usr/bin/env bash
# Installs, upgrades or removes Skimr as a systemd service on Debian/Ubuntu (and similar).
# Run it from an unpacked release folder (Skimr, wwwroot/, appsettings.json, skimr.service):
#   sudo ./install.sh [--port 8080]        install, or upgrade in place (keeps data and config)
#   sudo ./install.sh --uninstall          remove the service and program; keep data and config
#   sudo ./install.sh --uninstall --purge  also delete /var/lib/skimr, /etc/skimr and the user
set -euo pipefail

APP_DIR=/opt/skimr
DATA_DIR=/var/lib/skimr
CONFIG_DIR=/etc/skimr
CONFIG_FILE=$CONFIG_DIR/skimr.env
UNIT=/etc/systemd/system/skimr.service
SERVICE_USER=skimr
SRC_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

port=8080
uninstall=false
purge=false

usage() { sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
fail() { echo "install.sh: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) [[ $# -ge 2 && "$2" =~ ^[0-9]+$ && "$2" -ge 1 && "$2" -le 65535 ]] || fail "--port needs a number from 1 to 65535"; port=$2; shift 2 ;;
    --uninstall) uninstall=true; shift ;;
    --purge) purge=true; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 1 ;;
  esac
done

[[ $EUID -eq 0 ]] || fail "run with sudo (it creates a system user and a systemd service)"
command -v systemctl >/dev/null && [[ -d /run/systemd/system ]] || fail "systemd isn't running on this machine"
$purge && ! $uninstall && fail "--purge only goes with --uninstall"

if $uninstall; then
  systemctl disable --now skimr.service 2>/dev/null || true
  rm -f "$UNIT"
  systemctl daemon-reload
  rm -rf "$APP_DIR" /var/cache/skimr
  if $purge; then
    rm -rf "$DATA_DIR" "$CONFIG_DIR"
    id "$SERVICE_USER" >/dev/null 2>&1 && userdel "$SERVICE_USER"
    echo "Skimr removed, including its data and settings."
  else
    echo "Skimr removed. Data kept in $DATA_DIR and settings in $CONFIG_DIR (add --purge to delete them)."
  fi
  exit 0
fi

[[ -x "$SRC_DIR/Skimr" && -d "$SRC_DIR/wwwroot" && -f "$SRC_DIR/skimr.service" ]] \
  || fail "run this from an unpacked Skimr release folder (Skimr, wwwroot/ and skimr.service next to it)"

# .NET needs ICU (culture-aware text, as in the Docker image); minimal installs lack it.
if ! ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
  if command -v apt-get >/dev/null; then
    echo "Installing ICU (libicu), which .NET needs..."
    apt-get update -qq
    icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n1)
    [[ -n "$icu" ]] || fail "couldn't find a libicu package; install ICU and run this again"
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$icu" tzdata >/dev/null
  else
    fail "ICU isn't installed; install your distribution's libicu (or icu-libs) package and run this again"
  fi
fi

# A dedicated account with no login and no home outside the data directory.
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

upgrade=false
if systemctl is-active --quiet skimr.service; then
  upgrade=true
  systemctl stop skimr.service
fi

# Program files: replaced as a whole, owned by root, read-only to the service.
staging=$(mktemp -d /opt/.skimr-new.XXXXXX)
trap 'rm -rf "$staging"' EXIT
cp -a "$SRC_DIR"/. "$staging"/
rm -f "$staging/install.sh" "$staging/install.ps1" "$staging/skimr.service"
chown -R root:root "$staging"
chmod -R u=rwX,go=rX "$staging"
rm -rf "$APP_DIR"
mv "$staging" "$APP_DIR"
trap - EXIT

install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_USER" "$DATA_DIR"
install -d -m 0750 -o root -g "$SERVICE_USER" "$CONFIG_DIR"
if [[ ! -f "$CONFIG_FILE" ]]; then
  cat > "$CONFIG_FILE" <<CONF
# Skimr settings: SKIMR__Section__Key=value (see the README's configuration table).
# Restart after changes: sudo systemctl restart skimr
ASPNETCORE_HTTP_PORTS=$port
# Behind a reverse proxy, list its address so client IPs and https are seen correctly:
# SKIMR__Server__KnownProxies=127.0.0.1
# SKIMR__Server__BasePath=/skimr
CONF
  chown root:"$SERVICE_USER" "$CONFIG_FILE"
  chmod 0640 "$CONFIG_FILE"
else
  echo "Keeping existing settings in $CONFIG_FILE."
  port=$(sed -n 's/^ASPNETCORE_HTTP_PORTS=\([0-9]*\).*/\1/p' "$CONFIG_FILE" | head -1)
  port=${port:-8080}
fi

install -m 0644 "$SRC_DIR/skimr.service" "$UNIT"
systemctl daemon-reload
systemctl enable --now skimr.service >/dev/null 2>&1 || fail "systemd couldn't start Skimr; see: journalctl -u skimr"

# Readiness endpoint over bash's /dev/tcp, so it works without curl or wget.
probe() {
  { exec 3<>"/dev/tcp/127.0.0.1/$port" && printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -n1 <&3; } 2>/dev/null
}

ready=false
for _ in $(seq 1 60); do
  if status=$(probe) && [[ "$status" == *" 200 "* ]]; then
    ready=true
    break
  fi
  sleep 1
done

if ! $ready; then
  echo "Skimr didn't become ready on port $port. Recent log:" >&2
  journalctl -u skimr.service -n 30 --no-pager >&2 || true
  exit 1
fi

if $upgrade; then
  echo "Skimr upgraded and running on port $port."
else
  echo "Skimr is running on port $port (http://$(hostname -f 2>/dev/null || hostname):$port)."
  code=$(journalctl -u skimr.service --no-pager 2>/dev/null | grep -o 'setup code: [A-Z0-9-]*' | tail -n1 || true)
  [[ -n "$code" ]] && echo "Open /setup in your browser and enter the $code"
fi
