# REAPER Control – Macro Deck 3 Plugin

Steuert REAPER direkt über dessen Web-Interface (HTTP). Keine Tastaturkürzel, REAPER muss nicht im Fokus sein.

**Stand 0.8.0:** Action „Run REAPER command“ mit Button-Status, feste Actions (Item ruler), Setup-Dialog mit Verbindungstest, Web-API-Client.

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
macrodeck-plugin validate --artifact ./artifacts/com.example.reaper-control-0.8.0.macroDeckPlugin
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

Action **„Transport“** mit Auswahl *Command*: Play/Stop (40044), Repeat (1068), Stop at loop end (41834). Beschriftung = Befehlsname, leuchtet, solange die Action an ist (Spalte *State* der Action List). Weitere Befehle: eine Zeile in `Commands` in `Actions/TransportAction.cs`.

## Item ruler

Action **„Item ruler“** mit Auswahl *Ruler mode*: Item time (42312), Item H:M:S:F (42314), Item beats (42315), Item beats, minimal (42359), Source time (42313), Source time H:M:S:F (42358). Setzt den Modus für die ausgewählten Items; Beschriftung = Modusname, leuchtet, solange die ausgewählten Items diesen Modus haben.

### Status-Script

Das Ruler-Format ist eine Eigenschaft jedes Items (Zeile `ITEMRULER <modus> 0 0` im Item-Chunk), daher liefern die Actions 42312 … keinen Toggle-Zustand (`-1`). Den Status veröffentlicht stattdessen `ReaperScripts/ReaperControl_ItemRulerState.lua`:

- ExtState `ReaperControl/ItemRuler` = `1`…`6` (Modus aller ausgewählten Items: 1 Item time, 2 Source time, 3 Item H:M:S:F, 4 Item beats, 5 Item beats minimal, 6 Source time H:M:S:F), `0` (nichts ausgewählt / kein Ruler), `mixed` (unterschiedliche Modi).
- Leuchtet: der Button, dessen Modus alle ausgewählten Items haben. Läuft das Script nicht, zeigen die Buttons *Unavailable*.

Einrichten:
1. Script nach `<REAPER-Resource-Pfad>/Scripts/ReaperControl/` kopieren.
2. REAPER: Actions → Show action list → *New action → Load ReaScript…* → Script wählen.
3. Automatisch beim REAPER-Start: in `<Resource-Pfad>/Scripts/__startup.lua` (anlegen, falls nicht vorhanden) eintragen:
   ```lua
   dofile(reaper.GetResourcePath() .. "/Scripts/ReaperControl/ReaperControl_ItemRulerState.lua")
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

Zwei Actions; beim Hinzufügen „Bereitgestellte Status verwenden“ bestätigen:

| Action | Einstellung | Verhalten |
| --- | --- | --- |
| **Go to marker** | Marker-ID / Slot, Schalter *Follow marker filter* und *Use marker color* (Standard: an) | Springt zum Marker, Beschriftung „Name“ und darunter „ID: n“. Mit *Follow marker filter* und aktivem Filter: der n-te passende Marker (zeitliche Reihenfolge) – für einen Folder mit Tasten 1, 2, 3, … |
| **Marker filter** | Text, „enthält“ / „beginnt mit“ | Aktiviert den Filter; erneuter Druck zeigt wieder alle. Immer nur ein Filter aktiv. Groß-/Kleinschreibung egal. |

- Aktueller Marker (Play-/Edit-Cursor ab dem Marker bis zum nächsten): mit *Use marker color* in voller Markerfarbe, alle anderen abgedunkelt; ohne eigene Markerfarbe bzw. Schalter aus: aktueller Marker grün, die anderen dunkelgrau.
- Leere Slots (weniger Treffer als Tasten) zeigen keine Beschriftung und tun nichts.
- Nur Marker, keine Regionen. Der Filter gilt Plugin-weit (alle Folder) und ist nach einem Neustart des Plugins aufgehoben.
- Technik: `MarkerMonitor` fragt alle 300 ms `MARKER;TRANSPORT` ab, solange Marker-Tasten angezeigt werden; Sprung per `SET/POS_STR/m<ID>`.

## Nächste Schritte

- **Transport-/Track-Status:** Play/Record aus `TRANSPORT`, Mute/Solo pro Track aus `TRACK`.
- **Track-Aktionen:** Mute/Solo/Arm per `SET/TRACK/<n>/MUTE/-1` (Toggle).
- **Marker-Manager mobil:** Liste per `GetMarkersAsync`; Ändern über `SetExtStateAsync` + kleines Lua-Script (`reaper.GetExtState` → `SetProjectMarker4`), das per Command-ID aufgerufen wird. Für die Oberfläche die Macro-Deck-Views (List, Text field) prüfen.
- Tests mit `MacroDeck.Plugin.Testing`.
