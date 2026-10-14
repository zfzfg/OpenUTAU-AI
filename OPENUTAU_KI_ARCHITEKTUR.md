# KI-Steuerung von OpenUtau: MCP oder CLI mit KI-Fenster?

Stand: 14. Oktober 2026. Grundlage: Untersuchung des lokalen OpenUtau-Quellcodes mit CocoIndex und direkten Dateilesungen, zusätzliche Untersuchung deiner CLI-Projekte unter `C:\Users\zfzfg\Documents\HammerMegaProjekte\GitHub-fetches` sowie offizielle MCP-/ACP-Dokumentation. Dieses Dokument beschreibt eine Architekturentscheidung; die vorgeschlagenen OpenUtau-Schnittstellen sind noch nicht implementiert. Der Projektname im Code lautet **OpenUtau**.

## 1. Meine Empfehlung

**Mit deinen vorhandenen CLI-Projekten empfehle ich eine Hybridarchitektur: kleine Automatisierungsschnittstelle im laufenden OpenUtau, externer MCP-stdio-Adapter und ein KI-CLI-Host auf Basis deiner vorhandenen ACP-Erfahrung. Ein KI-Fenster kann darauf aufsetzen.**

Für dein unmittelbares Ziel – eine KI soll im geöffneten Editor Noten, Lyrics und Phoneme bearbeiten und ganze Vocal-Projekte erstellen – bleibt die gemeinsame Bearbeitungsschicht der erste Schritt. Meine ursprüngliche Empfehlung eines vollständig eingebauten MCP-Servers beruhte darauf, dass noch kein vorhandener KI-Host bekannt war. Deine Projekte enthalten bereits ACP-Provideranbindung und eine MCP-stdio-Brücke. Dadurch ist die externe Variante für dich konkreter und attraktiver als ein Neustart der KI-Infrastruktur im C#-Editor.

OpenUtau bleibt Besitzer des geöffneten Projekts. Ein kleiner externer MCP-Adapter verbindet sich mit dieser Instanz; der KI-Agent bekommt diesen Adapter über seine Session-Konfiguration. Der KI-Host steuert Gespräch und Agentenprozess über ACP. Die vorhandene CLI-Einbindung ist dafür eine Grundlage, aber noch kein unmittelbar installierbares OpenUtau-Plugin.

Die Entscheidung ist damit: **CLI/ACP für den KI-Assistenten, MCP für seine OpenUtau-Werkzeuge und eine Live-Editor-API für die tatsächlichen Änderungen.** Ein eigenes Fenster lohnt sich, wenn du im Editor chatten möchtest. Die konkreten Befunde und Wiederverwendungsgrenzen stehen in Abschnitt 13.

## 2. Drei Dinge, die man auseinanderhalten muss

| Baustein | Aufgabe | Was er allein nicht erledigt |
|---|---|---|
| Editor-Automatisierung | Projekt lesen, Noten ändern, Phoneme prüfen, rendern | Ein Sprachmodell betreiben |
| MCP-Server oder CLI | Diese Funktionen für einen anderen Prozess zugänglich machen | Gute Melodien oder korrekte Aussprache garantieren |
| KI-Client bzw. KI-Fenster | Gespräch, Modellaufrufe, Planung, Werkzeugaufrufe | Ohne Editor-Anbindung zuverlässig das offene Projekt bearbeiten |

MCP und ein Fenster sind keine Gegensätze. MCP ist eine Verbindung für Werkzeuge; ein Fenster ist eine Benutzeroberfläche. Eine CLI kann entweder ein deterministisches Steuerprogramm wie `openutauctl` sein oder ein vollständiger KI-Agent. Die zweite Bedeutung verursacht erheblich mehr Aufwand.

Ein MCP-Server enthält nicht automatisch eine KI. Ein passender KI-Client muss ihn verbinden und seine Werkzeuge tatsächlich aufrufen können. Ebenso erzeugt ein Terminalfenster allein noch keine OpenUtau-Steuerung.

## 3. Was dein Quellcode bereits mitbringt

Die Befunde gelten für diesen lokalen Stand, nicht pauschal für jede offizielle OpenUtau-Version.

| Vorhandene Grundlage | Bedeutung für dein Vorhaben | Fundstelle |
|---|---|---|
| Avalonia-Desktop-App; Zielplattform hier .NET 10 | Ein natives KI-Fenster ist technisch anschließbar | `OpenUtau/OpenUtau.csproj`, `OpenUtau/App.axaml.cs` |
| Zentraler `DocManager`, UI-Thread-Weiterleitung und Undo-Gruppen | Externe Änderungen können denselben Bearbeitungsweg wie der Editor verwenden | `OpenUtau.Core/DocManager.cs` |
| `AddNoteCommand`, `ChangeNoteLyricCommand`, `ChangePhonemeAliasCommand`, Timing- und Ausdrucksbefehle | Viele benötigte Schreiboperationen existieren bereits intern | `OpenUtau.Core/Commands/NoteCommands.cs`, `ExpCommands.cs` |
| Track-, Part-, Tempo- und Taktbefehle | Ein Song braucht mehr als einzelne Phonemänderungen | `OpenUtau.Core/Commands/TrackCommands.cs`, `PartCommands.cs`, `ProjectCommands.cs` |
| Lyrics mit phonetischen Hinweisen und gespeicherte Phonem-Overrides | Aussprache kann auf verschiedenen Ebenen bearbeitet werden | `OpenUtau.Core/Ustx/UNote.cs`, `OpenUtau.Core/Api/Phonemizer.cs` |
| Hintergrundverarbeitung durch `PhonemizerRunner` | Nach Lyrics-Änderungen sind fertige Phoneme nicht zwingend sofort verfügbar | `OpenUtau.Core/Api/PhonemizerRunner.cs` |
| Dokumentrevision, `PartId` und Snapshots | Gute Grundlage für konsistentes Lesen und Konflikterkennung; noch keine fertige externe Bearbeitungs-API | `OpenUtau.Core/Pipeline/Identities.cs`, `Snapshots.cs` |
| MIDI-Import und Audioexport | Vorhandene Melodien übernehmen und Ergebnisse hörbar ausgeben | `OpenUtau/ViewModels/MainWindowViewModel.cs`, `OpenUtau.Core/Format/MidiWriter.cs`, `OpenUtau.Core/PlaybackManager.cs` |
| Phonemizer- und BatchEdit-Plugin-Erkennung | Erweiterungspunkte vorhanden, aber kein nachgewiesener allgemeiner Lebenszyklus für dauerhaft laufende KI-Dienste und Fenster | `DocManager.SearchAllPlugins()`, `OpenUtau.Core/Editing/README.md` |
| Lokale DAW-Integration | Beispiel für externe Prozesse, Protokollversionen und Synchronisierung | `OpenUtau.Core/DawIntegration/API.md` |

In den untersuchten App-/Core-C#- und Projektdateien habe ich keine vorhandene MCP-Implementierung gefunden.

**Wichtig zur DAW-Integration:** Laut ihrem Vertrag ist der DAW-Plugin-Prozess der TCP-Server und OpenUtau der Client. Sie synchronisiert Projekt- und Audiodaten zur DAW und erhält Transportinformationen zurück. Sie stellt keine allgemeine API zum Einfügen von Noten oder Phonemen bereit. MIDI-Eingang ist dort sogar als zukünftige Erweiterung reserviert. Du kannst Architekturideen übernehmen, aber nicht einfach diese Verbindung als fertigen KI-Server verwenden.

**Wichtig zur CLI:** `Program.Main` prüft bereits laufende Prozesse und beendet einen weiteren normalen Start. Der MainWindowViewModel verarbeitet einen einzelnen Dateipfad als Startargument. Eine neue Steuer-CLI müsste gezielt mit der vorhandenen Editor-Instanz sprechen; wiederholte Starts der Desktop-EXE sind dafür kein fertiger Mechanismus.

## 4. Variante A: MCP-Server direkt in OpenUtau

### Aufbau

```text
Externer KI-Client mit MCP-Unterstützung
    ↕ MCP, beispielsweise über lokales Streamable HTTP
MCP-Adapter innerhalb von OpenUtau
    ↕ typisierte Automatisierungsfunktionen
Editor-Bearbeitungsdienst
    ↕ DocManager / Commands auf dem UI-Thread
Offenes Projekt → Phonemizer → Renderer
```

OpenUtau startet den Server über eine Einstellung oder beim aktivierten Automatisierungsmodus. Der KI-Client liest den tatsächlichen Projektzustand und sendet strukturierte Bearbeitungsaufträge. Der Server führt keine freien C#-Programme oder Shellbefehle aus, sondern bietet begrenzte Fachfunktionen an.

Für einen bereits geöffneten Editor ist HTTP praktisch: Der Client verbindet sich mit dem laufenden Server. Bei MCP über `stdio` startet dagegen üblicherweise der Client einen Server-Unterprozess. Diese Transportwahl muss zum verwendeten KI-Client passen. Die offizielle Spezifikation beschreibt beide Transportfamilien; ihr konkretes Verhalten ist versionsabhängig. [MCP-Transporte](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx), [MCP-Transportentwicklung](https://blog.modelcontextprotocol.io/posts/2025-12-19-mcp-transport-future/).

### Vorteile

- Die KI bearbeitet genau das Projekt, das du gerade siehst, einschließlich ungespeicherter Änderungen.
- Projektzustand, Auswahl, verfügbare Stimmen und Phonemfehler lassen sich gezielt auslesen.
- Änderungen können Undo, Validierung und Neuberechnung des Editors nutzen.
- Ein externer KI-Client übernimmt Gespräch und Modellanbindung; OpenUtau braucht zunächst keinen eigenen Chat.
- Strukturierte Werkzeuge sind leichter zu prüfen als simulierte Mausaktionen oder frei interpretierte Shellausgaben.
- Mit einer gemeinsamen Bearbeitungsschicht können später CLI und Fenster ergänzt werden.
- C# passt zum vorhandenen Code; ein offizielles C#-MCP-SDK ist verfügbar. [C#-SDK](https://github.com/modelcontextprotocol/csharp-sdk).

### Nachteile

- Ein Server im App-Prozess erweitert dessen Abhängigkeiten und Fehlerfläche.
- Blockierende Werkzeugaufrufe oder falsche Thread-Nutzung können den Editor beeinträchtigen.
- HTTP benötigt Lebenszyklusverwaltung, Verbindungsschutz und Client-Konfiguration.
- Änderungen im eingebauten Server werden normalerweise zusammen mit OpenUtau ausgeliefert.
- MCP-Funktionen und Transporte müssen vom konkreten Client unterstützt werden.
- Eine KI außerhalb von OpenUtau hat ohne zusätzliche Rückgabe von Audio noch keine verlässliche Hörkontrolle.

### Geeignet, wenn …

Du mit einer vorhandenen externen HTTP-fähigen KI arbeiten willst und das offene Projekt im Editor unmittelbar verändern möchtest. Mit deinen bestehenden ACP-/CLI-Projekten ist zusätzlich die externe stdio-Variante in Abschnitt 13 besonders interessant.

## 5. Variante B: CLI mit zusätzlichem KI-Fenster als Erweiterung

### Sinnvoller Aufbau

```text
KI-Fenster in OpenUtau
    ↕ Gespräch / Fortschritt / Vorschau
KI-Agent als externer CLI-Prozess
    ↕ strukturierte lokale IPC-Verbindung
Editor-Bearbeitungsdienst in OpenUtau
    ↕ DocManager / Commands auf dem UI-Thread
Offenes Projekt → Phonemizer → Renderer
```

IPC bedeutet hier eine Verbindung zwischen Prozessen, etwa Named Pipes oder eine lokale Socket-Verbindung. Der CLI-Prozess erhält Projektinformationen und ruft definierte Bearbeitungsfunktionen auf. Er sollte nicht nur eine `.ustx`-Datei auf der Platte verändern, während OpenUtau eine andere Version im Speicher hält.

Ein natives Avalonia-Fenster mit Chat, Änderungen und Fortschritt ist meist nützlicher als ein eingebettetes Terminal. Ein Terminal-Widget würde zusätzlich Terminalemulation und dessen plattformabhängige Prozessanbindung benötigen. Diese Kosten bringen keinen direkten Vorteil für die musikalische Bearbeitung.

### Vorteile

- Das Gespräch und die Ergebnisse sind direkt in OpenUtau sichtbar.
- Eine externe KI-Komponente kann unabhängig vom Editor aktualisiert oder neu gestartet werden.
- Abstürze des externen Agenten beenden normalerweise nicht den Editor; Ressourcenverbrauch und fehlerhafte Aufträge müssen trotzdem begrenzt werden.
- Eine deterministische CLI ist zusätzlich für Skripte, Batch-Verarbeitung und reproduzierbare Abläufe nützlich.
- Modellanbieter oder lokale Modelle können hinter einer gemeinsamen Agentenschnittstelle ausgetauscht werden.
- Die Oberfläche kann musikalische Vorschläge, betroffene Noten und Hörproben zusammen anzeigen.

### Nachteile

- Ein KI-Fenster, Agentenprozess und IPC-Protokoll kommen zur ohnehin nötigen Editor-Bearbeitungsschicht hinzu.
- Modellkonfiguration, Zugangsdaten, Gesprächsverlauf, Fehleranzeige und Prozessverwaltung werden Teil deines Produkts.
- Ein bestimmter KI-CLI-Anbieter kann Authentifizierung, Ausgabeformat oder Bedienverhalten ändern.
- Freie Terminaltexte sind als technische Schnittstelle fragil; strukturierte Nachrichten sind nötig.
- Eine reine Dateibearbeitungs-CLI verursacht Konflikte mit ungespeicherten Editor-Änderungen und bietet kein nahtloses Undo.
- Externe KI-Clients bekommen ohne MCP-Adapter keine standardisierte Werkzeuganbindung.
- Eine echte Headless-CLI muss Initialisierung, Plugins, Sänger und Rendering ohne Desktop-Lebenszyklus organisieren. Das ist zusätzliche Arbeit.

### Geeignet, wenn …

Du OpenUtau als eigenständiges Produkt mit integriertem KI-Assistenten anbieten möchtest oder viele Skriptabläufe benötigst. Ohne Vorarbeiten ist die komplette Kombination für einen ersten Live-Edit umfangreich. Deine vorhandenen ACP-/CLI-Bausteine ändern diese Abwägung; siehe Abschnitt 13.

## 6. Direkter Vergleich

Bewertungen sind eine Architekturabwägung für diesen Codebestand, keine gemessenen Benchmarks.

| Kriterium | A: eingebauter MCP-Server | B: KI-CLI plus Fenster |
|---|---|---|
| Externe KI steuert laufenden Editor | Direkter Zweck | Benötigt ebenfalls eine Editor-API, eventuell zusätzlich MCP |
| Direkt in OpenUtau chatten | Zusätzliches Fenster nötig | Bestandteil der Variante |
| Einstieg für dein beschriebenes Ziel | Wenig zusätzliche Oberfläche bei externem KI-Client | Mehr Komponenten, aber KI-Anbindung durch deine Vorarbeiten teilweise vorhanden |
| Live-Projekt und Undo | Gut über Commands integrierbar | Ebenso gut, wenn dieselbe Live-API verwendet wird |
| Ganze Vocal-Projekte | Batchfunktionen und Jobs nötig | Dieselben Batchfunktionen und Jobs nötig |
| Aussprachequalität | Abhängig von Phonemizer, Voicebank und Prüfung | Gleiche Abhängigkeiten |
| Modellabhängigkeit im Editor | Gering bei externem KI-Client | Höher bei eingebauter Agentenverwaltung |
| Prozessisolation | MCP-Adapter im Editor-Prozess | Agent extern isolierbar; Bearbeitungsdienst bleibt im Editor |
| Skripte ohne KI | MCP nutzbar, CLI oft bequemer | Gute Ergänzung durch `openutauctl` |
| Echte Verarbeitung ohne GUI | Separater Host erforderlich | Möglich, aber nicht automatisch durch ein CLI-Fenster gegeben |
| Wartung | Editor-API und MCP-Adapter | Editor-API, IPC, CLI/Agent und Fenster |
| Potenzial für spätere Erweiterungen | Hoch bei getrennten Schichten | Hoch bei derselben Trennung |

**Keiner der beiden Wege ist von sich aus musikalisch besser.** Die Qualität entsteht durch gute Fachfunktionen und einen Ablauf aus Erzeugen, Prüfen, Anhören und Korrigieren. Eine CLI wird auch nicht automatisch schneller: Phonemisierung und Rendering bleiben wesentliche Kosten.

## 7. Was „Phoneme eintragen“ fachlich bedeutet

Es gibt mindestens drei verschiedene Aufgaben:

1. **Lyrics setzen:** Die KI schreibt Wörter oder Silben in Noten. Der ausgewählte Phonemizer erzeugt passende Phoneme.
2. **Aussprache vorgeben:** Die KI ergänzt phonetische Hinweise. Der Code extrahiert Hinweise in eckigen Klammern; ihre Interpretation hängt vom konkreten Phonemizer ab. Ein Beispiel wie `read[r iy d]` ist keine universelle Syntax für jede Stimme.
3. **Erzeugte Phoneme korrigieren:** Die KI setzt Alias-Overrides oder verändert Timing, Preutter und Overlap über die passenden Commands.

**Bevorzuge Lyrics und vom Phonemizer unterstützte Hinweise.** Verwende konkrete Alias-Overrides für gezielte Korrekturen, nachdem die tatsächlichen Phoneme ausgelesen wurden. Ein abstraktes Lautsymbol ist nicht zwangsläufig ein vorhandener Voicebank-Alias. Kontext, Nachbarn, Subbanks und Tonhöhe können dessen Auswahl beeinflussen.

Die KI sollte deshalb Sänger, Phonemizer, erzeugte Aliase und Fehler abfragen können. Bei klassischen Voicebanks sind Alias-/Oto-Prüfungen sinnvoll; bei anderen Renderern muss die Prüfung deren Fähigkeiten berücksichtigen.

Nicht direkt abgeleitete `UPhoneme`-Objekte verändern: Eine Neuberechnung kann solche Änderungen ersetzen. Persistente Änderungen gehören in Lyrics, Hinweise, Overrides und Expressions über den Bearbeitungsweg des Editors.

## 8. Was „ganze Songs“ benötigt

Eine Verbindung allein erzeugt noch keinen fertigen Song. Für einen vollständigen Vocal-Entwurf braucht die KI:

- Tempo und Takt, Spuren und Parts;
- Tonhöhen, Notenlängen, Pausen und Übergänge;
- Lyrics mit Silbenverteilung und gehaltenen Silben;
- passende installierte Stimmen und Phonemizer;
- Ausspracheprüfung und Timingkorrekturen;
- Pitch, Vibrato und Ausdruck;
- Vorschau, Rendering, Speichern und Export.

Ein brauchbarer Ablauf: Songplan erstellen → Noten/MIDI übernehmen oder erzeugen → Lyrics zuordnen → phonemisieren → Fehler prüfen → Abschnitt rendern → anhören oder mit verfügbarer Audioanalyse prüfen → korrigieren → speichern und exportieren.

MIDI plus Lyrics ist ein guter Einstieg, weil die Melodie bereits vorliegt. Bei „aus einer fertigen Audiodatei einen Song bauen“ kommt zusätzlich Transkription und gegebenenfalls Quellentrennung hinzu; MCP löst diese Aufgaben nicht. Der vorhandene `Analysis/MidiExtractor.cs` ist ein möglicher Untersuchungspunkt, aber seine Qualität und Eignung wurden hier nicht getestet.

Ein Vocal-Projekt ist außerdem nicht automatisch ein vollständig arrangierter und gemischter Song. Die README beschreibt OpenUtau als Vocal-Editor mit begrenztem Mixing. Instrumentalproduktion und umfassendes Mixing können weiterhin eine DAW benötigen.

## 9. Die gemeinsame Bearbeitungsschicht

Vorgeschlagen ist ein `EditorAutomationService` mit typisierten Eingaben und Ergebnissen. MCP, CLI und Fenster sollen nur Adapter sein. Namen und Aufteilung sind Vorschläge, keine bereits vorhandene API.

| Funktionengruppe | Beispielwerkzeuge | Zweck |
|---|---|---|
| Zustand lesen | `get_project`, `get_selection`, `get_notes` | Kleine, gezielte Ausschnitte statt ständig des ganzen Songs |
| Fähigkeiten lesen | `list_singers`, `list_phonemizers`, `get_capabilities` | Nur unterstützte Stimmen und Funktionen verwenden |
| Änderungen vorbereiten | `validate_edit`, `preview_edit` | Fehler und betroffene Bereiche vor der Anwendung erkennen |
| Änderungen anwenden | `apply_edit_batch` | Tracks, Parts, Noten, Lyrics, Overrides und Expressions bearbeiten |
| Aussprache lesen | `get_phonemes`, `get_diagnostics` | Tatsächliche Phonemisierung und Fehler prüfen |
| Lange Aufgaben | `start_render`, `get_job`, `cancel_job` | Rendering mit Fortschritt und Abbruch |
| Ausgabe | `save_project`, `export_audio` | Projekt und hörbares Ergebnis ausgeben |

Lieber eine überschaubare Menge fachlicher Werkzeuge als hunderte winzige Befehle oder ein freies „führe beliebigen Code aus“. Ein ganzer Song braucht viele Noten; deren Übermittlung muss als Batch möglich sein. Audio sollte als Datei oder dafür geeignete Ressource bereitgestellt werden, statt große Audiodaten in jedes Werkzeugergebnis einzubetten.

### Regeln für verlässliche Live-Bearbeitung

- **UI-Thread:** Zustand dort konsistent erfassen und komplette Schreiboperationen dort ausführen. `ExecuteCmd` leitet einzelne Commands weiter, aber daraus entsteht noch keine atomare Sequenz aus Start, mehreren Befehlen und Ende einer Undo-Gruppe.
- **Ein Writer:** Aufträge serialisieren; bei einer bereits offenen Benutzer-Undo-Gruppe warten oder mit einer klaren Busy-Antwort abbrechen. `StartUndoGroup` kann eine bestehende Gruppe abschließen; blindes Aufrufen könnte eine Nutzeraktion stören.
- **IDs:** `PartId` als Grundlage verwenden, Lebensdauer prüfen und zusätzlich ein Session-/Projektmerkmal einsetzen. Externe Note-/Track-Identitäten müssen definiert werden; verschiebbare Listenindizes allein sind ungeeignet.
- **Revision:** Ein Auftrag enthält die erwartete Revision. Vor dem Commit prüfen und bei veraltetem Zustand neu lesen lassen. Die vorhandene Revision steigt auch bei Phonemisierung; zunächst konservativ ablehnen, später gegebenenfalls fachlich getrennte Revisionen vorsehen.
- **Einheiten:** Tick-Auflösung aus dem Projekt verwenden. Notenpositionen sind laut `UNote` relativ zum Part; Projektpositionen und Millisekunden ausdrücklich davon unterscheiden.
- **Undo und Fehler:** Eingaben vorab prüfen, Änderungen logisch gruppieren und vorhandene Rollback-Funktionen integrieren. Undo-Gruppen sind keine fertigen Datenbanktransaktionen; Fehlerpfade, Command-Konstruktoren und Rücknahmen müssen gesondert geprüft werden.
- **Wiederholungen:** Auftrags-IDs und Duplikaterkennung verhindern, dass ein nach Verbindungsabbruch wiederholter Auftrag Noten doppelt erzeugt. Nach einem verlorenen Ergebnis den Auftragsstatus abfragen.
- **Asynchronität:** Zwischen „Änderung übernommen“, „Phonemisierung fertig“ und „Audio fertig“ unterscheiden. Phonemindizes nur zusammen mit dem zugehörigen Ergebnisstand akzeptieren.
- **Songgröße:** Größere Entwürfe abschnittsweise vorbereiten und begrenzte Batches anwenden. Render-/Analysejobs außerhalb kurzer UI-Commits; keinen Netzwerkaufruf während einer offenen Undo-Gruppe abwarten.
- **Speichern:** Live-Änderung und Dateiüberschreibung als getrennte Funktionen anbieten. Eine Vorschau oder Bestätigung für große Ersetzungen kann eine konfigurierbare Produktoption sein.

### Verbindungsschutz

Lokale Automatisierung sollte nur die erforderlichen Fachfunktionen freigeben. Für HTTP: Loopback-Bindung, Prüfung des `Origin`-Headers und geeignete Authentifizierung, etwa ein Sitzungstoken. Diese Punkte nennt die offizielle MCP-Transportspezifikation ausdrücklich. [MCP-Transporte und Schutzmaßnahmen](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx).

Auch eine IPC-Bridge braucht Zugriffsbeschränkung und eindeutige Instanzzuordnung. Die vertrauensbasierte DAW-Verbindung ist kein ausreichendes Vorbild für eine neue KI-Schreibschnittstelle. Dateizugriffe und Exportziele sollten zum freigegebenen Arbeitsbereich passen; Lyric- oder Projektdaten sind Inhalt und dürfen keine technischen Berechtigungen verändern.

## 10. Die Hybridvariante

```text
Externe KI ── MCP/stdio ── kleiner Adapter ── lokale IPC ── OpenUtau
Steuer-CLI ──────────────────────────────── lokale IPC ── OpenUtau
Optionales natives KI-Fenster ── gemeinsamer Bearbeitungsdienst ── Projekt
```

Diese Variante ist besonders sinnvoll, wenn du MCP-Abhängigkeiten außerhalb des Desktop-Prozesses halten oder einen `stdio`-Client anbinden möchtest. Der externe Adapter übersetzt MCP in dieselbe kleine Editor-API. Der Preis sind ein zusätzlicher Prozess, IPC, Discovery und getrennte Versionierung.

**Mit deinen Vorarbeiten würde ich die externe stdio-Brücke bereits für den ersten Prototyp verwenden.** Dein ACP-Client übergibt genau solche MCP-Server beim Session-Start; dein Gateway zeigt bereits die Übersetzung von MCP-stdio in einen lokalen HTTP-Aufruf. Die gemeinsame Bearbeitungsschicht hält trotzdem einen späteren direkt eingebauten MCP-Server offen.

## 11. Umsetzung in sinnvoller Reihenfolge

| Phase | Ergebnis | Woran du erkennst, dass sie funktioniert |
|---|---|---|
| 1: Bearbeitungsdienst | Projekt-/Notenabfragen, Lyrics-Batch, Phonemdiagnostik | Eine Phrase wird im geöffneten Editor geändert, phonemisiert und mit einem Undo zurückgenommen |
| 2: MCP-/ACP-Anbindung | Externe stdio-Brücke, lokale Editor-Verbindung und Start einer vorhandenen KI-CLI über ACP | Eine KI liest den Zustand und bearbeitet dieselbe Phrase; zunächst vorhandene Oberfläche oder kleiner Testhost |
| 3: Aussprachekorrektur | Hinweise, Alias- und Timingänderungen | Änderungen bleiben nach erneuter Phonemisierung sowie Speichern/Öffnen erhalten |
| 4: Songentwurf | Tracks, Parts, Tempo, Noten-Batches, MIDI-Übernahme | Ein mehrteiliger Vocal-Entwurf lässt sich erstellen und geordnet rückgängig machen |
| 5: Rendering | Jobs, Fortschritt, Abbruch, Audioausgabe | Die KI erhält einen fertigen Ergebnisverweis oder einen konkreten Fehler |
| 6: Komfort | Optionales KI-Fenster und Steuer-CLI | Alle Zugänge nutzen dieselbe Bearbeitungsschicht |

Für einen frühen Test eine installierte Stimme und einen bekannten Phonemizer wählen. Weitere Sprachen und Renderertypen erst nach diesem funktionierenden Ablauf ergänzen. Zeitaufwand lässt sich ohne festgelegten Client, Rendererumfang und Qualitätsziel nicht seriös beziffern.

Wichtige spätere Integrationstests: konkurrierende Nutzeränderung → Konflikt statt Überschreiben; wiederholter Auftrag → keine Duplikate; unterbrochener Batch → definierter Zustand; Lyrics-Änderung → passender fertiger Phonemstand; Renderabbruch → Editor bleibt bedienbar. Die Untersuchung hier hat keinen Build, Laufzeittest oder Audiotest durchgeführt.

## 12. Entscheidung für dein Ziel

**Nimm eine Live-Bearbeitungs-API in OpenUtau plus einen kleinen externen MCP-stdio-Adapter als ersten Schritt.** Nutze Commands, Undo und Phonemizer hinter einem gemeinsamen Bearbeitungsdienst. Deine vorhandene ACP-/CLI-Anbindung ist die Basis für den KI-Host.

**Nutze deine vorhandenen CLI-Bausteine, statt einen neuen KI-Host von null zu bauen.** Beginne mit einem Provider und einer Phrase. Ein natives KI-Fenster kann anschließend Chat, musikalische Änderungsvorschau und Hörproben anzeigen; eine Terminalemulation brauchst du dafür nicht.

**Ein vollständig eingebauter MCP-HTTP-Server bleibt eine gute Alternative**, wenn du ausschließlich einen externen HTTP-fähigen KI-Client anbinden möchtest und keine zusätzliche Node-Komponente ausliefern willst. Für die Wiederverwendung deiner bestehenden ACP-/CLI-Architektur ist die externe stdio-Brücke der passendere Start.

Die entscheidende Investition ist die verlässliche Editor-API: Sie macht Phonembearbeitung und ganze Vocal-Projekte möglich und hält dir beide Oberflächen offen.

## 13. Ergänzende Untersuchung deiner vorhandenen CLI-Projekte

### Was ich konkret gefunden habe

Untersucht wurden insbesondere `grok-build-vscode` (All your Companions) und `CLI-to-API-endpoint`. Ich habe Quellcode und Dokumentation gelesen, keine realen KI-Sessions gestartet und keine neuen Live-Tests durchgeführt. Aussagen über frühere erfolgreiche Tests stammen aus deiner Projektdokumentation und sind keine erneute Abnahme.

| Vorhandener Baustein | Konkreter Befund | Nutzen für OpenUtau |
|---|---|---|
| Einheitliche Agenten-Backends | `AcpBackend` beschreibt Prozessstart, normalisierte Antworten, Modell-/Moduswechsel und Fähigkeiten | Anbieterunterschiede außerhalb des Editors halten |
| ACP-Session mit MCP | `AcpClient.newSession()` übergibt `cwd` und `mcpServers` an `session/new`; Serverliste kann dynamisch erzeugt werden | OpenUtau-Werkzeuge direkt beim Start einer KI-Session bereitstellen |
| Fähigkeitenmatrix | `provider-capabilities.ts` trennt `yes`, `no` und `probe`; die lokale Matrix markiert clientseitige MCP-Unterstützung nicht für jeden Anbieter gleich | Funktionen pro installiertem Adapter prüfen statt überall voraussetzen |
| Prozessverwaltung | Gateway startet mit getrennten Pipes, `shell: false`, verborgenem Windows-Fenster und Prozessbaum-Abbau | Kein sichtbares Terminal nötig; Agent als Hintergrundprozess möglich |
| Kleine MCP-Brücke | `bridge/stdio.ts` registriert `tools/list` und `tools/call` und leitet sie per Bearer-geschütztem lokalem HTTP weiter | Sehr nahe am benötigten externen OpenUtau-MCP-Adapter |
| Dynamischer Werkzeugvertrag | `Bridge` prüft Schemas, verwaltet Call-IDs, offene Aufrufe und Ergebniszuordnung | Muster für validierte Werkzeugaufträge und verlässliche Rückgaben |
| Getrennter UI-Host | `host.ts` beschreibt eine Host-Abstraktion; Chat-HTML verwendet diese Host-Typen | Oberfläche und Plattformfunktionen sind bereits konzeptionell getrennt |
| Dokumentierte Integrationsgrenzen | Gateway sperrt normale Inferenz bei nicht verifizierter nativer Werkzeugisolation | Belegt, dass MCP-Anbindung allein keine vollständige Begrenzung der CLI-Werkzeuge garantiert |

Ein besonders wichtiger Unterschied: **ACP verbindet deine Anwendung mit dem Agenten; MCP verbindet den Agenten mit den OpenUtau-Fachwerkzeugen.** Dein `newSession()`-Code enthält die Verbindung beider Ebenen bereits. ACP v1 beschreibt die Übergabe von MCP-Servern und stdio als Basistransport; bei v2 werden Transportfähigkeiten ausdrücklich ausgehandelt. Für einen Port muss deshalb der tatsächliche Protokollstand des Adapters gelten. [ACP v1 Session Setup](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/docs/protocol/v1/session-setup.mdx), [ACP v2 Session Setup](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/docs/protocol/v2/session-setup.mdx).

### Die passendere Zielarchitektur mit deinen Vorarbeiten

```text
OpenUtau-KI-Fenster (später, z. B. Avalonia)
    ↕ eigener kleiner Nachrichtenvertrag für Chat und Status
Externer Node-/TypeScript-KI-Host
    ↕ ACP: Session, Prompt, Antworten, Abbruch
Vorhandene KI-CLI / passender ACP-Adapter
    ↕ MCP-stdio: OpenUtau-Fachwerkzeuge
Kleiner OpenUtau-MCP-Adapter
    ↕ authentifizierte lokale Editor-API
EditorAutomationService in OpenUtau
    ↕ Commands / UI-Thread / Undo / Phonemisierung
Geöffnetes Projekt
```

Der KI-Host und der MCP-Adapter haben unterschiedliche Aufgaben. Der Host verwaltet die Unterhaltung; der vom Agenten gestartete MCP-Adapter führt Werkzeuganfragen zur laufenden OpenUtau-Instanz. Ein Session-/Instanzmerkmal stellt sicher, dass der Auftrag im richtigen Projekt landet.

Für die lokale Editor-API würde ich beim ersten Prototyp Loopback-HTTP mit Sitzungstoken wählen: Deine vorhandene `stdio.ts`-Brücke verwendet genau dieses Muster. Das ist ein internes Fachprotokoll und muss selbst kein MCP-HTTP-Endpunkt sein. Named Pipes bleiben eine Alternative; sie würden jedoch eine neue Transportanbindung zu deiner Referenz ergänzen.

### Was sich übernehmen lässt – und was neu gebaut werden muss

| Bereich | Wiederverwendung | Noch nötige Arbeit |
|---|---|---|
| Provider- und ACP-Logik | Verträge, Normalisierung, Session- und Prozessmuster | Abhängigkeiten auslesen, kleinen eigenständigen Kern bilden, installierte CLI-Versionen prüfen |
| MCP-stdio-Brücke | Lokale Weiterleitung und MCP-Handler als Referenz | Feste musikalische Werkzeuge, OpenUtau-Zuordnung, Revisionsprüfung, Jobstatus |
| Chat-/Statusmodell | Gesprächs-, Tool- und Fortschrittsereignisse | Kleinen Vertrag zur C#-Oberfläche definieren |
| Chat-Oberfläche | Bedienkonzept und vorhandene HTML-/CSS-Bausteine als Referenz | VS-Code-Host ersetzen; native Avalonia-UI oder gesondert geprüften WebView-Host bauen |
| Änderungsvorschau | Idee der Review-Karten | Musikalischer Vergleich von Noten, Lyrics, Phonemen und Timing statt Code-Diff |
| Rücknahme | Konzeptionelle Trennung von Vorschlag und Ausführung | OpenUtau-Undo verwenden; Dateicheckpoints sind kein Ersatz für den Live-Projektzustand |
| Editor-Steuerung | Kein entsprechender Baustein in den untersuchten CLI-Projekten nachgewiesen | Fach-API in C# implementieren |

**Die Host-Abstraktion macht den Port leichter, aber nicht automatisch.** Sie enthält weiterhin Texteditor-, Konfigurations-, Webview- und Dateifunktionen. OpenUtau ist ein anderer Host mit einem musikalischen Dokumentmodell. Die vollständige VS-Code-Erweiterung würde ich deshalb nicht als Laufzeitabhängigkeit hineinziehen; ein kleiner Agentenhost ist überschaubarer.

**Den OpenAI-kompatiblen n8n-Gateway brauchst du für einen direkten ACP-Assistenten nicht dazwischenzuschalten.** Seine Brücke ist eine gute Architekturreferenz. Sein Werkzeugablauf ist aber ein anderer: Er hält Calls offen, gibt sie an den API-Aufrufer zurück und wartet auf dessen Ergebnisse. Für OpenUtau sollte der MCP-Adapter direkt den Bearbeitungsdienst aufrufen und dessen Ergebnis zurückgeben.

### Grenzen der Wiederverwendung

- **Native CLI-Werkzeuge:** Die Gateway-README und `PROVIDER_ISOLATION.md` dokumentieren unvollständige beziehungsweise widerlegte Isolation für untersuchte Versionen. `engine.ts` setzt deshalb `isolation_unverified` durch. Diesen Gateway würde ich nicht unverändert als produktive OpenUtau-Modellkomponente einsetzen oder seine Sperre als Teil des Ports entfernen. Beim direkten ACP-Host muss klar bleiben, welche zusätzlichen Dateisystem-/Shellfähigkeiten der konkrete Agent besitzt; die neue Editor-API begrenzt nur ihre eigenen Aufrufe.
- **Versionen und Fähigkeiten:** Die Fähigkeitseinträge sind der lokale Projektstand, kein Nachweis, dass jede aktuell installierte CLI alle Werkzeuge korrekt ausführt. Ein realer Durchlauf mit genau dem ausgewählten Adapter ist nötig.
- **Herkunft:** OpenUtau enthält MIT-Lizenzhinweise, das Companion-Projekt FSL-1.1-MIT-Hinweise. Die Gateway-Provenienz dokumentiert eine eigenständige Neuimplementierung und ein privates Paket ohne Open-Source-Lizenz. Architekturideen übernehmen und konkrete Quellcodeübernahme sind daher getrennte Schritte; vor dem Kopieren die jeweilige Herkunft und Lizenzbedingungen klären. Ich habe hier keinen Fremdcode kopiert.
- **Musikalischer Kontext:** Code-Worktrees, Textdatei-Diffs und Coding-Briefings nicht pauschal übernehmen. Der Agent braucht musikalische Werkzeuge, Projektauflösung, Voicebank-Fähigkeiten und Hörproben als Kontext.

### Konkreter erster Durchlauf

1. OpenUtau stellt `get_project`, `get_notes`, `set_lyrics_batch` und `get_phonemes` über den gemeinsamen Bearbeitungsdienst und eine lokale Verbindung bereit.
2. Ein kleiner externer MCP-stdio-Adapter übersetzt diese vier Werkzeuge in Editor-Aufrufe.
3. Eine Session deines vorhandenen ACP-/CLI-Hosts erhält den Adapter in `mcpServers`. Zunächst nur einen tatsächlich verfügbaren Provider verwenden.
4. Der Agent bekommt eine Phrase mit bestehenden Noten, liest den Zustand, setzt Lyrics, wartet auf die Phonemisierung und prüft die Ergebnisse.
5. Prüfen: sichtbare Live-Änderung, korrekte Alias-Ausgabe, ein Undo für die Änderung und definierte Behandlung einer veralteten Revision.
6. Erst danach Song-Erzeugung, Rendering und das neue Fenster erweitern.

### Direkt nachvollziehbare lokale Quellen

- [ACP-Backend-Vertrag](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/grok-build-vscode/src/acp-backend.ts)
- [MCP-Übergabe beim Session-Start](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/grok-build-vscode/src/acp.ts:786)
- [Provider-Fähigkeiten](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/grok-build-vscode/src/provider-capabilities.ts)
- [UI-Host-Vertrag](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/grok-build-vscode/src/host.ts:514)
- [Vorhandene MCP-stdio-Brücke](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/src/bridge/stdio.ts)
- [Werkzeugregistrierung und Call-Verwaltung](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/src/bridge/registry.ts)
- [CLI-Prozessverwaltung](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/src/acp/process.ts)
- [Gateway-Status und dokumentierte Prüfungen](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/README.md)
- [Dokumentierte Werkzeugisolation](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/docs/PROVIDER_ISOLATION.md)
- [Gateway-Herkunft](C:/Users/zfzfg/Documents/HammerMegaProjekte/GitHub-fetches/CLI-to-API-endpoint/docs/PROVENANCE.md)
