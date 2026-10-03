# Architecture Globale, Mécanismes & Méthodes : Projet Novgov

Ce document constitue la **référence technique et architecturale exhaustive** du projet de jeu tactique urbain **Novgov**. Il détaille l'ensemble des sous-systèmes, les principes de conception retenus, ainsi que les solutions aux problématiques de performance, de synchronisation, d'intelligence artificielle et de gameplay.

---

## 1. Vue d'Ensemble & Philosophie de Conception

**Novgov** est un jeu de tactique militaire urbaine au tour par tour avec exécution simultanée en temps réel combinant :
- Une **vue 2D Commandement** (haute définition, ultra-rapide, basée sur la topologie polygonale des bâtiments).
- Une **vue 3D Action** (isométrique, immersive, avec streaming dynamique des structures et vue en coupe).
- Un cycle tactique en 3 temps : **Planification** (tracé des ordres) -> **Création** (points de passage / actions contextuelles) -> **Exécution Simultanée** (résolution physique, cinématique et balistique en temps réel).
- Un monde généré procéduralement à partir de données cartographiques réelles (**OpenStreetMap** via l'API Overpass) avec découpage parcellaire et projection métrique exacte.

### Règles d'or de conception :
1. **Légèreté intelligente en 2D** : Aucun calcul 3D lourd (raycast volumique, physics cast continu) n'est exécuté en vue 2D. La géométrie polygonale 2D XZ (`BuildingStructure.ContainsPoint2D`) suffit pour la sélection, les trajectoires et les interactions.
2. **Zéro saccade (Zero Frame-Drop / Anti-Jitter)** : Les calculs lourds (Bake NavMesh, `CalculatePath`) sont strictement interdits en boucle par frame (`Update()`). Tout est mis en cache et recalculé sur événement (`dirty flag`). La caméra se déplace en `LateUpdate()`.
3. **Robustesse sans dépendance externe** : Effets visuels (sang directionnel, douilles, marqueurs holographiques, barres de vie) et sons d'interface/combat sont synthétisés de manière procédurale par le code (`ProceduralAudioBuilder`, `SafeMaterialFactory`, particules générées à la volée).
4. **Architecture Modulaire Partielle** : Le comportement des unités est scindé en classes partielles spécialisées (`UnitAI`, `UnitAI_Movement`, `UnitAI_Combat`, `UnitAI_Visuals`) pour garantir une maintenabilité optimale.

---

## 2. Système de Caméra Hybride 2D / 3D

Fichiers clés : [`TacticalCamera.cs`](file:///e:/streetact/My%20project/Assets/TacticalCamera.cs), [`CameraStateManager.cs`](file:///e:/streetact/My%20project/Assets/CameraStateManager.cs)

### 2.1. Vue 2D (Mode Commandement)
- **Rôle** : Offrir une vision globale et claire du champ de bataille pour l'élaboration des tactiques.
- **Paramètres** :
  - Projection : `Orthographique`.
  - Taille par défaut : `commandOrthographicSize = 95f` (couvre un large rayon urbain de 250m).
  - Plage de Zoom : de `minOrthoSize = 40f` (zoom rapproché) à `maxOrthoSize = 180f` (vue stratégique de l'ensemble de la ville).
  - Orientation : Fixe au zénith (`Pitch = 90°`, `Yaw = 0°`).
  - Culling Layer : depuis le 2026-09-06, ne masque plus les modèles 3D (demande explicite : "l'utilisation de la vraie image au lieu d'une icône est très bien" — voir `UnitAI.cs`). `Units_UI_Markers` ne porte plus qu'un simple anneau de couleur d'équipe au sol sous chaque unité, plus le badge OTAN qu'il affichait déjà.

### 2.2. Vue 3D (Mode Action)
- **Rôle** : Plonger le joueur au cœur de l'action, observer les lignes de vue, les toits, les ouvertures et la verticalité des combats.
- **Paramètres** :
  - Projection : `Perspective` (`FOV = 45°`).
  - Distance de recul par défaut : `actionDistance = 65f` (zoom réglable en continu de `25f` à `180f`).
  - Inclinaison : `actionPitch = 50°` (plage de liberté sécurisée : 25° à 75°).
  - Rotation orbitale : `Yaw` fluide sur 360° autour du point focal.
  - Plan de découpe lointain (`FarClipPlane`) : Porté à `400f` pour dégager l'horizon urbain.
  - Ambiance atmosphérique : Brouillard linéaire doux (`FogMode.Linear`, début 120m, fin 350m).

### 2.3. Élimination des Saccades (Anti-Jitter Architecture)
- **Calcul en `LateUpdate()`** : Le positionnement et la rotation de la caméra sont appliqués dans `LateUpdate()` après la mise à jour des positions d'animation et de physique, supprimant tout décalage d'une frame.
- **Interpolation temporelle indépendante** : Utilisation de `Time.unscaledDeltaTime` combiné à des fonctions de lissage exponentiel amorti (`Mathf.Lerp` / `Mathf.SmoothDamp`) pour assurer une fluidité constante même en cas de variation de framerate ou de pause de jeu.
- **Contrôles Universels PC & Mobile** : Prise en charge native du New Input System (Pinch-to-zoom, Pan tactile 1 doigt, Twist 2 doigts, clavier ZQSD/flèches, molette et glisser-déplacer souris).

---

## 3. Génération Urbaine, Projection & Découpage

Fichiers clés : [`CityGenerator.cs`](file:///e:/streetact/My%20project/Assets/CityGenerator.cs), [`BuildingStructure.cs`](file:///e:/streetact/My%20project/Assets/BuildingStructure.cs), [`MapTileLoader.cs`](file:///e:/streetact/My%20project/Assets/MapTileLoader.cs), [`GeoProjection.cs`](file:///e:/streetact/My%20project/Assets/Scripts/Core/GeoProjection.cs), [`BuildingSubdivider.cs`](file:///e:/streetact/My%20project/Assets/Scripts/Generation/BuildingSubdivider.cs)

```
[OSM / Overpass API] ──> [GeoProjection (EPSG:3857)] ──> [BuildingSubdivider (Lots)]
                                                                │
[MapTileLoader (Sol Y=-0.1)] ──> [CityGenerator (Murs double-face -2m)] ──> [Bake NavMeshSurface]
                                                                │
                                                    [BuildingStructure]
                                                    ├── Footprint Polygon (List<Vector2>)
                                                    ├── Centroïde Géométrique (Vector3)
                                                    ├── Hauteur Réelle (float)
                                                    ├── Ray-Casting 2D (ContainsPoint2D)
                                                    └── Portes (DoorInteraction) & Fenêtres (WindowInteraction)
```

### 3.1. Projection Cartographique EPSG:3857 (`GeoProjection`)
- `GeoProjection.SetCenter(latitude, longitude)` initialise le point central (origine `(0,0,0)` Unity).
- Conversion des coordonnées GPS en mètres réels via la projection Web Mercator avec correction d'échelle locale (`Math.Cos(lat)`).
- Alignement mathématique parfait entre les tuiles d'image satellite/carte téléchargées par `MapTileLoader` et les polygones de bâtiments d'`Overpass API`.

### 3.2. Découpage Parcellaire Procédural (`BuildingSubdivider`)
- Les grands blocs polygonaux OSM continus sont récursivement subdivisés en parcelles individuelles réalistes (`MAX_HOUSE_WIDTH = 12.0m`, `MIN_HOUSE_AREA = 30.0m²`).
- Vérification de la convexité pour éviter toute dégénérescence géométrique ou auto-intersection.

### 3.3. Construction Géométrique Robuste
- **Murs double-face** : Les parois génèrent 4 triangles par quad (faces extérieure et intérieure) pour empêcher le *backface culling* lors de la rastérisation par le voxelizer du NavMesh et permettre le tir depuis l'intérieur.
- **Ancrage sous-terrain scellé** : Les fondations s'enfoncent à `Y = -2.0m` sous le sol pour bloquer tout cheminement sous les bâtiments.
- **NavMeshModifier Not Walkable** : Chaque bâtiment est marqué avec `area = 1` ("Not Walkable") pour une étanchéité absolue du pathfinding.
- **Système de Cache et Secours Hors-Ligne** : Sauvegarde automatique du JSON brut dans `Application.persistentDataPath` et chargement de données intégrées (`LoadDefaultOfflineCity`) en cas de panne réseau.

### 3.4. Topologie & Centroïdes dans `BuildingStructure`
Chaque bâtiment conserve en mémoire :
- `polygonFootprint` : La liste ordonnée des sommets 2D (plan XZ).
- `centroid` : Le centre de masse calculé par moyenne barycentrique exacte.
- `bounds2D` : L'AABB 2D pour un rejet préliminaire en $O(1)$.
- `ContainsPoint2D(Vector3 worldPos)` : Algorithme classique de **Ray-Casting (Even-Odd rule)** qui vérifie si un point se trouve dans l'emprise du bâtiment en quelques microsecondes sans aucun raycast PhysX.
- `FindBuildingAt(Vector3 worldPos)` : Méthode statique ultra-performante retournant le bâtiment contenant un point du monde.

---

## 4. Moteur de Rendu, GPU Instancing & Streaming

Fichiers clés : [`TacticalStreamingManager.cs`](file:///e:/streetact/My%20project/Assets/TacticalStreamingManager.cs), [`TacticalVisibility.cs`](file:///e:/streetact/My%20project/Assets/TacticalVisibility.cs), [`SafeMaterialFactory.cs`](file:///e:/streetact/My%20project/Assets/SafeMaterialFactory.cs), [`GameManagerUI.cs`](file:///e:/streetact/My%20project/Assets/GameManagerUI.cs)

### 4.1. GPU Instancing Automatique au Démarrage
- Dans `GameManagerUI.OptimizeSceneMaterials()`, le jeu parcourt tous les matériaux au chargement et active dynamiquement `enableInstancing = true`, réduisant drastiquement les Draw Calls sur les scènes comportant des centaines d'éléments de décor.

### 4.2. Streaming 3D Haute Performance
- En vue 3D, les `MeshRenderer` des toits et murs sont activés/désactivés dynamiquement par morceaux (*chunks*).
- La distance de test de chaque chunk est calculée sur le **centroïde réel du bâtiment** (`building.centroid`), avec un rayon de visibilité étendu à **260m**.
- **Important** : Le bake NavMesh utilise `useGeometry = NavMeshCollectGeometry.PhysicsColliders` pour que les colliders restent actifs même si le renderer d'un bâtiment distant est masqué.

### 4.3. Vue en Coupe Transversale (`TacticalVisibility`)
- Quand une unité s'infiltre dans un bâtiment ou lorsqu'un joueur planifie un ordre à l'intérieur, le toit passe automatiquement en **semi-transparence (80% Cutaway, alpha = 0.20f)**.
- Le matériau transparent conserve le `MeshCollider` supérieur actif afin que les tireurs d'élite positionnés sur le toit ne tombent pas à travers le plancher.
- En vue 2D Commandement, la mise à jour des toits 3D est automatiquement mise en veille pour économiser les cycles GPU.

---

## 5. Gestionnaire Tactique & Actions Contextuelles

Fichiers clés : [`TacticalPathManager.cs`](file:///e:/streetact/My%20project/Assets/TacticalPathManager.cs), [`RoadBarrier.cs`](file:///e:/streetact/My%20project/Assets/Scripts/Combat/RoadBarrier.cs), [`DestructibleEnvironment.cs`](file:///e:/streetact/My%20project/Assets/Scripts/DestructibleEnvironment.cs)

### 5.1. Détection & Interaction Contextuelle
Lorsqu'un clic/toucher est détecté en phase de planification :
1. **Unité Mortier / Artillerie** : Tout clic (bâtiment, sol, toit) est interprété comme un ordre de tir balistique à longue portée.
2. **Véhicules Blindés (Tanks / Canons)** : Accès interdit aux intérieurs et toits de bâtiments intacts.
3. **Infanterie & Clic Polygone Intact** : Ouverture immédiate du menu contextuel dédié offrant les 3 choix tactiques fondamentaux :
   - 🏢 **1. INFILTRATION / INTÉRIEUR (RDC)** : L'unité navigue vers la porte la plus proche, pénètre le bâtiment et la visibilité en coupe est activée.
   - 🧗 **2. MONTER SUR LE TOIT (Sniper / Guet)** : L'unité escalade la façade et prend position à la hauteur exacte du toit (`building.height`).
   - 🚪 **3. PORTE LA PLUS PROCHE** : L'unité se positionne devant l'entrée extérieure.
4. **Bâtiments Détruits / Ruines (`DestructibleEnvironment.isDestroyed`)** : Les polygones cassés et gravats sont immédiatement reconnus comme du terrain plat franchissable sans bloquer ni ouvrir de menu.
5. **Barricades Routières (`RoadBarrier`)** : L'infanterie peut se poster derrière pour bénéficier d'une couverture lourde (-60% de dégâts reçus).

### 5.2. Tracé Léger & Cache de Trajectoires (Zero CPU Bottleneck)
- Chaque `UnitAI` possède une liste de points précalculés `cachedDrawPoints` et un drapeau booléen `isPathDirty`.
- Le calcul NavMesh n'a lieu **qu'une seule fois** lors de l'ajout, du retrait ou de la modification d'un ordre (`AddTacticalNode`, `RemoveLastTacticalNode`).
- En régime continu, le `LineRenderer` injecte instantanément les points mis en cache, garantissant un coût CPU nul.

---

## 6. Architecture des Unités, Déplacements & Combat

Fichiers clés : [`UnitAI.cs`](file:///e:/streetact/My%20project/Assets/UnitAI.cs), [`UnitAI_Movement.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Movement.cs), [`UnitAI_Combat.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Combat.cs), [`UnitAI_Visuals.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Visuals.cs), [`UnitTacticalMarker.cs`](file:///e:/streetact/My%20project/Assets/UnitTacticalMarker.cs)

```
[TacticalNode]
├── NodeAction.Continuer        ──> Déplacement standard NavMesh
├── NodeAction.EntrerBatiment   ──> Franchissement porte & passage intérieur
├── NodeAction.SortirBatiment   ──> Sortie dans la rue
├── NodeAction.GarnisonFenetre  ──> Posture de tir fenêtrée (-75% dégâts reçus)
├── NodeAction.Escalade         ──> Montée verticale sur le toit
├── NodeAction.Guetter          ──> Posture de veille défensive (+50% défense)
├── NodeAction.Embuscade        ──> Posture furtive / embuscade
├── NodeAction.SeCacher         ──> Dissimulation
└── NodeAction.TirMortier       ──> Déclenchement de salve d'artillerie balistique
```

### 6.1. Types d'Unités & Caractéristiques
1. **Fantassins (Infanterie)** : Polyvalents, pénètrent dans les bâtiments, escaladent sur les toits, s'abritent aux fenêtres et derrière les barricades.
2. **Chars Leopard 2** : Blindage lourd, tourelle orientable indépendante (`turretBone`), tirs destructeurs contre les barricades et bâtiments.
3. **Véhicules Canons** : Blindés légers rapides avec cadence de tir élevée (`shootCooldown = 1.4s`).
4. **Mortiers** : Pièces d'artillerie fixes tirant des obus balistiques haute trajectoire avec dégâts de zone (AoE Blast).
5. **Barricades Routières** : Déployables sur les axes routiers avec 250 PV et Carving NavMesh dynamique.

### 6.2. Exécution Séquentielle des Mouvements
- Les déplacements sont exécutés par la coroutine `ExecuteMovementCoroutine()`.
- L'unité parcourt ses nœuds tactiques les uns après les autres. En cas d'escalade ou de passage sur toit, le `NavMeshAgent` est momentanément suspendu au profit d'un déplacement cinématique propre avant d'être réancré sur le maillage.
- Évitement dynamique d'obstacles activé (`HighQualityObstacleAvoidance` RVO) avec priorité différenciée entre joueurs et IA.

### 6.3. Système de Combat & Couvertures
- Les unités scannent les ennemis dans leur cône de vision et portée d'engagement (4 scans par seconde).
- Les tireurs d'élite sur le toit bénéficient d'un bonus de portée de détection (+20m).
- Multiplicateurs de réduction de dégâts :
  - Garnison de fenêtre : **-75% de dégâts reçus**.
  - Barricade routière : **-60% de dégâts reçus**.
  - Posture de guet : **-50% de dégâts reçus**.
- Effets visuels procéduraux : projection de sang directionnel cône (`SpawnBloodEffect`), étincelles d'impact, fumée de canon et douilles éjectées.

---

## 7. Système d'Artillerie Balistique & Mortier

Fichier clé : [`MortarShell.cs`](file:///e:/streetact/My%20project/Assets/Scripts/Combat/MortarShell.cs)

- **Trajectoire Parabolique Haute** : L'obus de mortier s'élève avec un sommet d'apogée dynamique (`apexHeight = 22m à 50m`) lui permettant de survoler tous les gratte-ciels et immeubles intermédiaires.
- **Traînée Incandescente** : Composant `TrailRenderer` générant une traînée de fumée et de flammes pendant le vol (durée 2.2s).
- **Détonation de Zone (AoE Blast)** : À l'impact au sol ou sur un toit :
  1. Onde de choc avec rayon de souffle de `6.5m`.
  2. Dégâts massifs (`maxDamage = 150f`) décroissants avec la distance.
  3. Inflige des dégâts directs aux bâtiments via `DestructibleEnvironment.TakeDamage()`.
  4. Déclenche une onde sonore procédurale basse fréquence et un tremblement de caméra (`TacticalCamera.ShakeCamera`).

---

## 8. Cerveau Tactique IA Ennemi

Fichier clé : [`TacticalAIPlanner.cs`](file:///e:/streetact/My%20project/Assets/Scripts/AI/TacticalAIPlanner.cs)

- **Brouillard de Guerre Équitable** : L'IA n'utilise pas de données de triche. Elle ne cible que les unités joueuses détectées par ligne de vue directe ou repérées par un éclaireur (`UnitAI.IsUnitSpottedByTeam`).
- **Comportement en Patrouille / Brouillard** :
  - **Les Mortiers IA ne tirent JAMAIS à l'aveugle sur la zone de départ du joueur** : ils restent en attente (`NodeAction.Guetter`) jusqu'à confirmation visuelle.
  - L'infanterie et les blindés patrouillent vers les carrefours stratégiques et barricades.
- **Planification Tactique sur Cible Confirmée** :
  - **Infanterie** : Recherche de fenêtres de garnison face à l'ennemi ou de toits surplombants pour snipers.
  - **Blindés** : Rapprochement à distance d'engagement optimale et orientation de la tourelle vers la cible.
  - **Mortiers** : Verrouillage de coordonnées et tir balistique immédiat sur la position connue de l'escouade ennemie.

---

## 9. Interface Militaire, Déploiement & Radar HUD

Fichiers clés : [`UnitSpawnerUI.cs`](file:///e:/streetact/My%20project/Assets/UnitSpawnerUI.cs), [`TacticalRadarUI.cs`](file:///e:/streetact/My%20project/Assets/Scripts/TacticalRadarUI.cs), [`GameManagerUI.cs`](file:///e:/streetact/My%20project/Assets/GameManagerUI.cs)

### 9.1. Menu de Déploiement Drag & Drop (`UnitSpawnerUI`)
- Permet de déployer manuellement les 5 types d'unités (Fantassin, Leopard 2, Véhicule Canon, Mortier, Barricade) pour l'équipe bleue ou rouge (jusqu'à 12 unités par camp).
- Anneau holographique de prévisualisation au sol (`DeploymentPreviewRing`) projeté par raycast.
- Déploiement automatique de secours (`AutoDeployBattlefield`) si aucune unité n'est présente au démarrage.

### 9.2. Radar Militaire HUD Temps Réel (`TacticalRadarUI`)
- Rendu vectoriel immédiat `OnGUI` style affichage tête haute (HUD militaire) :
  - Réticule circulaire gradué avec faisceau de balayage sonar rotatif.
  - Blips lumineux en temps réel : Alliés en bleu cyan, Ennemis détectés en rouge vif.
  - Ondes de détection circulaires (*Pings*) générées lors des coups de feu et explosions.

### 9.3. Gestionnaire Global & Écran de Démarrage (`GameManagerUI`)
- Sélecteur de ville au démarrage (`IsStartupSelectionActive`).
- Verrouillage automatique à 60 FPS (`Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;`).
- Configuration de l'ambiance visuelle du brouillard de guerre (`RenderSettings.fog`).

---

## 10. Destruction Procédurale de l'Environnement

Fichier clé : [`DestructibleEnvironment.cs`](file:///e:/streetact/My%20project/Assets/Scripts/DestructibleEnvironment.cs)

- Lorsqu'un bâtiment ou une barricade subit des dégâts critiques (tirs d'obus de mortier ou de char) :
  1. Les occupants (tireurs d'élite, garnisons) subissent des dégâts létaux de chute/écrasement.
  2. Le maillage 3D est aplati dynamiquement au niveau du sol (`Y = 0.05f`) avec teinte calcinée pour former un tapis de décombres.
  3. Tous les `Collider` bloquants sont désactivés.
  4. L'emprise est enregistrée dans `DestructibleEnvironment.AllRubbleBounds`.
  5. Le pathfinding (`NavMesh`) et les systèmes de détection 2D considèrent instantanément cette zone comme du **terrain plat franchissable sans obstacle**.

---

## 11. Synthèse Audio Procédurale

Fichier clé : [`ProceduralAudioBuilder.cs`](file:///e:/streetact/My%20project/Assets/ProceduralAudioBuilder.cs)

Pour éliminer toute dépendance à des fichiers audio externes et prévenir les erreurs d'assets manquants, le jeu embarque un synthétiseur d'ondes PCM temps réel :
- `CreateClickSound()` : Bip UI court sinusoïdal modulé.
- `CreateHoverSound()` : Tonalité douce de survol.
- `CreateErrorSound()` : Tonalité grave d'ordre impossible.
- `CreateGunshotSound()` : Décharge percussive avec bruit blanc filtré et décroissance exponentielle.
- `CreateExplosionSound()` : Onde de choc basse fréquence pour les obus de mortier et chars.

---

## 12. Outils d'Éditeur (`Assets/Editor/`)

| Outil | Rôle |
| :--- | :--- |
| `AnimatorSetupEditor.cs` | Construit automatiquement l'arbre d'états de l'Animator des unités (Hit, Death, Escalade) — s'exécute au chargement de l'Éditeur. |
| `FetchDefaultMapData.cs` (`Tools > Novgov > Télécharger la carte hors-ligne`) | Télécharge le JSON Overpass de la carte du mode Solo. |
| `AppIconGenerator.cs`, `UIToolkitSetup.cs` | Icône de l'application ; création du PanelSettings de l'UI. |
| `ServerBuildScript.cs`, `AndroidTestBuildScript.cs`, `RestoreClientBuildSettings.cs`, `AndroidManifestOrientationFix.cs` | Builds serveur Linux et APK Android. |
| `ConquestScreenPreview.cs` | Captures PNG de l'écran Conquête (voir `_ServerDocs/multiplayer/07-tests.md`). |
| `TacticalCoreSelfTest*.cs`, `*AutoTest.cs`, `EditorAutoTestHarness.cs` | Auto-tests (pathfinding, sélection, rejeu réseau, géométrie...). |

Les outils de configuration ponctuels (squelettes Humanoid, boucles d'animation, matériaux, char
Leopard, WarFX) ont été retirés le 2026-10-03 : leurs réglages sont enregistrés dans les assets.

---

## 13. Jeu en ligne

Voir `Assets/_ServerDocs/multiplayer/00-architecture.md` (référence à jour). En résumé : écran
CONQUÊTE (carte OpenStreetMap des quartiers + gestion), capture des quartiers libres, revenu en
Points d'Action, caserne, et prise des quartiers des autres joueurs par des **sièges joués au tour
par tour entre les deux joueurs** sur un serveur autoritaire (vrai moteur Unity). Aucune IA en
multijoueur : les unités d'un joueur absent tiennent leur position.

---

## 14. Où trouver chaque script

Voir `Organisation_Des_Scripts.md` (dossiers `Assets/Scripts/AI`, `Auth`, `Camera`, `Combat`, `Core`,
`Generation`, `Interaction`, `Network`, `Rendering`, `Server`, `TacticalCore`, `UI`).
