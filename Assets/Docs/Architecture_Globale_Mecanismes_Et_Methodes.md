# Architecture Globale, Mécanismes & Méthodes : Projet StreetAct

Ce document constitue la **référence technique et architecturale exhaustive** du projet de jeu tactique urbain **StreetAct**. Il détaille l'ensemble des sous-systèmes, les principes de conception retenus, ainsi que les solutions aux problématiques de performance, de synchronisation et de gameplay.

---

## 1. Vue d'Ensemble & Philosophie de Conception

**StreetAct** est un jeu de tactique militaire urbaine au tour par tour combinant :
- Une **vue 2D Commandement** (haute définition, ultra-rapide, basée sur la topologie polygonale des bâtiments).
- Une **vue 3D Action** (isométrique, immersive, avec streaming dynamique des structures).
- Un cycle tactique en 3 temps : **Planification** (tracé des ordres) -> **Création** (points de passage / actions contextuelles) -> **Exécution Simultanée** (résolution physique et cinématique en temps réel).
- Un monde généré procéduralement à partir de données cartographiques réelles (**OpenStreetMap** via l'API Overpass).

### Règles d'or de conception :
1. **Légèreté intelligente en 2D** : Aucun calcul 3D lourd (raycast volumique, physics cast continu) n'est exécuté en vue 2D. La géométrie polygonale 2D XZ suffit pour la sélection, les trajectoires et les interactions.
2. **Zéro saccade (Zero Frame-Drop)** : Les calculs lourds (Bake NavMesh, `CalculatePath`) sont strictement interdits en boucle par frame (`Update()`). Tout est mis en cache et recalculé sur événement (`dirty flag`).
3. **Robustesse sans dépendance externe** : Effets visuels (sang, marqueurs holographiques) et sons d'interface/combat sont synthétisés de manière procédurale par le code (`ProceduralAudioBuilder`, particules générées à la volée).

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
  - Rendu des bâtiments : Tracé polygonal épuré au sol, sans surcharge d'occlusion 3D.

### 2.2. Vue 3D (Mode Action)
- **Rôle** : Plonger le joueur au cœur de l'action, observer les lignes de vue, les toits, les ouvertures et la verticalité des combats.
- **Paramètres** :
  - Projection : `Perspective` (`FOV = 45°`).
  - Distance de recul par défaut : `actionDistance = 65f` (zoom réglable en continu de `25f` à `180f`).
  - Inclinaison : `actionPitch = 50°` (plage de liberté sécurisée : 25° à 75°).
  - Rotation orbitale : `Yaw` fluide sur 360° autour du point focal.
  - Plan de découpe lointain (`FarClipPlane`) : Porté à `400f` pour dégager l'horizon urbain.
  - Ambiance atmosphérique : Brouillard linéaire doux (`FogMode.Linear`, début 120m, fin 350m) remplaçant l'ancien brouillard noir étouffant.

### 2.3. Élimination des Saccades (Anti-Jitter Architecture)
- **Calcul en `LateUpdate()`** : Le positionnement et la rotation de la caméra sont appliqués dans `LateUpdate()` après la mise à jour des positions d'animation et de physique, supprimant tout décalage d'une frame.
- **Interpolation temporelle indépendante** : Utilisation de `Time.unscaledDeltaTime` combiné à des fonctions de lissage exponentiel amorti (`Mathf.Lerp` / `Mathf.SmoothDamp`) pour assurer une fluidité constante même en cas de variation de framerate ou de pause de jeu.

---

## 3. Génération Urbaine & Topologie Polygonale

Fichiers clés : [`CityGenerator.cs`](file:///e:/streetact/My%20project/Assets/CityGenerator.cs), [`BuildingStructure.cs`](file:///e:/streetact/My%20project/Assets/BuildingStructure.cs), [`MapTileLoader.cs`](file:///e:/streetact/My%20project/Assets/MapTileLoader.cs)

```
[OSM / Overpass API] ──> [MapTileLoader (Sol)] ──> [CityGenerator (Lots & Bâtiments)] ──> [Bake NavMeshSurface]
                                                               │
                                                   [BuildingStructure]
                                                   ├── Footprint Polygon (List<Vector2>)
                                                   ├── Centroïde Géométrique (Vector3)
                                                   ├── Hauteur Réelle (float)
                                                   └── Ray-Casting 2D (ContainsPoint2D)
```

### 3.1. Parsing & Construction Géométrique
- `MapTileLoader` télécharge les tuiles de carte et génère le maillage du sol (`Y = -0.1f`).
- `CityGenerator` récupère les nœuds géographiques (*nodes*) et polygones (*ways / relations*) d'Overpass API, projette les coordonnées GPS en repère cartésien métrique Unity, et construit les structures :
  - **Murs double-face** : Les parois sont générées avec des normales intérieures et extérieures pour éviter le *backface culling* lors du bake NavMesh et permettre le tir depuis l'intérieur.
  - **Ancrage sous-terrain scellé** : Les fondations s'enfoncent à `Y = -2.0m` pour bloquer la génération de chemins sous les bâtiments.
  - **Détection des ouvertures** : Identification procédurale des façades sur rue pour y implémenter portes (`DoorInteraction`) et fenêtres de couverture (`WindowInteraction`).

### 3.2. Stockage Polygonal & Centroïde dans `BuildingStructure`
Chaque bâtiment conserve en mémoire :
- `polygonFootprint` : La liste ordonnée des sommets 2D (plan XZ).
- `centroid` : Le centre de masse calculé par moyenne barycentrique exacte.
- `bounds2D` : L'AABB 2D pour un rejet préliminaire en $O(1)$.
- `ContainsPoint2D(Vector3 worldPos)` : Algorithme classique de **Ray-Casting (Even-Odd rule)** qui vérifie si un point se trouve dans l'emprise du bâtiment en quelques microsecondes sans aucun raycast PhysX.
- `FindBuildingAt(Vector3 worldPos)` : Méthode statique ultra-performante retournant le bâtiment contenant un point du monde.

---

## 4. Moteur de Rendu & Streaming Optimisé

Fichiers clés : [`TacticalStreamingManager.cs`](file:///e:/streetact/My%20project/Assets/TacticalStreamingManager.cs), [`TacticalVisibility.cs`](file:///e:/streetact/My%20project/Assets/TacticalVisibility.cs), [`SafeMaterialFactory.cs`](file:///e:/streetact/My%20project/Assets/SafeMaterialFactory.cs)

### 4.1. Streaming Haute Performance 3D
- En vue 3D, pour maintenir plus de 60 FPS sur des villes comportant des centaines de bâtiments, les `MeshRenderer` des toits et murs sont activés/désactivés dynamiquement par morceaux (*chunks*).
- **Correction apportée** : La position de test de chaque chunk est calculée sur le **centroïde réel du bâtiment** (`building.centroid`), et le rayon de visibilité est calibré à **260m**. Tous les bâtiments dans le champ de vision sont donc parfaitement visibles.

### 4.2. Vue en Coupe Transversale (`TacticalVisibility`)
- Quand une unité s'infiltre dans un bâtiment ou lorsqu'un joueur planifie un ordre à l'intérieur, le toit passe automatiquement en **semi-transparence (80% Cutaway, alpha = 0.20f)**.
- Le matériau transparent conserve le `MeshCollider` supérieur actif afin que les tireurs d'élite positionnés sur le toit ne tombent pas à travers le plancher.
- En vue 2D Commandement, la mise à jour des toits 3D est automatiquement mise en veille pour économiser les cycles GPU.

---

## 5. Gestionnaire Tactique & Logique d'Infanterie

Fichiers clés : [`TacticalPathManager.cs`](file:///e:/streetact/My%20project/Assets/TacticalPathManager.cs), [`DestructibleEnvironment.cs`](file:///e:/streetact/My%20project/Assets/Scripts/DestructibleEnvironment.cs)

### 5.1. Interaction avec les Polygones de Bâtiments
Lorsqu'un clic/toucher est détecté en phase de planification :
1. **Unité Mortier / Artillerie** : Tout clic (bâtiment, sol, toit) est interprété comme un ordre de tir balistique immédiat.
2. **Véhicules Blindés (Tanks)** : Accès interdit aux intérieurs et toits de bâtiments intacts.
3. **Infanterie & Clic Polygone Intact** : Ouverture immédiate du menu contextuel dédié offrant les 3 choix tactiques fondamentaux :
   - 🏢 **1. INFILTRATION / INTÉRIEUR (RDC)** : L'unité navigue vers la porte la plus proche, pénètre le bâtiment et la visibilité en coupe est activée.
   - 🧗 **2. MONTER SUR LE TOIT (Sniper / Guet)** : L'unité escalade la façade et prend position à la hauteur exacte du toit (`building.height`).
   - 🚪 **3. PORTE LA PLUS PROCHE** : L'unité se positionne devant l'entrée extérieure.
4. **Bâtiments Détruits / Ruines (`DestructibleEnvironment.isDestroyed`)** : Les polygones cassés et gravats sont immédiatement reconnus comme du terrain plat franchissable sans bloquer ni ouvrir de menu.

### 5.2. Tracé Léger & Cache de Trajectoires (Zero CPU Bottleneck)
- **Problème résolu** : L'ancien code recalculait `NavMesh.CalculatePath()` à chaque frame pour toutes les unités dans `DessinerTousLesChemins()`.
- **Mécanisme adopté** :
  - Chaque `UnitAI` possède une liste de points précalculés `cachedDrawPoints` et un drapeau booléen `isPathDirty`.
  - Le calcul NavMesh n'a lieu **qu'une seule fois** lors de l'ajout, du retrait ou de la modification d'un ordre (`AddTacticalNode`, `RemoveLastTacticalNode`).
  - En régime continu, le `LineRenderer` injecte instantanément les points mis en cache, garantissant un coût CPU nul.

### 5.3. Détection Mobile & PC Unifiée
- Support transparent du **Touchscreen** et de la **Souris**.
- Détection écran *Forgiving Touch* avec rayon d'accroche proportionnel au DPI pour sélectionner facilement les soldats au doigt sur écran tactile sans frustration.

---

## 6. Unités, Pathfinding & Combat

Fichiers clés : [`UnitAI.cs`](file:///e:/streetact/My%20project/Assets/UnitAI.cs), [`UnitAI_Movement.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Movement.cs), [`UnitAI_Combat.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Combat.cs), [`UnitAI_Visuals.cs`](file:///e:/streetact/My%20project/Assets/UnitAI_Visuals.cs)

```
[TacticalNode]
├── NodeAction.Continuer        ──> Déplacement standard NavMesh
├── NodeAction.EntrerBatiment   ──> Franchissement porte & passage intérieur
├── NodeAction.SortirBatiment   ──> Sortie dans la rue
├── NodeAction.GarnisonFenetre  ──> Posture de tir fenêtrée (-75% dégâts reçus)
├── NodeAction.Escalade         ──> Montée verticale sur le toit
├── NodeAction.Guetter          ──> Posture de veille défensive (+50% défense)
└── NodeAction.TirMortier       ──> Déclenchement de salve d'artillerie
```

### 6.1. Exécution Séquentielle des Mouvements
- Les déplacements sont exécutés par la coroutine `ExecuteMovementCoroutine()`.
- L'unité parcourt ses nœuds tactiques les uns après les autres. Si un nœud est sur un toit ou en escalade, le `NavMeshAgent` est momentanément suspendu au profit d'un déplacement cinématique propre avant d'être réancré sur le maillage.

### 6.2. Système d'Animation Mixamo Humanoid
- **Règle absolue** : Tous les modèles et animations FBX sont configurés en type `Humanoid` avec `Bake Into Pose` (Position XZ et Rotation) activé sur les clips de course pour éviter tout décalage entre le squelette et le cylindre de collision.

### 6.3. Système de Combat & Couvertures
- Les unités scannent les ennemis dans leur cône de vision et portée d'engagement.
- Les soldats postés aux fenêtres (`GarnisonFenetre`) ou derrière des murets bénéficient d'un multiplicateur de réduction de dégâts (jusqu'à -75%).
- Des effets de sang directionnels procéduraux (`SpawnBloodEffect`) et des douilles éjectées sont générés dynamiquement sans dépendre de packages externes.

---

## 7. Destruction Procédurale de l'Environnement

Fichier clé : [`DestructibleEnvironment.cs`](file:///e:/streetact/My%20project/Assets/Scripts/DestructibleEnvironment.cs)

- Lorsqu'un bâtiment subit des dégâts critiques (tirs d'obus de mortier, tirs de char) :
  1. Le maillage 3D est aplati dynamiquement au niveau du sol (`Y = 0.05f`) pour représenter des décombres et ruines calcinées.
  2. Les colliders bloquants sont désactivés.
  3. La zone est enregistrée dans `DestructibleEnvironment.AllRubbleBounds`.
  4. Le pathfinding et le système de détection 2D considèrent immédiatement cette zone comme du terrain praticable.

---

## 8. Synthèse Audio Procédurale

Fichier clé : [`ProceduralAudioBuilder.cs`](file:///e:/streetact/My%20project/Assets/ProceduralAudioBuilder.cs)

Pour éviter les manques d'assets ou les fichiers corrompus, le jeu embarque un générateur de sons PCM générés par équations mathématiques en mémoire :
- `CreateClickSound()` : Bip de validation UI court (onde sinusoïdale modulée).
- `CreateHoverSound()` : Tonalité douce de survol / sélection.
- `CreateErrorSound()` : Tonalité grave d'ordre impossible.
- `CreateGunshotSound()` : Décharge percussive avec bruit blanc filtré et décroissance exponentielle.
- `CreateExplosionSound()` : Onde de choc basse fréquence pour les obus et destructions.

---

## 9. Synthèse des Classes et Responsabilités

| Script / Classe | Rôle Principal |
| :--- | :--- |
| **`TacticalCamera`** | Moteur de caméra 2D/3D, zoom continu, rotation orbitale, pan sans saccade en `LateUpdate()`. |
| **`CameraStateManager`** | Machine à états de caméra (Command 2D vs Action 3D), transitions fluides et atmosphère. |
| **`CityGenerator`** | Téléchargement OpenStreetMap/Overpass, création des maillages de bâtiments et bake NavMesh. |
| **`BuildingStructure`** | Empreinte polygonale 2D, calcul du centroïde, détection `ContainsPoint2D`, portes et fenêtres. |
| **`TacticalStreamingManager`** | Activation sélective des maillages 3D par centroïde dans un rayon de 260m. |
| **`TacticalVisibility`** | Gestion de la vue en coupe semi-transparente (80%) pour les toits occupés. |
| **`TacticalPathManager`** | Gestion du cycle de tour, détection des clics polygonaux, menus contextuels, cache de tracés. |
| **`UnitAI`** (et modules) | Comportement tactique, exécution des ordres, système de combat, santé et animations. |
| **`DestructibleEnvironment`** | Aplatissement en ruines, gestion des gravats et actualisation des zones franchissables. |
| **`ProceduralAudioBuilder`** | Génération de clips audio PCM procéduraux temps réel (zéro asset externe). |
