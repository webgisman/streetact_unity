# Organisation de `Assets/Scripts/` (état au 2026-10-03)

Tous les scripts C# du jeu sont rangés par responsabilité sous `Assets/Scripts/` (outils d'Éditeur :
`Assets/Editor/` ; effets tiers WarFX : `Assets/JMO Assets/`). Aucun script de jeu hors de ces dossiers.

| Dossier | Contenu |
|---|---|
| `AI/` | Unités : `UnitAI.cs` + `UnitAI_Combat`/`_Movement`/`_Visuals` (une seule classe en `partial`), IA du mode Solo (`TacticalAIPlanner.cs`), cycle de tour (`TacticalPathManager.cs` + `_ContextMenu`/`_Execution`/`_PathDrawing`/`_Selection`/`_UI`), brouillard de guerre (`TacticalVisibility.cs`, `FogOfWarEntity.cs`), marqueurs (`WaypointMarker.cs`, `UnitTacticalMarker.cs`) |
| `AI/Input/` | Gestes et taps (`TapGestureDetector`, `PointerFrameReader`, `GestureScale`), sélection d'unité (`UnitSelectionResolver`), routage des taps (`TacticalPathManager_Input`, `_TapActionRouter`) |
| `Auth/` | Compte et données en ligne via Supabase (`SupabaseAuthClient.cs`, `SupabaseDatabaseClient.cs`) |
| `Camera/` | Caméra 2D/3D (`CameraStateManager.cs`, `TacticalCamera.cs`) |
| `Combat/` | Barricades, obus de mortier, destruction des bâtiments (`RoadBarrier.cs`, `MortarShell.cs`, `DestructibleEnvironment.cs`) |
| `Core/` | Utilitaires (`MusicManager`, `GeoProjection`, `ProceduralAudioBuilder`, `DeterministicHash`, `VectorSentinel`, `EditorPlayerPrefsScope`) |
| `Generation/` | Ville depuis OpenStreetMap (`CityGenerator`, `BuildingStructure`, `BuildingSubdivider`, `MapTileLoader`, `StreetPropsGenerator`, `TacticalStreamingManager`), quartiers (`ZoneManager`) |
| `Interaction/` | Portes et fenêtres (`DoorInteraction.cs`, `WindowInteraction.cs`) |
| `Network/` | Client en ligne (`MultiplayerMatchController`, `GameServerClient`, `NetMessage`, `SafeCoroutineRunner`), comptes de test de l'Éditeur (`EditorTestPlayers`) |
| `Rendering/` | Matériaux et icônes procéduraux (`SafeMaterialFactory.cs`, `ProceduralIconFactory.cs`) |
| `Server/` | Serveur de jeu headless (voir `_ServerDocs/multiplayer/00-architecture.md` §4) |
| `TacticalCore/` | Grille tactique et pathfinding purs, testés hors Éditeur (`TacticalGrid`, `TacticalGridBuilder`, `Pathfinding`, `GeometryMath`, `TacticalTypes`) |
| `UI/` | Écrans UI Toolkit (`UIBootstrap`, `UIScreenManager`), démarrage (`GameManagerUI`), Conquête (`ZoneMapController`, `QuartierText`, `UiSfx`), dock de déploiement (`UnitSpawnerUI`), menu pause (`InGameMenuController`), classement, radar (`TacticalRadarUI`, seul écran en IMGUI), journal de combat, panneaux de diagnostic (`EditorDebugOverlay`, `TapDiagnosticOverlay`), thème C# (`NovgovTheme`) |

## À savoir

- **`UnitAI*` et `TacticalPathManager*`** : une seule classe chacun, découpée en fichiers `partial`
  pour la lisibilité — garder les fichiers ensemble.
- **Compilation serveur** : le serveur de jeu est le même projet compilé avec `UNITY_SERVER`. Tout
  code client (UI, `MultiplayerMatchController` hors membres statiques) est sous `#if !UNITY_SERVER`.
- **Écrans** : un fichier UXML par écran dans `Assets/Resources/UI/`, déclaré dans
  `UIScreenManager.ScreenDefinitions` ; thème commun `Assets/UI/Theme.tss`.
