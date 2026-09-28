# RpiCam

Kamera-Weboberfläche für einen Raspberry Pi 3 A+ (DietPi, Raspberry-Pi-Kamera). Blazor Server, .NET 10, MudBlazor 9,
gebaut aus dem `BlazorServerTemplate`.

- **Galerie:** Aufnahmen ansehen, einzeln oder gesammelt löschen, einzeln oder als ZIP herunterladen, manuell oder im Intervall aufnehmen.
- **WLAN:** Der Pi ist standardmäßig ein **Hotspot**. Die App scannt nach Netzwerken, verbindet nach Passworteingabe und wechselt dabei vom Hotspot in den Client-Modus.
  Schlägt das fehl, startet der Hotspot automatisch wieder.
- **Einstellungen:** Aufnahme (Intervall, Auflösung, Qualität), gespeicherte Netzwerke **entfernen** (das aktive Netzwerk entfernen = zurück zum Hotspot),
  Systemzeit vom Browser übernehmen (ohne Internet hat der Pi keine Uhr), Sprache (Deutsch/Englisch).
- Alles hinter einem Login (ein Admin-Konto, Cookie).

## Entwickeln

```bash
dotnet run          # http://localhost:5018, Login dev / dev
```

Im Development-Modus läuft die App im **Simulationsmodus** (`RpiCam:Simulate=true`): falsche Kamera (Farbverläufe) und ein
simuliertes Netzwerk. Es wird nie `nmcli` aufgerufen, das WLAN des Entwicklungsrechners bleibt also unberührt.
Ein Passwort, das mit `wrong` beginnt, lässt die simulierte Verbindung fehlschlagen.

## Auf den Pi bringen

1. DietPi-Image auf die SD-Karte schreiben (Raspberry-Pi-Variante, ARMv8).
2. Auf der Boot-Partition (FAT) eintragen:
   - `dietpi-wifi.txt`: Heim-WLAN (`aWIFI_SSID[0]`, `aWIFI_KEY[0]`). Nur für die Installation beim ersten Start nötig.
   - `dietpi.txt`: `AUTO_SETUP_AUTOMATED=1`, `AUTO_SETUP_NET_WIFI_ENABLED=1`, `AUTO_SETUP_NET_WIFI_COUNTRY_CODE=DE`, Passwort, Hostname (`rpicam`).
   - `rpicam.env`: `HOTSPOT_SSID`, `HOTSPOT_PSK` (8-63 Zeichen), `APP_ADMIN_USER`, `APP_ADMIN_PASSWORD`. Optional `CAMERA_OVERLAY`
     (Standard `ov5647` - die AZ-Delivery-V1.3-Kamera, für die dieses Projekt gebaut ist; bei einer anderen Kamera z.B. `imx708`
     eintragen). Keine Anführungszeichen und keine Backslashes.
   - `config.txt`: nichts Kameraspezifisches nötig, das Setup-Skript trägt `dtoverlay=vc4-kms-v3d,cma-128` und den
     Sensor-Overlay aus `CAMERA_OVERLAY` selbst ein (siehe unten, "Bekannte Grenzen").
   - `cmdline.txt`: am Ende der einen Zeile ` cfg80211.ieee80211_regdom=DE`.
3. `deploy/Automation_Custom_Script.sh` auf die Boot-Partition kopieren und die App dorthin veröffentlichen:
   ```bash
   dotnet publish RpiCam.csproj -c Release -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false -o publish
   cp -r publish /media/$USER/<boot-partition>/rpicam-app
   ```
   Architekturneutral, nicht mit `-r linux-arm64`: Das Paket ist reine verwaltete Bibliotheken und läuft so auch auf dem Entwicklungsrechner (Rauchtest).
4. SD-Karte in den Pi, Strom an. Beim ersten Start (ca. 10-20 Minuten, braucht das Heim-WLAN als Internet) installiert das Skript
   NetworkManager, `rpicam-apps-lite`, die .NET-10-Laufzeit, die App als systemd-Dienst `rpicam`, das Hotspot-Profil und die
   Kamera-Konfiguration in `config.txt` (siehe "Kamera-Hardware" unten). Danach löscht es die Zugangsdaten (`rpicam.env`,
   WLAN-Schlüssel) und startet neu.
5. Verbinde dich mit dem Hotspot und öffne **http://192.168.4.1**.

Das Protokoll der Einrichtung steht in `/boot/firmware/rpicam-setup.log` (auf der SD-Karte, am PC lesbar).
Nach einem Fehler lässt sich das Skript per SSH wiederholen: `bash /boot/Automation_Custom_Script.sh`.

## Auf dem Pi

| | |
|---|---|
| Dienst | `systemctl status rpicam`, Logs: `journalctl -u rpicam` |
| Konfiguration | `/etc/rpicam/rpicam.env` (nur root), Änderungen mit `systemctl restart rpicam` übernehmen |
| Daten | `/var/lib/rpicam` (`images/`, `thumbs/`, `settings.json`, `keys/`) |
| Kamera prüfen | `rpicam-hello --list-cameras`, bei "No cameras available": `i2cdetect -y 10` (Bus leer = Kabel/Hardware, Adresse ohne Antwort = Treiber/Overlay) |
| Netzwerk | `nmcli device`, `nmcli connection show` (Hotspot-Profil: `rpicam-hotspot`) |

## Kamera-Hardware

Getestet mit der **AZ-Delivery Raspberry Pi Kamera V1.3** (OmniVision OV5647, 5 MP, kein EEPROM). `camera_auto_detect` allein
funktioniert damit nicht zuverlässig (erkennt teils fälschlich `imx708`); das Setup-Skript trägt daher explizit
`dtoverlay=$CAMERA_OVERLAY` (Standard `ov5647`) und `dtoverlay=vc4-kms-v3d,cma-128` in `config.txt` ein und lädt zusätzlich
das ISP-Kernelmodul (`bcm2835-isp`) über `/etc/modules-load.d/`, das DietPi anders als Raspberry Pi OS nicht automatisch
lädt - ohne beides meldet `rpicam-hello` "no cameras available" bzw. "platform not supported", obwohl der Sensor am I2C-Bus
antwortet. Eine andere Kamera anschließen: `CAMERA_OVERLAY` in `rpicam.env` auf den passenden Sensor-Overlay-Namen setzen.

## Bekannte Grenzen

- Der Funkchip kann **nicht gleichzeitig** Hotspot und Client sein. Beim Umschalten bricht die Verbindung des Browsers ab, wenn er über den Hotspot verbunden ist.
- Ein WLAN-Scan im Hotspot-Betrieb kann verbundene Geräte kurz unterbrechen.
- Unternehmens-WLANs (802.1X) werden nicht unterstützt.
- Die Seite läuft über **HTTP** (kein Zertifikat im eigenen Hotspot). Passwörter werden unverschlüsselt im Hotspot-WLAN übertragen, das WLAN selbst ist aber WPA2-verschlüsselt.
