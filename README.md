# REAPER Control – Macro Deck 3 Plugin

Steuert REAPER direkt über dessen Web-Interface (HTTP). Keine Tastaturkürzel, REAPER muss nicht im Fokus sein.

**Stand 0.12.1:** Action „Run REAPER command“ mit Button-Status, feste Actions (Item ruler), Setup-Dialog mit Verbindungstest, Web-API-Client.

> Gebaut gegen `MacroDeck.Plugin.Hosting 3.0.0-beta.17`. Das SDK ist Preview und ändert sich noch. Der Code wurde gegen den SDK-Quellcode geschrieben, aber noch **nicht kompiliert** – erster Schritt ist `dotnet build`.

## Voraussetzungen

- .NET 10 SDK
- Macro Deck 3 (Version, die Out-of-Process-Plugins unterstützt)
- REAPER mit aktiviertem Web-Interface:
  Options → Preferences → Control/OSC/web → **Add** → *Web browser interface*, Port merken (Standard 8080).
  Optional dort Benutzername/Passwort setzen.

## Projekt

```
ReaperControl/
├── Program.cs                     Startet das Plugin, registriert Integration + ReaperConnection
├── ReaperIntegration.cs           Actions, Setup-Flow, liest Konfiguration in InitializeAsync
├── ConfigFlow/ReaperConfigFlow.cs Setup-Dialog: Host, Port, (erweitert) Login, Resource-Pfad
├── Actions/RunCommandAction.cs    „Run REAPER command“ mit durchsuchbarer Auswahl
├── Reaper/
│   ├── ReaperWebClient.cs         GET /_/cmd;cmd;… , TRANSPORT/TRACK/MARKER, SET/EXTSTATE
│   ├── ReaperResponseParser.cs    Tab-getrennte Antworten → Records
│   ├── ReaperActionCatalog.cs     Custom Actions + Scripts aus reaper-kb.ini
│   ├── ReaperConnection.cs        Geteilter Client (DI-Singleton)
│   └── ReaperSettings.cs
├── Values.cs                      Parameterwerte normalisieren
├── manifest.json                  Plugin-Identität (id vor Veröffentlichung ändern!)
└── macrodeck-build.json           Build-Ziele win-x64, osx-arm64, osx-x64
```

## Bauen und testen

```bash
dotnet tool install --global MacroDeck.Plugin.Cli --prerelease

dotnet build

# Ohne Macro Deck, gegen einen Wegwerf-Host:
macrodeck-plugin run --project ReaperControl.csproj --stub-host

# Gegen das laufende Macro Deck (Developer Mode an, Pairing bestätigen):
macrodeck-plugin run --project ReaperControl.csproj

# Paket für die Installation:
macrodeck-plugin build --output ./artifacts
macrodeck-plugin validate --artifact ./artifacts/com.example.reaper-control-0.12.1.macroDeckPlugin
```

Die `.macroDeckPlugin`-Datei wird per Drag & Drop in Macro Deck installiert. Nicht von Hand zippen.

## Benutzung

1. In Macro Deck die Integration einrichten: Host (`127.0.0.1`), Port. Der Dialog testet die Verbindung sofort.
2. Unter *Erweitert* den REAPER-Resource-Pfad angeben (REAPER: Options → *Show REAPER resource path*). Dann erscheinen Custom Actions und Scripts in der Auswahl mit Namen.
3. Button anlegen → Action „Run REAPER command“ → Befehl wählen oder eintippen.

Befehlsformate:

| Typ | Beispiel | Woher |
| --- | --- | --- |
| Native Action | `40044` | Action List → Rechtsklick → *Copy selected action command ID* |
| Custom Action | `_a1b2c3…` | dito, oder Auswahl aus reaper-kb.ini |
| ReaScript | `_RS7d3c…` | dito |
| SWS/Erweiterung | `_SWS_ABOUT` | dito |
| Mehrere | `40042;40044` | mit `;` getrennt, werden der Reihe nach ausgeführt |

Die IDs der vordefinierten nativen Actions in `ReaperActionCatalog.WellKnown` bitte einmal in der Action List gegenprüfen.

## Button-Status (an/aus)

„Run REAPER command“ kann am Button anzeigen, ob eine Action an oder aus ist – derselbe Zustand, den REAPER als Häkchen im Menü bzw. in der Spalte *State* der Action List zeigt.

- Im Button-Editor die Action als **Status-Quelle** übernehmen (Multi state). Zustände: *On*, *Off*, *Unavailable* (REAPER nicht erreichbar bzw. Status-Script läuft nicht); Aussehen pro Zustand frei einstellbar.
- Standardmäßig zeigt der Button den Zustand des ersten Befehls. Unter **„Show state of“** lässt sich eine andere Command-ID angeben.
- Actions ohne Zustand (Spalte *State* leer) liefern keinen Status.

Technik: `CommandStateMonitor` fragt im Hintergrund alle 250 ms per `GET/<id>` (`CMDSTATE`) bzw. `GET/EXTSTATE/…` nur die Werte ab, die gerade auf Buttons angezeigt werden – alle in einer Anfrage – und meldet Änderungen per `state.update` an Macro Deck.

## Feste Actions

Fertige Actions ohne Einstellungen, mit Status am Button. Sie stehen in `Actions/FixedCommandAction.cs`, Liste `FixedCommands.All` (derzeit leer). Neue feste Action = eine Zeile in der Liste:

```csharp
new("meine-action-id", "Anzeigename", "40044"),
```

Ohne Angabe zeigt der Button den Toggle-Zustand der Action selbst. Optional als vierter Wert eine andere Statusquelle, z. B. ein ExtState, den ein Script setzt:

```csharp
new("meine-action-id", "Anzeigename", "40044", StateRule.ExtStateEquals("Bereich", "Schlüssel", "Wert")),
```

Die erste Spalte (Action-ID) speichert Macro Deck in den Profilen: nach dem ersten Einsatz nicht mehr ändern, und jede ID nur einmal vergeben. Name und Command-ID dürfen sich jederzeit ändern. Macro Deck kennt keine Gruppen innerhalb eines Plugins, deshalb ordnet ein gemeinsamer Namens-Präfix zusammengehörige Actions.

## Transport

Action **„Transport“** mit Auswahl *Command*: Play/Stop (40044), Play/Stop + Edit Cursor (40328), Pause (1008, leuchtet nur bei Pause), Repeat (1068), Stop at loop end (41834), Play, Skip Timeline Selection (40317), Record (1013, rot nur während der Aufnahme, auch bei Aufnahme-Pause). Beschriftung = Befehlsname (Schalter *Show text* aus: nur Icon), leuchtet, solange die Action an ist (Spalte *State* der Action List; Play/Stop: solange REAPER spielt). Weitere Befehle: eine Zeile in `Commands` in `Actions/TransportAction.cs`.

### Icons

Die Transport-Action liefert auch das Button-Icon. Beim Hinzufügen bietet Macro Deck an, Status **und** Icon zu übernehmen – beide Häkchen setzen.

- Icons liegen als Dateien in `Assets/Icons` und werden über den Dateinamen ohne Endung angesprochen. **Max. 512 KB pro Datei** (größere werden ignoriert, große Bilder blockieren sonst den Button-Editor), empfohlen 256 × 256 px. **Nur PNG, JPEG, WebP oder GIF** – SVG lehnt Macro Deck für Plugin-Icons ab. Empfohlen: PNG, 256 × 256, weiß auf transparent. Mitgeliefert: `play`, `stop`, `pause`, `repeat`, `record`.
- Gedimmte Variante für „aus“: eigene Datei `<name>-dim.png` (z. B. gleiches Bild mit 35 % Deckkraft). Fehlt sie, erscheint auch bei „aus“ das volle Icon.
- Pro Befehl in `Commands`: `Icon:` = Icon für „aus“, optional `IconWhenOn:` = Icon für „an“. Ohne `IconWhenOn` erscheint bei „an“ `<name>.png`, bei „aus“ `<name>-dim.png`.
- `Assets/IconSources` enthält die SVG-Vorlagen der mitgelieferten Icons (wird nicht ins Paket übernommen).

```csharp
new("record", "Record", "1013", Icon: "record"),                       // record.png / record-dim.png
new("play-stop", "Play/Stop", "40044", Icon: "play", IconWhenOn: "stop"), // zwei Icons
```

Neues Icon: Datei nach `Assets/Icons` legen, Namen in die Zeile schreiben, Version erhöhen, neu bauen. Die mitgelieferten Icons sind schlichte eigene Formen und lassen sich durch eigene Dateien mit gleichem Namen ersetzen.

## Item

Action **„Item“** mit Auswahl *Command*: Einstellungen der ausgewählten Items, u. a. Mute, Loop Source, Lock, Play All Takes, FX Chain. Rot, wenn alle ausgewählten Items stummgeschaltet sind, sonst dunkel grau-rot (`OnColor`/`OffColor` pro Eintrag in `Commands`). Die Rückmeldung kommt vom Status-Script (siehe unten), Tabelle `ITEM_PROPERTIES`.

Neue Item-Einstellung mit Rückmeldung = zwei Zeilen:
1. `Actions/ItemAction.cs`: `new("lock", "Lock", "<Command-ID>", State: SelectedItems("ItemLock")),`
2. Lua-Script, Tabelle `ITEM_PROPERTIES`: `ItemLock = "C_LOCK",` (Parametername von `GetMediaItemInfo_Value`)

## Track

Action **„Track“** mit Auswahl *Command*: Mute (6), Solo (7), Unsolo All (40340, leuchtet gelb, solange irgendein Track solo ist), Pin Tracks (40000), Lock, Phase (40282). Die Rückmeldung kommt vom Script `ReaperScripts/ReaperControl_TrackState.lua` (Tabelle `TRACK_PROPERTIES`, dazu `TrackAnySolo`); es wird wie das Item-Script in `Scripts/__startup.lua` eingetragen.

**Lock** hat eine eigene Auswahl *Lock type* (nur sichtbar, wenn *Lock* gewählt ist): Controls (41314), Height (42336), Items (43696). Controls = `LOCK 1` im Track-Teil des Chunks, Height = `B_HEIGHTLOCK`, Items = alle Items des Tracks gesperrt (43696 sperrt die Items einzeln). Tasten, die noch mit den früheren Einzeleinträgen „Lock Track Controls/Height/Items“ angelegt wurden, funktionieren weiter.

Varianten wie bei Lock: in `Commands` beim Eintrag `Options: [...]` (je `new(Key, Name, Command-ID, State)`) und `OptionsLabel:` angeben – `CommandListAction` blendet dafür ein eigenes Auswahlfeld ein.

### Aufbau Transport / Item / Track

Beide erben von `Actions/CommandListAction.cs` (Zustands-IDs werden dort kurz gehalten: Macro Deck erlaubt bei Actions mit Icon nur ca. 26 Zeichen), das Ausführen, Status, Farben, Icons und *Show text* erledigt. Eine eigene Liste braucht nur Id, Name, Beschreibung und die Liste `Commands`. Unterschied ist nur, woher „an“ kommt:
- ohne `State:` – Toggle-Zustand der REAPER-Action,
- `State: Playing / Paused` – Transport-Status (Web-Interface, kein Script),
- `State: SelectedItems("…")` – Wert vom Lua-Script.

„Item ruler“ ist älter und eigenständig; es funktioniert genauso, nur mit fest eingebauter Statusquelle.

## Item ruler

Action **„Item ruler“** mit Auswahl *Ruler mode*: Item time (42312), Item H:M:S:F (42314), Item beats (42315), Item beats, minimal (42359), Source time (42313), Source time H:M:S:F (42358). Setzt den Modus für die ausgewählten Items; Beschriftung = Modusname, leuchtet, solange die ausgewählten Items diesen Modus haben.

### Status-Script

Das Ruler-Format ist eine Eigenschaft jedes Items (Zeile `ITEMRULER <modus> 0 0` im Item-Chunk), daher liefern die Actions 42312 … keinen Toggle-Zustand (`-1`). Den Status veröffentlicht stattdessen `ReaperScripts/ReaperControl_ItemState.lua`:

- ExtState `ReaperControl/ItemRuler` = `1`…`6` (Modus aller ausgewählten Items: 1 Item time, 2 Source time, 3 Item H:M:S:F, 4 Item beats, 5 Item beats minimal, 6 Source time H:M:S:F), `0` (nichts ausgewählt / kein Ruler), `mixed` (unterschiedliche Modi).
- Leuchtet: der Button, dessen Modus alle ausgewählten Items haben. Läuft das Script nicht, zeigen die Buttons *Unavailable*.

Einrichten:
1. Script nach `<REAPER-Resource-Pfad>/Scripts/ReaperControl/` kopieren.
2. REAPER: Actions → Show action list → *New action → Load ReaScript…* → Script wählen.
3. Automatisch beim REAPER-Start: in `<Resource-Pfad>/Scripts/__startup.lua` (anlegen, falls nicht vorhanden) eintragen:
   ```lua
   dofile(reaper.GetResourcePath() .. "/Scripts/ReaperControl/ReaperControl_ItemState.lua")
   ```
   Für jetzt sofort: das Script einmal in der Action List ausführen.

Die erste Spalte (Action-ID) speichert Macro Deck in den Profilen: nach dem ersten Einsatz nicht mehr ändern, und jede ID nur einmal vergeben. Name und Command-ID dürfen sich jederzeit ändern. Macro Deck kennt keine Gruppen innerhalb eines Plugins, deshalb ordnet ein gemeinsamer Namens-Präfix („Item ruler: …“) zusammengehörige Actions.

## Render-Preset laden

Action **„Load render preset“**: öffnet den Render-Dialog mit dem gewählten Preset geladen – rendert nicht. Die Action lässt sich auf beliebig viele Buttons legen, jeder mit eigenem Preset (Auswahlliste aus `reaper-render.ini`).

Voraussetzungen:
1. Im Plugin-Setup ist der **REAPER-Resource-Pfad** gesetzt (für Presetliste und Script-Suche).
2. **SWS-Extension** und **cfillion „Apply render preset“** (ReaPack → ReaTeam Scripts) sind installiert.
3. `ReaperScripts/ReaperControl_LoadRenderPreset.lua` nach `Scripts/ReaperControl/` kopieren und einmal in der Action List laden (*New action → Load ReaScript…*). Ausführen nicht nötig, und **nicht umbenennen** – das Plugin findet es über den Dateinamen in `reaper-kb.ini`.

Ablauf: Das Plugin setzt ExtState `ReaperControl/RenderPreset` = Presetname und startet das Script in derselben Anfrage; das Script wendet das Preset über cfillions Script an und öffnet den Render-Dialog (Action 40015). Liegt cfillions Script woanders, Pfad oben im Lua-Script anpassen.

Beschriftung: Die Action liefert den Preset-Namen als Button-Beschriftung. Macro Deck fragt **beim Hinzufügen der Action** zum Button, ob die bereitgestellten Status verwendet werden sollen – bestätigen, dann das Preset wählen; danach trägt der Button den Namen und übernimmt bei einem Preset-Wechsel den neuen. Eine von Hand geänderte Beschriftung bleibt erhalten, solange das Preset gleich bleibt.

Langer Tastendruck: In Macro Deck hat jeder Button eigene Abläufe für *Short Press* und *Long Press* – eine zweite Action dort eintragen.

## Marker

Drei Actions; beim Hinzufügen „Bereitgestellte Status verwenden“ bestätigen:

| Action | Einstellung | Verhalten |
| --- | --- | --- |
| **Go to marker** | Marker-ID / Slot, Schalter *Follow marker filter* und *Use marker color* (Standard: an) | Springt zum Marker, Beschriftung „Name“ und darunter „ID: n“. Mit *Follow marker filter* und aktivem Filter: der n-te passende Marker (zeitliche Reihenfolge) – für einen Folder mit Tasten 1, 2, 3, … |
| **Go to next / previous marker** | Richtung (next / previous / *Switch "Follow filter"*), *Follow marker filter* (Umschalt-Taste / immer / nie), *Use marker color* | Springt zum nächsten Marker rechts bzw. vorherigen links vom Cursor. Folgt die Taste dem aktiven Filter, erscheint oben rechts ein Filtersymbol (Icon `filter-badge.png`; beim Hinzufügen auch „Button-Symbol“ übernehmen). Richtung *Switch "Follow filter"*: schaltet für alle Next/Previous-Tasten mit „Follow the switch button“ um – ohne eigene Beschriftung, damit es auf *Long Press* einer Next/Previous-Taste passt. Für eine eigene leuchtende Umschalt-Taste: Variable `reaper_marker_nav_filtered` per *State mapping*. Beschriftung „Go to next“/„Go to previous“ und darunter der Markername, folgt dem Cursor. |
| **Marker filter** | Text, „enthält“ / „beginnt mit“ | Aktiviert den Filter; erneuter Druck zeigt wieder alle. Immer nur ein Filter aktiv. Groß-/Kleinschreibung egal. |

- Aktueller Marker (Play-/Edit-Cursor ab dem Marker bis zum nächsten): mit *Use marker color* in voller Markerfarbe, alle anderen abgedunkelt; ohne eigene Markerfarbe bzw. Schalter aus: aktueller Marker grün, die anderen dunkelgrau.
- Leere Slots (weniger Treffer als Tasten) zeigen keine Beschriftung und tun nichts.
- Nur Marker, keine Regionen. Der Filter gilt Plugin-weit (alle Folder) und ist nach einem Neustart des Plugins aufgehoben.
- Technik: `MarkerMonitor` fragt alle 300 ms `MARKER;TRANSPORT` ab, solange Marker-Tasten angezeigt werden; Sprung per `SET/POS_STR/m<ID>`.

## Widget „REAPER timecode“

Eigener Widget-Typ (Kachel): zeigt die Position des Play-Cursors (beim Abspielen/Pause/Aufnahme) bzw. Edit-Cursors. Im Deck-Editor wie die eingebauten Widgets hinzufügen.

Die Zeit wechselt die Farbe mit dem Transport: grün beim Abspielen, gelb bei Pause, rot bei Aufnahme, orange bei Aufnahme-Pause; im Stopp die eingestellte Textfarbe.

Darunter optional eine Infozeile: *Show playback rate* (z. B. „Rate 1.00“, braucht `ReaperControl_Timecode.lua`) und *Show record mode* („Rec: Normal / Time punch / Item punch“, aus dem Zustand der Actions 40252 / 40076 / 40253, kein Script nötig). Beide standardmäßig an.

Konfiguration: *Format*, *Show playback rate*, *Show record mode*, Rahmen, Hintergrund (auch transparent), Textfarbe. Im Aktionen-Editor des Widgets lassen sich wie bei einem Button Actions auf *Short Press*, *Long Press* usw. legen.

| Format | Script nötig? |
| --- | --- |
| Like transport window (Standard) | nein |
| Measures.Beats | nein |
| Minutes:Seconds, Seconds | nein (mit Script exakt wie REAPER formatiert) |
| Measures.Beats + Time, Samples, Hours:Minutes:Seconds:Frames, Absolute frames | ja |

Script `ReaperScripts/ReaperControl_Timecode.lua`: wie das Item-Ruler-Script nach `Scripts/ReaperControl/` kopieren, laden und in `Scripts/__startup.lua` eintragen:

```lua
dofile(reaper.GetResourcePath() .. "/Scripts/ReaperControl/ReaperControl_Timecode.lua")
```

Technik: `TimecodeMonitor` fragt nur, solange eine Timecode-Kachel offen ist, alle 100 ms `TRANSPORT`, `GET/EXTSTATE/ReaperControl/Timecode` und `…/PlayRate` sowie `GET/40252;GET/40076;GET/40253` ab; das Script schreibt die Position in allen Formaten über `format_timestr_pos`.

## Nächste Schritte

- **Marker-Manager mobil:** Liste per `GetMarkersAsync`; Ändern über `SetExtStateAsync` + kleines Lua-Script (`reaper.GetExtState` → `SetProjectMarker4`), das per Command-ID aufgerufen wird. Für die Oberfläche die Macro-Deck-Views (List, Text field) prüfen.
- Tests mit `MacroDeck.Plugin.Testing`.
