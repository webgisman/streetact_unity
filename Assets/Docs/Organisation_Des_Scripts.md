# Organisation de `Assets/Scripts/`

Tous les scripts C# du projet (hors outils d'Éditeur dans `Assets/Editor/`, hors assets tiers dans
`Assets/JMO Assets/`) sont rangés par responsabilité sous `Assets/Scripts/`. Avant réorganisation,
la moitié des scripts de gameplay traînaient directement à la racine d'`Assets/` — plus aucun script
de gameplay ne devrait être ajouté hors d'un de ces dossiers.

| Dossier | Contenu |
|---|---|
| `AI/` | Sélection/déplacement/combat des unités (`UnitAI.cs` + ses 3 fichiers `partial class` `UnitAI_Combat.cs`/`UnitAI_Movement.cs`/`UnitAI_Visuals.cs`), planification IA (`TacticalAIPlanner.cs`), gestion du tour tactique (`TacticalPathManager.cs`), visibilité/brouillard de guerre (`TacticalVisibility.cs`, `FogOfWarEntity.cs`), marqueurs (`WaypointMarker.cs`, `UnitTacticalMarker.cs`) |
| `Auth/` | Client d'authentification Supabase (`SupabaseAuthClient.cs`) |
| `Camera/` | États et transitions de caméra 2D/3D (`CameraStateManager.cs`, `TacticalCamera.cs`) |
| `Combat/` | Mécaniques de combat/destruction (`RoadBarrier.cs`, `CaptureZone.cs`, `MortarShell.cs`, `DestructibleEnvironment.cs`) |
| `Core/` | Utilitaires transverses (`MusicManager.cs`, `GeoProjection.cs`, `ProceduralAudioBuilder.cs`) |
| `Generation/` | Génération procédurale de la ville depuis OSM (`CityGenerator.cs`, `BuildingStructure.cs`, `MapTileLoader.cs`, `BuildingSubdivider.cs`, `StreetPropsGenerator.cs`, `TacticalStreamingManager.cs`) |
| `Interaction/` | Interactions avec l'environnement (`DoorInteraction.cs`, `WindowInteraction.cs`) |
| `Network/` | Client réseau multijoueur (`GameServerClient.cs`, `MultiplayerMatchController.cs`, `NetMessage.cs`) |
| `Rendering/` | Fabriques de matériaux/textures procédurales (`SafeMaterialFactory.cs`, `ProceduralIconFactory.cs`) |
| `Server/` | Code du serveur headless Unity (`JwtValidator.cs`, `PlayerConnection.cs`, `GameServerBootstrap.cs`, `MatchSessionManager.cs`) |
| `UI/` | Interface (bootstrap/registre d'écrans UI Toolkit `UIBootstrap.cs`/`UIScreenManager.cs`, contrôleurs d'écran `GameManagerUI.cs`/`UnitSpawnerUI.cs`/`LeaderboardController.cs`, radar tactique legacy `TacticalRadarUI.cs`, utilitaires `CanvasGroupFader.cs`/`UIAnimator.cs`) |

## Points connus, pas des bugs

- **`UnitAI*.cs` (4 fichiers)** : une seule classe `UnitAI : MonoBehaviour`, découpée en `partial class`
  pour la lisibilité (`UnitAI.cs` = cœur/état, `_Combat`/`_Movement`/`_Visuals` = le reste). Toujours
  garder les 4 fichiers ensemble dans `AI/`.
- **`TacticalRadarUI.cs`** est le seul écran encore en IMGUI (`OnGUI()`) — tout le reste de l'UI
  runtime passe par `UIScreenManager`/UI Toolkit (`Resources/UI/*.uxml`). Ce n'est pas un oubli
  cassé, juste un écran jamais migré (dessin radar circulaire dynamique, plus simple en IMGUI) — à
  migrer un jour si l'occasion se présente, mais fonctionnel tel quel.
- **`GameManagerUI.cs`** pilote l'écran `StartupMenu` (UI Toolkit) mais garde aussi deux panneaux
  legacy uGUI (`loadingPanel`/`errorPanel`, animés par `CanvasGroupFader.cs`) pour le chargement et
  les erreurs. Cohabitation volontaire, pas du code mort.

## Nettoyage effectué (2026-08-24)

- Déplacement de 24 scripts de gameplay depuis la racine d'`Assets/` (et `Assets/Scripts/` racine)
  vers leur dossier de catégorie ci-dessus, via `git mv` (historique et `.meta`/GUID préservés).
- Suppression de deux méthodes mortes sans aucun appelant : `TacticalRadarUI.RegisterUnit()`/
  `RemoveUnit()` (stubs vides) et `UnitSpawnerUI.IsPanelOpen()`.
- Suppression de `Assets/_Recovery/0.unity`, une scène orpheline de récupération après crash de
  l'Éditeur, absente des Build Settings et jamais chargée par aucun code.
