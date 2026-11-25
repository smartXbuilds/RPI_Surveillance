# RpiCam

> **English:** A self-contained camera web app for a Raspberry Pi 3 A+ running DietPi: photo gallery, live view, motion
> detection with pictures or video clips, and a Wi-Fi hotspot with switching to a normal Wi-Fi. Blazor Server (.NET 10),
> MudBlazor 9. The documentation below is in German; issues in English or German are welcome.

Kamera-Weboberfläche für einen Raspberry Pi 3 A+ (DietPi, Raspberry-Pi-Kamera). Blazor Server, .NET 10, MudBlazor 9.

- **Galerie:** Aufnahmen und Bewegungs-Videoclips ansehen, einzeln oder gesammelt löschen, einzeln oder als ZIP herunterladen, manuell oder im
  Intervall aufnehmen.
- **Live-Ansicht:** Fragmentiertes MP4 direkt aus der Kamera (H.264-Hardware-Encoder → `ffmpeg -c copy`, kein Neucodieren), im Browser abspielbar über
  `<video>`. Wird **nicht** aufgezeichnet und nicht auf die SD-Karte geschrieben, siehe "Live-Ansicht" unten.
- **Bewegungserkennung:** Läuft im Hintergrund (Bilddifferenz alle ~2s über ein kleines Graustufenbild, siehe "Bewegungserkennung" unten). Bei Bewegung
  wahlweise Bilder im Intervall oder einen Videoclip fester Länge. Beides landet in der Galerie, Clips zusätzlich im Ereignisprotokoll
  auf `/motion` (mit Download/Löschen).
- **WLAN:** Der Pi ist standardmäßig ein **Hotspot**. Die App scannt nach Netzwerken, verbindet nach Passworteingabe und wechselt dabei vom Hotspot in den Client-Modus.
  Schlägt das fehl, startet der Hotspot automatisch wieder.
- **Einstellungen:** Aufnahme (Intervall, Auflösung, Qualität), Bildregler (Helligkeit, Kontrast, Sättigung, Schärfe, Belichtungskorrektur, Weißabgleich,
  spiegeln), gespeicherte Netzwerke **entfernen** (das aktive Netzwerk entfernen = zurück zum Hotspot), Systemzeit vom Browser übernehmen (ohne Internet
  hat der Pi keine Uhr), Sprache (Deutsch/Englisch).
- Alles hinter einem Login (ein Konto, Cookie). Das Konto legt man beim **ersten Aufruf** im Browser selbst an, siehe "Erster Start" unten.

## Entwickeln

```bash
dotnet run          # http://localhost:5018, beim ersten Aufruf Konto anlegen (landet in data/account.json)
```

Im Development-Modus läuft die App im **Simulationsmodus** (`RpiCam:Simulate=true`): falsche Kamera (Farbverläufe) und ein
simuliertes Netzwerk. Es wird nie `nmcli` aufgerufen, das WLAN des Entwicklungsrechners bleibt also unberührt.
Ein Passwort, das mit `wrong` beginnt, lässt die simulierte Verbindung fehlschlagen.
Im Simulationsmodus ersetzt `ffmpeg`s eigener Testbild-Generator (`testsrc`/`testsrc2`) die Kamera, für Fotos, Live-Ansicht und
Bewegungsclips; Vorschaubilder erzeugt ebenfalls `ffmpeg`. Dafür muss `ffmpeg` lokal installiert sein (`sudo apt install ffmpeg`), sonst
kommt eine klare Fehlermeldung statt eines stillen Fehlschlags. Die simulierte Bewegungserkennung "sieht" alle 20 Sekunden kurz einen
hellen Block, damit sich das Auslösen auch ohne Kamera beobachten lässt.

## Auf den Pi bringen

1. DietPi-Image auf die SD-Karte schreiben (Raspberry-Pi-Variante, ARMv8).
2. Auf der Boot-Partition (FAT) eintragen:
   - `dietpi-wifi.txt`: Heim-WLAN (`aWIFI_SSID[0]`, `aWIFI_KEY[0]`). Nur für die Installation beim ersten Start nötig.
   - `dietpi.txt`: `AUTO_SETUP_AUTOMATED=1`, `AUTO_SETUP_NET_WIFI_ENABLED=1`, `AUTO_SETUP_NET_WIFI_COUNTRY_CODE=DE`, Passwort, Hostname (`rpicam`).
   - `rpicam.env`: `HOTSPOT_SSID`, `HOTSPOT_PSK` (8-63 Zeichen). Optional `CAMERA_OVERLAY`
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
   NetworkManager, `rpicam-apps-lite`, `ffmpeg`, die .NET-10-Laufzeit, die App als systemd-Dienst `rpicam`, das Hotspot-Profil und die
   Kamera-Konfiguration in `config.txt` (siehe "Kamera-Hardware" unten). Danach löscht es die Zugangsdaten (`rpicam.env`,
   WLAN-Schlüssel) und startet neu.
5. Verbinde dich mit dem Hotspot und öffne **http://192.168.4.1**. Beim ersten Aufruf legst du dort das Konto an.

Das Protokoll der Einrichtung steht in `/boot/firmware/rpicam-setup.log` (auf der SD-Karte, am PC lesbar).
Nach einem Fehler lässt sich das Skript per SSH wiederholen: `bash /boot/Automation_Custom_Script.sh`.

## Erster Start

Solange kein Konto existiert, leitet die App jeden Aufruf auf **/setup** um: Benutzername und Passwort (mindestens 8 Zeichen)
eingeben, danach ist man angemeldet und die Einrichtungsseite ist dauerhaft gesperrt. Das Passwort wird nur als gesalzener
PBKDF2-Hash in `/var/lib/rpicam/account.json` (nur root lesbar) gespeichert.

Wer zuerst auf die Seite kommt, legt das Konto an - deshalb das Konto direkt nach der Installation anlegen. Erreichbar ist
die Seite nur über den WPA2-geschützten Hotspot (oder das WLAN, in das der Pi schon eingebunden ist).

**Passwort vergessen:** per SSH `rm /var/lib/rpicam/account.json && systemctl restart rpicam`, danach wieder /setup aufrufen.
Ältere Installationen mit `Auth__User`/`Auth__Password` in `/etc/rpicam/rpicam.env` übernehmen diesen Login beim ersten
Start automatisch in `account.json`; danach können die beiden Zeilen gelöscht werden.

## Auf dem Pi

| | |
|---|---|
| Dienst | `systemctl status rpicam`, Logs: `journalctl -u rpicam` |
| Konfiguration | `/etc/rpicam/rpicam.env` (nur root), Änderungen mit `systemctl restart rpicam` übernehmen |
| Daten | `/var/lib/rpicam` (`account.json`, `images/`, `thumbs/`, `motion/` [Clips], `settings.json`, `motion.json`, `motion-events.json`, `keys/`) |
| Kamera prüfen | `rpicam-hello --list-cameras`, bei "No cameras available": `i2cdetect -y 10` (Bus leer = Kabel/Hardware, Adresse ohne Antwort = Treiber/Overlay) |
| Video/Encoder prüfen | `rpicam-vid -t 3000 --codec h264 -o - \| ffmpeg -f h264 -i pipe:0 -c copy -f mp4 -movflags frag_keyframe+empty_moov -y /tmp/t.mp4` - bei "failed to open V4L2 H264 encoder": `bcm2835-codec` fehlt (siehe deploy/) |
| Netzwerk | `nmcli device`, `nmcli connection show` (Hotspot-Profil: `rpicam-hotspot`) |

## Kamera-Hardware

Getestet mit der **AZ-Delivery Raspberry Pi Kamera V1.3** (OmniVision OV5647, 5 MP, kein EEPROM). `camera_auto_detect` allein
funktioniert damit nicht zuverlässig (erkennt teils fälschlich `imx708`); das Setup-Skript trägt daher explizit
`dtoverlay=$CAMERA_OVERLAY` (Standard `ov5647`) und `dtoverlay=vc4-kms-v3d,cma-128` in `config.txt` ein. Außerdem lädt es
das ISP-Modul (`bcm2835-isp`) und den Hardware-H.264-Encoder (`bcm2835-codec`) über `/etc/modules-load.d/` und hebt DietPis
Blacklist für diese Module (und `vc_sm_cma`) in `/etc/modprobe.d/` auf. DietPi lädt sie anders als Raspberry Pi OS nicht
automatisch - ohne das meldet `rpicam-hello` "no cameras available" bzw. "platform not supported", obwohl der Sensor am
I2C-Bus antwortet, und `rpicam-vid` "failed to open V4L2 H264 encoder". Eine andere Kamera anschließen: `CAMERA_OVERLAY` in `rpicam.env` auf den passenden Sensor-Overlay-Namen setzen.

## Live-Ansicht

`rpicam-vid` (Hardware-H.264-Encoder) → Pipe → `ffmpeg -c copy -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof` → Pipe → HTTP-Response
(`/api/stream/live`, chunked). Kein Neucodieren, deshalb kaum CPU-Last für `ffmpeg` selbst; die Encoder-Arbeit macht die dedizierte Hardware. Auf dem
echten Pi verifiziert (Bild im Browser, `ffprobe` auf dem Zwischenergebnis). ffmpeg kann MJPEG/HTTP auch **direkt selbst** ausliefern (`-listen 1`), das
würde aber am Login der App vorbeigehen, deshalb läuft alles über den authentifizierten Endpoint der App statt über einen eigenen ffmpeg-Port.
Nur **ein Betrachter gleichzeitig** (dieselbe Kamera-Sperre wie für Aufnahmen); ein zweiter Versuch bekommt eine "Kamera beschäftigt"-Meldung statt
endlos zu warten. Das Entfernen des `<video>`-Elements allein reicht nicht zuverlässig, um `rpicam-vid`/`ffmpeg` zu beenden (auf dem Gerät geprüft) -
der Stop-Button beendet die serverseitige Pipeline daher explizit (`LiveStreamSession`).

## Bewegungserkennung

Nutzt **nicht** die eingebaute `motion_detect`-Stage von `rpicam-apps`: deren Ergebnis wird in der installierten `rpicam-apps-lite`-Version nirgends
als Text geloggt oder im `--metadata`-Export sichtbar (nachgeprüft: nur der Stage-Name selbst steckt im Binary, kein Log-Text). Stattdessen holt die App
selbst alle ~2 Sekunden ein kleines Bild (160×120, `rpicam-still --encoding yuv420`) und vergleicht dessen Helligkeitsebene (die ersten
160×120 Bytes, kein Dekodieren nötig) mit der vorherigen; überschreitet der Anteil veränderter Pixel die
Empfindlichkeitsschwelle, gilt das als Bewegung. Läuft als eigener Hintergrunddienst (`MotionDetectionService`), Status live auf der Seite
`/motion` sichtbar. Reaktion wahlweise Intervall-Fotos (`ICameraService.CaptureAsync`, landen ganz normal in der Galerie) oder ein Videoclip
fester Länge über dieselbe `rpicam-vid`/`ffmpeg`-Pipeline wie die Live-Ansicht, aber in eine echte MP4-Datei unter `/var/lib/rpicam/motion/`.

## Bekannte Grenzen

- Der Funkchip kann **nicht gleichzeitig** Hotspot und Client sein. Beim Umschalten bricht die Verbindung des Browsers ab, wenn er über den Hotspot verbunden ist.
- Ein WLAN-Scan im Hotspot-Betrieb kann verbundene Geräte kurz unterbrechen.
- Unternehmens-WLANs (802.1X) werden nicht unterstützt.
- Die Seite läuft über **HTTP** (kein Zertifikat im eigenen Hotspot). Passwörter werden unverschlüsselt im Hotspot-WLAN übertragen, das WLAN selbst ist aber WPA2-verschlüsselt.
- Kamera, Bewegungserkennung, Live-Ansicht und Bewegungs-Videoclips teilen sich **eine** Kamera-Sperre: Nur eine Aktion läuft wirklich gleichzeitig,
  alles andere wartet oder meldet "beschäftigt". Auf dem Pi 3 A+ (512 MB RAM) macht das Sinn, sollte aber nicht überraschen.
- Bewegungserkennung per Bilddifferenz reagiert auf Helligkeitsänderungen im Bild, nicht auf "echte" Objekte - Lichtwechsel oder Schatten können
  auslösen, ein sehr kleines oder sehr langsames Objekt eventuell nicht. Empfindlichkeit in `/motion` anpassen.

## Fehler melden und Mitwirken

Das GitHub-Repository ist ein **Spiegel**, entwickelt wird in einem privaten Repository. Fehler, Fragen und Ideen bitte als
[Issue](../../issues) eintragen, gern mit Pi-Modell, Kamera-Modul, DietPi-Version und dem Auszug aus `journalctl -u rpicam`
bzw. `rpicam-setup.log` (vorher Passwörter und SSIDs entfernen). Pull Requests werden nicht direkt gemergt, gute Vorschläge
werden aber übernommen.

## Lizenz

[Apache License 2.0](LICENSE).

Verwendete Bibliotheken: [MudBlazor](https://github.com/MudBlazor/MudBlazor) (MIT),
[Serilog](https://github.com/serilog/serilog) (Apache-2.0), .NET/ASP.NET Core (MIT). Eine Bildbibliothek gibt es nicht:
`rpicam-apps` (BSD-2-Clause) und `ffmpeg` (LGPL/GPL, auch für die Vorschaubilder) werden nur als eigene Programme aufgerufen
und nicht mitgeliefert.
