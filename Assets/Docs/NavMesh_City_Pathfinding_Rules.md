# Règles d'Architecture : NavMesh Procédural & Pathfinding Tactique

Ce document sert de référence technique absolue pour éviter toute régression sur la génération procédurale de ville (OSM / Overpass), le bake dynamique du NavMesh (`NavMeshSurface`), et le système de déplacement tactique par tour (`TacticalPathManager`, `UnitAI`).

---

## 1. Les Pièges Rencontrés (Historique des Problèmes)

### Piège 1 : Le faux départ du Bake NavMesh (Problème d'asynchronisme Sol vs Bâtiments)
* **Symptôme :** Les unités et le tracé ignoraient totalement les bâtiments et traversaient toute la carte en ligne droite.
* **Cause :** Au lancement du jeu (`Play`), `CityGenerator` démarrait la coroutine et vérifiait si un GameObject `"Sol"` avait un `MeshFilter`. Comme la scène Unity contenait déjà un sol par défaut (sauvegardé en dur dans la scène), `CityGenerator` pensait que le sol était prêt à la frame 1 et lançait `surface.BuildNavMesh()`. 
* Pendant ce temps, `MapTileLoader` téléchargeait les tuiles OpenStreetMap en arrière-plan pendant 3 secondes. Quand `MapTileLoader` terminait et créait le véritable mesh du sol, le NavMesh **n'était jamais rebaké**. Le jeu tournait donc avec un NavMesh obsolète / désynchronisé !
* **Solution :** `MapTileLoader` expose un drapeau public `public bool isMapLoaded = false;`. `CityGenerator` attend formellement `while (!mapLoader.isMapLoaded) { yield return null; }` avant de construire le NavMesh.

---

### Piège 2 : Les bâtiments "fantômes" pour le Voxelizer (Murs simple-face et sous-sol)
* **Symptôme :** Même après un bake, le NavMesh était généré à travers les bâtiments au niveau du sol (`Y = -0.1f`).
* **Causes :**
  1. Les murs des bâtiments étaient en **simple face** (*single-sided triangles*). Lors de la rastérisation par Recast / Unity, le scanner ignorait les faces arrière (*backface culling*) et ne voyait aucun obstacle en traversant les parois.
  2. Les murs s'arrêtaient au ras du sol (`Y = -0.1f`), permettant au voxelizer d'estimer que l'espace sous le bâtiment était continu avec la rue extérieure.
  3. L'utilisation de `NavMeshObstacle` avec une forme `Box` était inadaptée aux bâtiments aux formes complexes (polygones en L, diagonales, relations OSM), ce qui créait des boîtes englobantes trop grandes ou inefficaces.
* **Solution :**
  1. **Murs double-face** : Les quadrilatères des murs génèrent 4 triangles (2 pour la face extérieure en sens horaire, 2 pour la face intérieure en sens anti-horaire).
  2. **Ancrage sous-terrain** : Les murs s'enfoncent à `Y = -2.0f` sous le sol pour sceller hermétiquement la zone.
  3. **NavMeshModifier Not Walkable** : Chaque bâtiment possède un `NavMeshModifier` avec `overrideArea = true` et `area = 1` ("Not Walkable").
  4. **Purge du cache NavMesh** : Appel systématique de `surface.RemoveData()` avant `surface.BuildNavMesh()`.

---

### Piège 3 : Les saccades de déplacement (*Jittering* avance/recule)
* **Symptôme :** L'unité démarrait son mouvement, avançait physiquement, puis "glissait" ou "se téléportait" brutalement en arrière à chaque boucle de l'animation de course.
* **Causes réelles identifiées :**
  1. **Absence de "Bake Into Pose" (Root Motion)** : L'animation `Rifle Run` de Mixamo n'a pas été exportée "In Place". Les hanches (`Hips`) du personnage avancent physiquement pendant la course. À la fin de la boucle d'animation, les hanches se téléportent instantanément à la frame 0 (en arrière), pendant que le `NavMeshAgent` continue d'avancer de façon fluide. Résultat : le mesh avance hors du cylindre puis clignote en arrière.
  2. **Evaluation erratique dans `Update()`** : L'ancien code `Update()` évaluait faussement `!agent.hasPath` à la frame 1, ce qui coupait l'ordre instantanément.
  3. **Micro-noeuds et surcompensation d'angles** : `PlanifierTourIA` injectait chaque coin de bâtiment comme sous-objectif, créant des micro-arrêts et demi-tours à chaque virage.
  4. **Collision PhysX vs NavMeshAgent** : Le `CapsuleCollider` physique non-trigger repoussait l'agent en arrière lors des contacts avec les murs.
* **Solution Définitive :** 
  1. **Fixer l'animation avec `Bake Into Pose`** : Dans Unity, cocher `Bake Into Pose` (Position XZ et Rotation) pour forcer l'animation à jouer "sur place" sans décaler le squelette. (Utiliser l'outil **StreetAct > 4. Activer le Loop et Fixer les Saccades**)
  2. **Exécution par Coroutine séquentielle (`ExecuteMovementCoroutine`)** : L'exécution attend proprement la fin du calcul et du parcours, avec une seule destination finale par tour.
  3. **`CapsuleCollider.isTrigger = true`** : Zéro conflit de répulsion physique.

---

### Piège 4 : Tracé tactique coupant à travers les bâtiments (`TacticalPathManager`)
* **Symptôme :** La ligne visuelle de planification coupait tout droit à travers les angles des bâtiments.
* **Causes :**
  1. `CalculatePath` retournait un statut `PathPartial` ou échouait si le clic n'était pas parfaitement projeté sur le NavMesh.
  2. En cas d'obstacle, le fallback reliait le point en ligne droite directe au lieu de suivre les coins calculés (`path.corners`).
  3. L'IA (`PlanifierTourIA`) ne stockait que le point d'arrivée et oubliait d'enregistrer les coins intermédiaires (*corners*) dans sa liste de waypoints `tacticalPath`.
* **Solution :**
  1. Projeter systématiquement la cible avec `NavMesh.SamplePosition(targetPos, out hitTarget, 10f, agent.areaMask)`.
  2. Ajouter tous les points `path.corners[j]` dans le `LineRenderer`.
  3. Dans `PlanifierTourIA()`, ajouter chaque `path.corners[i]` intermédiaire comme `TacticalNode` avec l'action `Continuer`.

---

### Piège 5 : Les bâtiments transparents au NavMesh lors du Streaming 3D
* **Symptôme :** Les unités et véhicules traversaient les polygones des bâtiments au sol comme si les murs n'existaient pas.
* **Causes :**
  1. Lors du streaming 3D haute performance, les `MeshRenderer` des murs et toits sont masqués par défaut (`wallsRenderer.enabled = false`).
  2. `NavMeshSurface` configuré en `surface.useGeometry = NavMeshCollectGeometry.RenderMeshes` ignore systématiquement tous les GameObjects dont le renderer est inactif !
  3. Le NavMesh était alors cuit sur une carte plate sans aucun obstacle.
* **Solution :**
  1. Utiliser impérativement `surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;` car les `MeshCollider` des murs restent actifs et hermétiques.
  2. Configurer `TacticalStreamingManager.Instance.RegisterAllBuildings()` immédiatement après le bake du NavMesh.

---

### Piège 6 : Les micro-saccades de recalcul de tracé par frame (`NavMesh.CalculatePath`)
* **Symptôme :** Caméra qui saccade lors des déplacements ou en phase de planification, chute de framerate constante.
* **Cause :** `TacticalPathManager.DessinerTousLesChemins()` exécutait `NavMesh.CalculatePath()` à chaque frame dans `Update()` pour chaque soldat sélectionné ou en mouvement.
* **Solution :**
  1. Mise en cache des points dans `unitAI.cachedDrawPoints`.
  2. Utilisation d'un flag d'invalidation `unitAI.isPathDirty` et `TacticalPathManager.isPathsDirty`. Le recalcul n'est exécuté que lorsqu'un ordre est ajouté, modifié ou supprimé.

---

### Piège 7 : Disparition des bâtiments 3D lors du Streaming
* **Symptôme :** En mode Action 3D, les bâtiments disparaissaient dès que la caméra ou le soldat s'éloignait du centre de la carte.
* **Cause :** Les GameObjects racines des bâtiments générés par `CityGenerator` étaient instanciés à la position `(0,0,0)`. `TacticalStreamingManager` mesurait la distance entre l'origine `(0,0,0)` et la caméra, masquant tous les renderers au-delà de 48m.
* **Solution :**
  1. Calcul et stockage du **centroïde géométrique réel** dans `BuildingStructure.centroid`.
  2. Le chunk de streaming utilise désormais `building.centroid` avec un rayon étendu à **260m**.

---

### Piège 8 : Calculs 3D superflus en vue 2D Commandement
* **Symptôme :** Lenteurs et difficultés à cibler les bâtiments en vue zénithale 2D.
* **Cause :** Utilisation de raycasts PhysX 3D volumiques coûteux alors que la vue est purement plane.
* **Solution :**
  1. Enregistrement du polygone 2D XZ `polygonFootprint` sur `BuildingStructure`.
  2. Détection instantanée Point-in-Polygon via `BuildingStructure.FindBuildingAt(worldPos)` sans aucun coût PhysX.
  3. Menu modal direct pour l'infanterie (Infiltration/RDC, Toit/Sniper, Porte) et passage direct sur les polygones cassés/ruines.

---

## 2. Ordre d'Initialisation Obligatoire du Jeu

```
1. MapTileLoader.Start() 
   └── Télécharge les tuiles OpenStreetMap
   └── Génère le Mesh customisé du Sol (Y = -0.1f)
   └── Met isMapLoaded = true

2. CityGenerator.Start() (en parallèle)
   └── Télécharge le JSON Overpass API
   └── Génère les meshes des bâtiments (murs double-face, -2m, NavMeshModifier Not Walkable)
   └── Attend impérativement : while (!mapLoader.isMapLoaded) yield return null;
   └── Attend 2 frames de synchronisation physique
   └── surface.RemoveData();
   └── surface.BuildNavMesh();
   └── Déclenche unit.OnNavMeshReady() sur toutes les unités actives

3. UnitAI.OnNavMeshReady()
   └── agent.enabled = true;
   └── agent.areaMask = ~(1 << notWalkableArea);
   └── agent.Warp(hit.position);
   └── Prêt pour la phase de planification tactique
```
