# RpiCam

> **English:** A small, self-contained camera web app for the Raspberry Pi: photo gallery, live view, motion detection with
> pictures or video clips, and its own Wi-Fi hotspot. Works with the Raspberry Pi camera, USB webcams and RTSP network
> cameras, and also runs on a regular Linux PC. Meant for watching your pets, garden or birdhouse; if you point it at
> people, local privacy laws apply (see [Datenschutz](#datenschutz-bitte-beachten)). Blazor Server (.NET 10), MudBlazor 9. The documentation below is in German;
> issues in English or German are welcome.

**RpiCam macht aus einem Raspberry Pi und einer Kamera eine kleine Kamera mit Weboberfläche,** gedacht zum Beispiel, um
nachzusehen, was Hund oder Katze tagsüber treiben, oder um Garten, Vogelhaus oder Terrarium im Blick zu behalten. Du öffnest sie im
Browser, auf dem Handy oder am PC, ohne App, ohne Cloud und ohne Konto bei irgendeinem Anbieter. Alle Bilder bleiben auf
dem Gerät.

## Was RpiCam kann

- 📷 **Galerie:** Fotos auf Knopfdruck oder automatisch alle paar Sekunden. Ansehen, löschen, einzeln oder als ZIP herunterladen.
- 🎥 **Live-Ansicht:** Das Kamerabild direkt im Browser. Es wird nicht aufgezeichnet.
- 🏃 **Bewegungserkennung:** Bewegt sich etwas, macht RpiCam Fotos oder nimmt einen kurzen Videoclip auf.
- 📶 **Eigener Hotspot:** Der Pi spannt sein eigenes WLAN auf, das klappt auch mitten im Garten ohne Router. Auf Wunsch
  verbindet er sich mit deinem WLAN zu Hause und fällt automatisch auf den Hotspot zurück, wenn das nicht klappt.
- 🔌 **Drei Kameratypen:** Raspberry-Pi-Kamera, USB-Webcam oder Netzwerkkamera (RTSP).
- 🔒 **Login:** Beim ersten Aufruf legst du dein eigenes Konto an.
- 🌍 Deutsch und Englisch, helles und dunkles Design.

## Was du brauchst

| | Mindestens | Getestet mit |
|---|---|---|
| Rechner | Raspberry Pi mit WLAN und Hardware-Videoencoder (Zero 2 W, 3, 4) | Raspberry Pi 3 A+ (512 MB) |
| Kamera | Raspberry-Pi-Kamera, USB-Webcam oder RTSP-Netzwerkkamera | AZ-Delivery Kamera V1.3 (OV5647), Microsoft LifeCam HD-3000 |
| Speicherkarte | 8 GB | 32 GB |
| Zum Einrichten | einen PC mit Linux, Windows oder macOS und Kartenleser | Linux Mint |

Der Raspberry Pi 5 ist nicht getestet. Ihm fehlt der Hardware-Videoencoder, den die Live-Ansicht mit der Pi-Kamera nutzt.
Andere Linux-Kleinrechner (z.B. NanoPi) funktionieren mit USB-Webcam oder Netzwerkkamera, siehe
[Weg B](#weg-b-raspberry-pi-os-oder-anderes-linux).

---

## Installation

Es gibt drei Wege. Such dir einen aus:

| Weg | Für wen | Aufwand |
|---|---|---|
| [**A: DietPi**](#weg-a-dietpi-auf-dem-raspberry-pi-empfohlen) | Neuer Pi nur für die Kamera, mit eigenem Hotspot | Karte vorbereiten, einstecken, 20 Minuten warten |
| [**B: Raspberry Pi OS**](#weg-b-raspberry-pi-os-oder-anderes-linux) | Pi läuft schon oder soll noch anderes tun | ein paar Befehle per SSH |
| [**C: PC**](#weg-c-auf-dem-pc-linux) | Ausprobieren, oder Webcam/Netzwerkkamera am PC | wenige Minuten |

Für Weg A und B baust du die App vorher einmal auf deinem PC.

### Vorbereitung: die App bauen

Das geht auf Linux, Windows und macOS gleich.

1. Installiere das **.NET 10 SDK**: <https://dotnet.microsoft.com/download/dotnet/10.0>
2. Hol dir den Quellcode und baue die App:
   ```bash
   git clone https://github.com/smartXbuilds/RPI_Surveillance.git
   cd RPI_Surveillance
   dotnet publish RpiCam.csproj -c Release -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false -o publish
   ```
3. Im Ordner `publish` liegt jetzt die fertige App (etwa 13 MB). Sie läuft auf jedem Pi, egal ob 32 oder 64 Bit.

### Weg A: DietPi auf dem Raspberry Pi (empfohlen)

[DietPi](https://dietpi.com) ist ein schlankes Linux für den Pi. Beim allerersten Start richtet ein Skript aus diesem
Repository alles selbst ein: Kamera, Hotspot, die App als Dienst. Du musst dich nie per SSH anmelden.

**1. DietPi auf die Karte schreiben**

Lade das DietPi-Image für den Raspberry Pi herunter (<https://dietpi.com/#download>, Variante „ARMv8“) und schreib es mit
dem [Raspberry Pi Imager](https://www.raspberrypi.com/software/) („Eigenes Image verwenden“) oder
[balenaEtcher](https://etcher.balena.io/) auf die Speicherkarte. Zieh die Karte danach kurz ab und steck sie wieder ein.
Dann erscheint ein Laufwerk namens `boot`.

**2. Auf dem `boot`-Laufwerk vier Dateien anpassen**

- **`dietpi.txt`**: Such die folgenden Zeilen und setz sie so:
  ```ini
  AUTO_SETUP_AUTOMATED=1
  AUTO_SETUP_NET_WIFI_ENABLED=1
  AUTO_SETUP_NET_WIFI_COUNTRY_CODE=DE
  AUTO_SETUP_NET_HOSTNAME=rpicam
  AUTO_SETUP_GLOBAL_PASSWORD=ein-sicheres-passwort
  ```
  Das Passwort gilt für die Anmeldung am Pi per SSH (Benutzer `root` oder `dietpi`), nicht für die Kamera-App.

- **`dietpi-wifi.txt`**: Dein WLAN zu Hause. Der Pi braucht es nur beim ersten Start, um Software herunterzuladen:
  ```ini
  aWIFI_SSID[0]='Mein-WLAN'
  aWIFI_KEY[0]='mein-wlan-passwort'
  ```

- **`cmdline.txt`**: Häng ans Ende der einzigen Zeile ein Leerzeichen und `cfg80211.ieee80211_regdom=DE` an. Es muss alles
  in **einer** Zeile bleiben.

- **`rpicam.env`** (neue Datei): Name und Passwort für den Hotspot der Kamera:
  ```ini
  HOTSPOT_SSID=RpiCam
  HOTSPOT_PSK=mindestens-8-zeichen
  ```
  Ohne Anführungszeichen und ohne Backslashes. Hast du eine andere Pi-Kamera als die OV5647, ergänzt du z.B.
  `CAMERA_OVERLAY=imx708` (Kamera-Modul 3) oder `CAMERA_OVERLAY=imx219` (Kamera-Modul 2).

**3. App und Einrichtungsskript auf die Karte kopieren**

- den Ordner `publish` aus der Vorbereitung, umbenannt in **`rpicam-app`**
- die Datei **`deploy/Automation_Custom_Script.sh`** aus dem Repository

Danach sieht das `boot`-Laufwerk ungefähr so aus:
```
boot/
├── Automation_Custom_Script.sh
├── cmdline.txt
├── dietpi.txt
├── dietpi-wifi.txt
├── rpicam.env
├── rpicam-app/
│   ├── RpiCam.dll
│   └── ...
└── ... (weitere DietPi-Dateien)
```

**4. Pi starten und warten**

Karte in den Pi, Kamera anschließen, Strom an. Der erste Start dauert **10 bis 20 Minuten**: DietPi aktualisiert sich,
danach installiert das Skript Kamera-Software, ffmpeg, .NET und die App. Zum Schluss löscht es die Zugangsdaten von der
Karte und startet neu.

**5. Mit dem Hotspot verbinden**

Verbinde dein Handy oder deinen Laptop mit dem WLAN aus `rpicam.env` (im Beispiel `RpiCam`) und öffne
**<http://192.168.4.1>**. Jetzt legst du dein Konto an, siehe [Erster Start](#erster-start).

> Klappt etwas nicht? Steck die Karte in den PC: Auf dem `boot`-Laufwerk liegt `rpicam-setup.log` mit dem Protokoll der
> Einrichtung.

### Weg B: Raspberry Pi OS oder anderes Linux

Für einen Pi mit Raspberry Pi OS oder jedes andere Debian-artige Linux. Du richtest die App per SSH als Dienst ein, das
Netzwerk des Geräts bleibt dabei unverändert. Diese Anleitung ist so auf Debian 13 (NanoPi NEO mit USB-Webcam) getestet.
Auf Raspberry Pi OS ist sie nicht getestet.

**1. System vorbereiten**

Schreib Raspberry Pi OS Lite mit dem [Raspberry Pi Imager](https://www.raspberrypi.com/software/) auf die Karte. In den
Einstellungen des Imagers vergibst du den Hostnamen `rpicam`, Benutzer und Passwort, dein WLAN und schaltest SSH ein. Pi
starten, dann vom PC aus anmelden:
```bash
ssh benutzer@rpicam.local
```

**2. Kamera-Software und ffmpeg installieren**
```bash
sudo apt update
sudo apt install -y ffmpeg rpicam-apps-lite   # rpicam-apps-lite nur auf dem Raspberry Pi
rpicam-hello --list-cameras                    # zeigt die Pi-Kamera, falls angeschlossen
```
Wird eine OV5647-Kamera (z.B. AZ-Delivery V1.3) nicht erkannt, trag in `/boot/firmware/config.txt` die Zeile
`dtoverlay=ov5647` ein und starte neu.

**3. .NET-Laufzeit installieren**
```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
sudo bash dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm dotnet-install.sh
```

**4. App kopieren.** Auf dem PC im Repository-Ordner:
```bash
scp -r publish benutzer@rpicam.local:~/rpicam-app
```
Dann wieder auf dem Pi:
```bash
sudo install -d /opt/rpicam && sudo cp -r ~/rpicam-app/. /opt/rpicam/ && rm -r ~/rpicam-app
```

**5. Konfiguration anlegen**
```bash
sudo install -d -m 700 /etc/rpicam /var/lib/rpicam
sudo tee /etc/rpicam/rpicam.env > /dev/null <<'EOF'
ASPNETCORE_URLS=http://0.0.0.0:80
ASPNETCORE_ENVIRONMENT=Production
DOTNET_gcConcurrent=0
RpiCam__DataDir=/var/lib/rpicam
# Ohne Hardware-Videoencoder (kein Raspberry Pi) für USB-Webcams:
#RpiCam__H264Encoder=libx264
# Optional eigener Hotspot (braucht NetworkManager):
#RpiCam__HotspotSsid=RpiCam
#RpiCam__HotspotPassword=mindestens-8-zeichen
EOF
sudo chmod 600 /etc/rpicam/rpicam.env
```
Läuft auf Port 80 schon etwas anderes, nimm einen anderen Port, z.B. `http://0.0.0.0:8080`.

**6. Als Dienst einrichten und starten**
```bash
sudo tee /etc/systemd/system/rpicam.service > /dev/null <<'EOF'
[Unit]
Description=RpiCam camera web app
After=network-online.target

[Service]
WorkingDirectory=/opt/rpicam
EnvironmentFile=/etc/rpicam/rpicam.env
ExecStart=/usr/local/bin/dotnet /opt/rpicam/RpiCam.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now rpicam
```
Der Dienst läuft als root, weil die App das WLAN (`nmcli`) und die Uhrzeit stellen darf.

**7. Fertig!** Öffne **<http://rpicam.local>** (bzw. mit `:8080` am Ende) und leg dein Konto an.

> **Hotspot unter Raspberry Pi OS:** Hast du den Hotspot in Schritt 5 eingeschaltet, soll das WLAN zu Hause trotzdem Vorrang
> haben. Dafür bekommt die vom Imager angelegte Verbindung eine höhere Priorität:
> `sudo nmcli connection modify preconfigured connection.autoconnect-priority 10`

### Weg C: Auf dem PC (Linux)

**Nur mal ausprobieren, ohne Kamera.** Mit dem .NET 10 SDK und ffmpeg (`sudo apt install ffmpeg`) im Repository-Ordner:
```bash
dotnet run
```
Dann <http://localhost:5018> öffnen. Die App läuft im **Simulationsmodus**: Statt der Pi-Kamera kommt ein Testbild, und
WLAN und Systemzeit werden nur simuliert. Dein PC bleibt also unverändert.

**Dauerhaft mit Webcam oder Netzwerkkamera.** Bau die App wie in der [Vorbereitung](#vorbereitung-die-app-bauen) und starte sie so:
```bash
RpiCam__Simulate=true \
RpiCam__H264Encoder=libx264 \
RpiCam__DataDir=$HOME/.local/share/rpicam \
ASPNETCORE_URLS=http://0.0.0.0:8080 \
dotnet publish/RpiCam.dll
```
- `Simulate=true` klingt seltsam, ist hier aber richtig: Er ersetzt nur die Pi-Kamera und die WLAN-Steuerung. USB-Webcam
  und Netzwerkkamera sind echt, und die App fasst das WLAN deines PCs nicht an.
- `libx264` rechnet das Webcam-Video per Software um, ein PC schafft das locker.
- Für die Webcam muss dein Benutzer in der Gruppe `video` sein: `groups` zeigt es, `sudo usermod -aG video $USER` fügt dich
  hinzu (danach neu anmelden).

Öffne <http://localhost:8080>, leg dein Konto an und wähl unter **Einstellungen → Kamera** deine Webcam oder trag die
RTSP-Adresse deiner Netzwerkkamera ein.

Windows und macOS werden nicht unterstützt: USB-Webcams spricht die App über Linux' V4L2 an.

---

## Erster Start

Beim allerersten Aufruf zeigt RpiCam **„Konto anlegen“**. Du wählst Benutzername und Passwort (mindestens 8 Zeichen) und
bist danach angemeldet. Die Seite ist ab dann gesperrt, ein zweites Konto lässt sich nicht anlegen.

Wer zuerst auf die Seite kommt, legt das Konto an. Mach das also gleich nach der Installation. Beim Hotspot ist das
unkritisch: Hinein kommt nur, wer das WLAN-Passwort kennt.

**Passwort vergessen?** Per SSH auf dem Gerät:
```bash
sudo rm /var/lib/rpicam/account.json && sudo systemctl restart rpicam
```
Danach kannst du wieder ein neues Konto anlegen. Fotos und Einstellungen bleiben erhalten.

## So bedienst du RpiCam

| Seite | Wofür |
|---|---|
| **Galerie** | Fotos und Clips ansehen, „Jetzt aufnehmen“, auswählen, löschen, als ZIP laden |
| **WLAN** | Netzwerke in der Nähe anzeigen und verbinden. Der Pi verlässt dabei den Hotspot, schlägt die Verbindung fehl, kommt der Hotspot automatisch zurück |
| **Live-Ansicht** | Kamerabild in Echtzeit, immer nur für einen Betrachter gleichzeitig |
| **Bewegungserkennung** | einschalten, Empfindlichkeit wählen, Fotos oder Clip als Reaktion, Protokoll der letzten Ereignisse |
| **Einstellungen** | Kameratyp, Auflösung und Qualität, automatische Aufnahme, Bildregler, gespeicherte WLANs, Uhrzeit, Sprache |

**Tipp zur Uhrzeit:** Ohne Internet weiß der Pi nicht, wie spät es ist. Unter **Einstellungen → Systemzeit** übernimmst du
mit einem Klick die Uhrzeit deines Handys, damit die Fotos den richtigen Zeitstempel bekommen.

## Kameras

Unter **Einstellungen → Kamera** wählst du den Kameratyp. Fotos, Live-Ansicht, Bewegungserkennung und Clips funktionieren
mit allen drei:

| | Raspberry-Pi-Kamera | USB-Webcam | Netzwerkkamera (RTSP) |
|---|---|---|---|
| Auswahl | - | Gerät aus der Liste | Adresse `rtsp://…`, optional Benutzer und Passwort |
| Video | Hardware-Encoder des Pi, kaum Last | wird nach H.264 umgerechnet (Hardware-Encoder oder `libx264`) | meist schon H.264, nur umverpackt, kaum Last |
| Bildregler (Helligkeit, Weißabgleich …) | ja | nein | nein, in der Kamera selbst einstellen |
| Auflösung | aus den Einstellungen | aus den Einstellungen, der Treiber nimmt die nächstpassende | bestimmt die Kamera |

- **Netzwerkkamera:** Stell die Kamera auf **H.264**. H.265 kann der Browser nicht abspielen. Die RTSP-Adresse steht im
  Handbuch oder in der Weboberfläche der Kamera. Das Passwort speichert RpiCam nur auf dem Gerät und zeigt es nie wieder an.
- **USB-Webcam:** Auf schwachen Geräten wie dem Pi 3 A+ ist die Live-Ansicht mit Webcam die anstrengendste Aufgabe.
- **Raspberry-Pi-Kamera:** Getestet mit der AZ-Delivery V1.3 (OV5647-Nachbau). Andere Kameras stellst du unter Weg A über
  `CAMERA_OVERLAY` in `rpicam.env` ein, siehe [Kamera-Details](#raspberry-pi-kamera-unter-dietpi).

## Update auf eine neue Version

Bau die neue Version auf dem PC (`git pull`, dann den `dotnet publish`-Befehl aus der
[Vorbereitung](#vorbereitung-die-app-bauen)) und kopier sie auf das Gerät:
```bash
scp -r publish root@rpicam.local:/tmp/rpicam-app          # bei Weg B: benutzer@ statt root@
ssh root@rpicam.local 'cp -r /tmp/rpicam-app/. /opt/rpicam/ && rm -r /tmp/rpicam-app && systemctl restart rpicam'
```
Fotos, Clips, Einstellungen und dein Konto bleiben erhalten, sie liegen getrennt in `/var/lib/rpicam`.

## Wenn etwas nicht klappt

| Problem | So findest du es heraus |
|---|---|
| Seite lädt nicht | `systemctl status rpicam` und `journalctl -u rpicam -n 50` zeigen, ob der Dienst läuft und was schiefging |
| Einrichtung unter DietPi hängt | `rpicam-setup.log` auf dem `boot`-Laufwerk lesen (Karte in den PC stecken). Nach einem Fehler per SSH wiederholen: `bash /boot/Automation_Custom_Script.sh` |
| „No cameras available“ | `rpicam-hello --list-cameras`. Bleibt die Liste leer: Flachbandkabel prüfen (Kontakte richtig herum?) und `i2cdetect -y 10` (leerer Bus = Kabel/Hardware, Adresse ohne Antwort = Treiber/Overlay) |
| Live-Ansicht bleibt schwarz | `rpicam-vid -t 3000 --codec h264 -o /tmp/t.h264`. Bei „failed to open V4L2 H264 encoder“ fehlt das Modul `bcm2835-codec` |
| USB-Webcam fehlt in der Liste | `ls /dev/video*`, Webcam ab- und wieder anstecken, dann auf „Neu suchen“ |
| Netzwerkkamera geht nicht | Adresse am PC testen: `ffplay rtsp://…`. Läuft es dort, passen meist Benutzer und Passwort nicht |
| WLAN-Probleme | `nmcli device` und `nmcli connection show` (das Hotspot-Profil heißt `rpicam-hotspot`) |

Wichtige Orte auf dem Gerät:

| | |
|---|---|
| App | `/opt/rpicam` |
| Konfiguration | `/etc/rpicam/rpicam.env`, nach Änderungen `systemctl restart rpicam` |
| Daten | `/var/lib/rpicam`: Fotos (`images/`, `thumbs/`), Clips (`motion/`), Einstellungen (`settings.json`, `motion.json`), Konto (`account.json`) |

---

## Für Entwickler

```bash
dotnet run          # http://localhost:5018, Simulationsmodus, Daten in ./data
```

Im Simulationsmodus (`RpiCam:Simulate=true`, gesetzt in `appsettings.Development.json`) gilt:
- `nmcli` wird nie aufgerufen, ein WLAN-Passwort, das mit `wrong` beginnt, lässt die simulierte Verbindung fehlschlagen.
- Die Pi-Kamera wird durch ffmpegs Testbild (`testsrc`/`testsrc2`) ersetzt, auch für Live-Ansicht und Clips.
- Die simulierte Bewegungserkennung „sieht“ alle 20 Sekunden kurz einen hellen Block.
- USB-Webcams und Netzwerkkameras sind echt.

ffmpeg muss lokal installiert sein, sonst kommt eine klare Fehlermeldung.

Aufbau in Kürze: Blazor Server mit MudBlazor, eine Seite pro Funktion unter `Components/Pages`, die Logik in `Services`.
Kamerazugriffe laufen über `ICameraService` und `IVideoPipeline`, die je nach Kameratyp ein Backend wählen (`rpicam-*` oder
ffmpeg). Alle Kamerazugriffe teilen sich eine Sperre (`CameraGate`), weil eine Kamera nur einen Nutzer gleichzeitig bedient.

### Technische Details

**Live-Ansicht:** `rpicam-vid` (Hardware-H.264) → Pipe → `ffmpeg -c copy -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof`
→ HTTP-Response (`/api/stream/live`). Es wird nicht neu kodiert. ffmpeg könnte den Stream auch selbst ausliefern (`-listen 1`), das
würde aber am Login vorbeigehen, deshalb läuft alles über den authentifizierten Endpunkt der App. Das Entfernen des
`<video>`-Elements beendet die Prozesse nicht zuverlässig (auf dem Gerät geprüft), deshalb stoppt der Stopp-Knopf die Pipeline
serverseitig (`LiveStreamSession`).

**Bewegungserkennung:** Die App nutzt nicht die `motion_detect`-Stage von `rpicam-apps`, deren Ergebnis ist in `rpicam-apps-lite`
nirgends auslesbar. Stattdessen holt sie alle ~2 Sekunden ein Bild mit 160×120 Pixeln als rohes YUV420 (`rpicam-still --encoding
yuv420`, bzw. ffmpeg `-pix_fmt yuv420p -f rawvideo`) und vergleicht die Helligkeitsebene mit der vorherigen. Ändert sich ein
ausreichend großer Anteil der Pixel, gilt das als Bewegung. Webcams bekommen vorher eine Sekunde zum Einregeln der Belichtung,
sonst sähe jedes neue Öffnen wie Bewegung aus. Nach einem Fehler, etwa einer nicht erreichbaren Kamera, wartet die Erkennung
30 Sekunden.

**Vorschaubilder:** Erzeugt ffmpeg (höchstens 480 px, nie vergrößert). Eine Bildbibliothek gibt es in der App nicht.

### Raspberry-Pi-Kamera unter DietPi

`camera_auto_detect` allein erkennt die AZ-Delivery V1.3 (OV5647 ohne EEPROM) nicht zuverlässig, teils fälschlich als
`imx708`. Das Setup-Skript trägt deshalb `dtoverlay=$CAMERA_OVERLAY` (Standard `ov5647`) und `dtoverlay=vc4-kms-v3d,cma-128`
in `config.txt` ein. Außerdem lädt es das ISP-Modul (`bcm2835-isp`) und den H.264-Encoder (`bcm2835-codec`) über
`/etc/modules-load.d/` und hebt DietPis Blacklist für diese Module (und `vc_sm_cma`) in `/etc/modprobe.d/` auf. DietPi lädt sie
anders als Raspberry Pi OS nicht von selbst. Ohne sie meldet `rpicam-hello` „no cameras available“ bzw. „platform not
supported“, obwohl der Sensor am I2C-Bus antwortet.

### Bekannte Grenzen

- Der WLAN-Chip kann nicht gleichzeitig Hotspot und Client sein. Beim Umschalten bricht die Verbindung des Browsers ab, wenn
  er über den Hotspot verbunden ist.
- Ein WLAN-Scan im Hotspot-Betrieb kann verbundene Geräte kurz unterbrechen.
- Firmen-WLANs (802.1X) werden nicht unterstützt.
- Die Seite läuft über **HTTP**, im eigenen Hotspot gibt es kein Zertifikat. Das WLAN selbst ist WPA2-verschlüsselt.
- Fotos, Live-Ansicht, Bewegungserkennung und Clips teilen sich eine Kamera. Läuft gerade etwas, meldet alles andere
  „Kamera beschäftigt“.
- Die Bewegungserkennung vergleicht Helligkeit, keine Objekte: Lichtwechsel oder Schatten können auslösen, sehr kleine
  oder langsame Bewegungen eventuell nicht. Die Empfindlichkeit stellst du unter `/motion` ein.

## Datenschutz: Bitte beachten

RpiCam ist für **Haustiere, Garten und Tiere** gedacht, nicht zum Beobachten von Menschen. Sobald Personen ins Bild
kommen können, gelten Datenschutzgesetze (in der EU die DSGVO, in Deutschland zusätzlich das Recht am eigenen Bild). Für
die Einhaltung ist verantwortlich, wer die Kamera aufstellt und betreibt. Ein paar Grundregeln:

- **Nur eigenes Gelände filmen.** Öffentliche Wege, die Straße und Nachbargrundstücke gehören nicht ins Bild. Richte die
  Kamera so aus, dass sie dort nicht hinsieht.
- **Mitbewohner, Familie, Gäste, Babysitter, Handwerker** müssen wissen, dass eine Kamera läuft, und sollten einverstanden
  sein. Heimliches Filmen ist nicht erlaubt; Ton nimmt RpiCam ohnehin nicht auf.
- **Kennzeichnen**, wenn andere Menschen den gefilmten Bereich betreten können, z.B. mit einem gut sichtbaren Hinweisschild.
- **Nicht länger speichern als nötig.** Lösche alte Bilder und Clips regelmäßig.
- **Arbeitsplätze, Mietshäuser, Vereine:** Hier gelten strengere Regeln. Informiere dich vorher, im Zweifel bei der
  Datenschutzbehörde deines Bundeslandes.

Die Bilder verlassen das Gerät nicht, RpiCam schickt nichts in eine Cloud. Schütze den Zugang trotzdem mit einem guten
Passwort, denn wer das Passwort kennt, sieht das Kamerabild.

Dieser Abschnitt ist ein Hinweis, keine Rechtsberatung.

## Fehler melden und Mitwirken

Das GitHub-Repository ist ein **Spiegel**, entwickelt wird in einem privaten Repository. Fehler, Fragen und Ideen sind als
[Issue](../../issues) sehr willkommen. Hilfreich sind Gerät und Kamera, das System (DietPi, Raspberry Pi OS …) und ein
Auszug aus `journalctl -u rpicam` bzw. `rpicam-setup.log`. Bitte vorher Passwörter und WLAN-Namen entfernen. Pull Requests
werden nicht direkt übernommen, gute Vorschläge fließen aber gern ein.

## Lizenz

[Apache License 2.0](LICENSE).

Verwendete Bibliotheken: [MudBlazor](https://github.com/MudBlazor/MudBlazor) (MIT),
[Serilog](https://github.com/serilog/serilog) (Apache-2.0), .NET/ASP.NET Core (MIT). `rpicam-apps` (BSD-2-Clause) und
`ffmpeg` (LGPL/GPL) werden nur als eigene Programme aufgerufen und nicht mitgeliefert.
