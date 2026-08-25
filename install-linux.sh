#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP_DIR="${INSTALL_DIR:-/opt/driving-license-reminder}"
SERVICE_NAME="driving-license-reminder"

echo "Ставлю .NET 9 runtime, если его нет..."
if ! command -v dotnet >/dev/null 2>&1; then
  wget -q https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
  sudo dpkg -i /tmp/packages-microsoft-prod.deb
  sudo apt-get update
  sudo apt-get install -y aspnetcore-runtime-9.0 dotnet-sdk-9.0
fi

echo "Собираю linux-x64..."
dotnet publish "$ROOT/DrivingLicenseReminder.csproj" -c Release -r linux-x64 --self-contained false -o "$ROOT/publish"

sudo mkdir -p "$APP_DIR"
sudo cp -a "$ROOT/publish/." "$APP_DIR/"
if [[ -f "$ROOT/appsettings.Local.json" ]]; then
  sudo cp "$ROOT/appsettings.Local.json" "$APP_DIR/"
fi

if [[ -z "${TELEGRAM_BOT_TOKEN:-}" && ! -f "$APP_DIR/appsettings.Local.json" ]]; then
  echo
  echo "Задайте токен перед systemd:"
  echo "  sudo mkdir -p $APP_DIR"
  echo "  echo '{ \"Telegram\": { \"BotToken\": \"123:ABC\" } }' | sudo tee $APP_DIR/appsettings.Local.json"
  echo "Или: export TELEGRAM_BOT_TOKEN=123:ABC && sudo -E ./install-linux.sh"
  echo
fi

UNIT="/etc/systemd/system/${SERVICE_NAME}.service"
sudo tee "$UNIT" >/dev/null <<EOF
[Unit]
Description=KEP Engomi driving license slot watcher
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=/usr/bin/dotnet $APP_DIR/DrivingLicenseReminder.dll
Restart=always
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production
Environment=TELEGRAM_BOT_TOKEN=${TELEGRAM_BOT_TOKEN:-}
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF

echo
echo "Проверка сайта с этой машины (без Telegram):"
sudo -u "$(id -un)" /usr/bin/dotnet "$APP_DIR/DrivingLicenseReminder.dll" --check-once || true

sudo systemctl daemon-reload
echo
echo "Дальше:"
echo "  1. Поднимите кипрский VPN на этой Ubuntu (без него сайт часто не открывается)."
echo "  2. sudo systemctl enable --now ${SERVICE_NAME}"
echo "  3. journalctl -u ${SERVICE_NAME} -f"
echo "  4. Напишите боту /start"
echo
echo "VPN: лучше WireGuard/OpenVPN клиента вашего провайдера на этой ВМ."
echo "Проверка IP: curl https://ifconfig.me   и   curl -I https://kep-kepo.gov.cy/appointments/"
