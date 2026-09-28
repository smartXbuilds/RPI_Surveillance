#!/bin/bash
# DietPi runs this once as root at the end of the first-boot install (it is imported from the
# FAT boot partition to /boot/Automation_Custom_Script.sh). The Pi is online at that point through
# the Wi-Fi from dietpi-wifi.txt.
#
# It sets up the camera app:
#   - camera tools (rpicam-apps), NetworkManager for the hotspot, .NET 10 runtime
#   - the RpiCam web app as a systemd service on port 80
#   - a Wi-Fi hotspot that is the default network; the app can switch to a normal Wi-Fi and back
#
# Inputs, on the FAT partition (mounted at /boot/firmware):
#   rpicam.env    HOTSPOT_SSID, HOTSPOT_PSK, APP_ADMIN_USER, APP_ADMIN_PASSWORD
#   rpicam-app/   publish output of the RpiCam project (framework-dependent, linux-arm64)
#
# Safe to run again by hand after a failure:  bash /boot/Automation_Custom_Script.sh
# The log ends up in /boot/firmware/rpicam-setup.log (readable on a PC when something went wrong).

BOOT=/boot/firmware
ENV_FILE=$BOOT/rpicam.env
APP_SRC=$BOOT/rpicam-app
APP_DIR=/opt/rpicam
DATA_DIR=/var/lib/rpicam
CONF_DIR=/etc/rpicam
DONE_MARKER=$CONF_DIR/.setup-done
LOG=/var/log/rpicam-setup.log
WIFI_IF=wlan0
HOTSPOT_CON=rpicam-hotspot

exec > >(tee -a "$LOG") 2>&1

# tee runs asynchronously; give it a moment to flush before the log is copied.
copy_log() { sleep 1; cp -f "$LOG" "$BOOT/rpicam-setup.log" 2> /dev/null; }
trap copy_log EXIT

step() { echo; echo "=== $(date '+%F %T')  $*"; }
fail() { echo "FEHLER: $*"; exit 1; }

[[ $EUID == 0 ]] || fail "muss als root laufen"
if [[ -f $DONE_MARKER ]]; then
	echo "Setup ist schon durchgelaufen ($DONE_MARKER), nichts zu tun."
	exit 0
fi

export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------------------------
step "Konfiguration lesen"
[[ -f $ENV_FILE ]] || fail "$ENV_FILE fehlt"
[[ -f $APP_SRC/RpiCam.dll ]] || fail "$APP_SRC/RpiCam.dll fehlt (App nicht auf die SD-Karte kopiert?)"

# Read with sed, never `source`: the file is user input and must not be executed.
env_get() { sed -n "s/^$1=//p" "$ENV_FILE" | head -1 | tr -d '\r'; }
HOTSPOT_SSID=$(env_get HOTSPOT_SSID)
HOTSPOT_PSK=$(env_get HOTSPOT_PSK)
APP_ADMIN_USER=$(env_get APP_ADMIN_USER)
APP_ADMIN_PASSWORD=$(env_get APP_ADMIN_PASSWORD)

# systemd's EnvironmentFile would mangle quotes and backslashes.
plain() { [[ -n $1 && $1 != *["'\"\\"]* ]]; }
plain "$HOTSPOT_SSID" || fail "HOTSPOT_SSID fehlt oder enthaelt Anfuehrungszeichen/Backslash"
plain "$HOTSPOT_PSK" || fail "HOTSPOT_PSK fehlt oder enthaelt Anfuehrungszeichen/Backslash"
plain "$APP_ADMIN_USER" || fail "APP_ADMIN_USER fehlt oder enthaelt Anfuehrungszeichen/Backslash"
plain "$APP_ADMIN_PASSWORD" || fail "APP_ADMIN_PASSWORD fehlt oder enthaelt Anfuehrungszeichen/Backslash"
((${#HOTSPOT_PSK} >= 8 && ${#HOTSPOT_PSK} <= 63)) || fail "HOTSPOT_PSK muss 8 bis 63 Zeichen lang sein"
((${#HOTSPOT_SSID} <= 32)) || fail "HOTSPOT_SSID darf hoechstens 32 Zeichen lang sein"
echo "Hotspot-SSID: $HOTSPOT_SSID, App-Benutzer: $APP_ADMIN_USER"

# ---------------------------------------------------------------------------------------------
step "Pakete installieren"
apt-get update || fail "apt-get update fehlgeschlagen (kein Internet?)"

# ICU is needed by .NET for cultures (the UI is German/English); its package name carries the version.
ICU_PKG=$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -1)
[[ -n $ICU_PKG ]] || fail "kein libicu-Paket gefunden"

apt-get install -y network-manager dnsmasq-base nftables iptables avahi-daemon curl ca-certificates "$ICU_PKG" \
	|| fail "Installation von NetworkManager & Co. fehlgeschlagen"
# The lite variant has no preview/GUI dependencies; fall back to the full one if it is not offered.
apt-get install -y rpicam-apps-lite || apt-get install -y rpicam-apps || fail "rpicam-apps nicht installierbar"
command -v rpicam-still > /dev/null || fail "rpicam-still fehlt nach der Installation"

# ---------------------------------------------------------------------------------------------
step ".NET 10 (ASP.NET Core Runtime) installieren"
if ! dotnet --list-runtimes 2> /dev/null | grep -q '^Microsoft.AspNetCore.App 10\.'; then
	curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh || fail "dotnet-install.sh nicht ladbar"
	bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet || fail ".NET Installation fehlgeschlagen"
	ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
	rm -f /tmp/dotnet-install.sh
fi
dotnet --list-runtimes | grep '^Microsoft.AspNetCore.App 10\.' || fail "ASP.NET Core 10 Runtime nicht gefunden"

# ---------------------------------------------------------------------------------------------
step "App und Konfiguration installieren"
install -d -m 755 "$APP_DIR"
cp -a "$APP_SRC"/. "$APP_DIR"/ || fail "App konnte nicht kopiert werden"
install -d -m 700 "$DATA_DIR" "$CONF_DIR"

# Root-only: contains the hotspot and login passwords.
(
	umask 077
	cat > "$CONF_DIR/rpicam.env" << EOF
ASPNETCORE_URLS=http://0.0.0.0:80
ASPNETCORE_ENVIRONMENT=Production
DOTNET_gcConcurrent=0
RpiCam__DataDir=$DATA_DIR
RpiCam__WifiInterface=$WIFI_IF
RpiCam__HotspotConnection=$HOTSPOT_CON
RpiCam__HotspotSsid=$HOTSPOT_SSID
RpiCam__HotspotPassword=$HOTSPOT_PSK
Auth__User=$APP_ADMIN_USER
Auth__Password=$APP_ADMIN_PASSWORD
EOF
)

# Root because the app calls nmcli and sets the system clock, and needs the camera devices.
cat > /etc/systemd/system/rpicam.service << 'EOF'
[Unit]
Description=RPi camera web app
After=NetworkManager.service
Wants=NetworkManager.service

[Service]
Type=simple
WorkingDirectory=/opt/rpicam
EnvironmentFile=/etc/rpicam/rpicam.env
ExecStart=/usr/local/bin/dotnet /opt/rpicam/RpiCam.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable rpicam.service || fail "Dienst konnte nicht aktiviert werden"

# ---------------------------------------------------------------------------------------------
step "NetworkManager und Hotspot einrichten"
# Reliable AP mode: no Wi-Fi power saving, no MAC randomisation while scanning.
cat > /etc/NetworkManager/conf.d/rpicam.conf << 'EOF'
[connection]
wifi.powersave=2

[device]
wifi.scan-rand-mac-address=no
EOF

# wpa_supplicant runs as the system service for NetworkManager (DietPi only used it through ifupdown).
systemctl unmask wpa_supplicant.service 2> /dev/null
systemctl enable wpa_supplicant.service 2> /dev/null
systemctl enable NetworkManager.service
systemctl restart NetworkManager.service
for _ in {1..20}; do nmcli -t general status &> /dev/null && break; sleep 1; done
nmcli -t general status > /dev/null || fail "NetworkManager antwortet nicht"

# Safety net: the app also creates/updates this profile at startup, but if it ever fails to start
# the hotspot must still come up so the device stays reachable.
nmcli connection delete id "$HOTSPOT_CON" &> /dev/null
nmcli connection add type wifi ifname "$WIFI_IF" con-name "$HOTSPOT_CON" ssid "$HOTSPOT_SSID" \
	connection.autoconnect yes connection.autoconnect-priority 0 \
	802-11-wireless.mode ap 802-11-wireless.band bg 802-11-wireless.channel 6 \
	ipv4.method shared ipv4.addresses 192.168.4.1/24 ipv6.method disabled \
	wifi-sec.key-mgmt wpa-psk wifi-sec.proto rsn wifi-sec.pairwise ccmp wifi-sec.group ccmp \
	wifi-sec.psk "$HOTSPOT_PSK" || fail "Hotspot-Profil konnte nicht angelegt werden"

# ---------------------------------------------------------------------------------------------
step "Kamera pruefen (nur zur Info)"
rpicam-hello --list-cameras 2>&1 | head -20 || true

# ---------------------------------------------------------------------------------------------
step "Zugangsdaten von der SD-Karte entfernen"
rm -f "$ENV_FILE"
rm -rf "$APP_SRC"
# DietPi imported dietpi-wifi.txt into the root filesystem; the home Wi-Fi was only needed for this install.
if [[ -f /boot/dietpi-wifi.txt ]]; then
	sed -i -E "s/^(aWIFI_(SSID|KEY)\[[0-9]+\])=.*/\1=''/" /boot/dietpi-wifi.txt
fi

# ---------------------------------------------------------------------------------------------
step "WLAN an NetworkManager uebergeben"
# DietPi configures wlan0 through ifupdown. Leaving that in place would fight NetworkManager for
# the interface. Keep a copy outside of interfaces.d, which is sourced with a wildcard.
if [[ -f /etc/network/interfaces.d/$WIFI_IF.conf ]]; then
	mv -f "/etc/network/interfaces.d/$WIFI_IF.conf" "$CONF_DIR/$WIFI_IF.conf.dietpi-original"
fi

touch "$DONE_MARKER"
step "Fertig. Neustart in 45 Sekunden, danach startet der Hotspot '$HOTSPOT_SSID'."
echo "Die App ist dann unter http://192.168.4.1 erreichbar."
copy_log
# Delayed, so DietPi can finish its own first-boot steps after this script returns.
systemd-run --on-active=45 --unit=rpicam-final-reboot systemctl reboot
exit 0
