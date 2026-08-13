# Documentation Technique - Projet Jeu Tactique 2D (Unity 6)

## 1. Vue d'Ensemble
Le projet est un jeu tactique 2D se déroulant dans un environnement urbain réel généré procéduralement à partir des données d'OpenStreetMap (OSM).
Le système repose sur deux composants principaux :
- **CityGenerator.cs** : Responsable de la génération de la géométrie 3D des bâtiments (vecteurs).
- **MapTileLoader.cs** : Responsable du téléchargement et de l'assemblage de la carte raster (images) pour le sol.

---

## 2. Le Défi de l'Alignement et la Projection Web Mercator
L'un des défis majeurs du projet était d'aligner au pixel près les bâtiments 3D générés par l'API Overpass avec les tuiles d'images (raster) téléchargées depuis OSM. 

### Pourquoi un décalage se produit-il ?
Par défaut, si l'on convertit naïvement des coordonnées géographiques (Latitude / Longitude - EPSG:4326) en mètres sur un plan plat (x, z), on obtient une grille linéaire. Or, les cartes en ligne (comme OSM, Google Maps) utilisent une projection cartographique appelée **Web Mercator (EPSG:3857)**. Cette projection déforme mathématiquement la carte (surtout l'axe Y/Latitude) à mesure que l'on s'éloigne de l'équateur.

Si les bâtiments utilisent un calcul linéaire et la carte une projection Mercator, un décalage visuel (plusieurs mètres) apparaît, les bâtiments ne se superposant pas aux dessins des rues.

### La Solution Mathématique
Les deux scripts partagent désormais **exactement le même algorithme de projection Web Mercator**.
La formule utilisée pour projeter un point `(lat, lon)` en coordonnées Unity `(x, z)` est la suivante :

```csharp
double R = 6378137.0; // Rayon de la Terre en mètres (Standard EPSG:3857)

// 1. Conversion en radians
double lonRad = lon * Math.PI / 180.0;
double latRad = lat * Math.PI / 180.0;

// 2. Projection Web Mercator pure
double mercatorX = R * lonRad;
double mercatorY = R * Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));
```

Pour que le centre de la ville soit toujours à `(0, 0, 0)` dans Unity, on soustrait les coordonnées Mercator du centre (`centerX`, `centerY`). 
Enfin, on applique un facteur d'échelle `cos(latitude)` pour éviter que la ville ne paraisse géante (compensation de la distorsion Mercator).

---

## 3. Architecture des Scripts

### CityGenerator.cs
- **Rôle** : Télécharge les données vectorielles via **Overpass API**.
- **Entrées** : `latitude`, `longitude`, `radius` (rayon en mètres).
- **Fonctionnement** :
  1. Construit une requête OverpassQL pour trouver tous les `way` et `relation` de type `building` dans le rayon.
  2. Parse le JSON renvoyé (`out geom;` fournit les coordonnées de chaque nœud).
  3. Projette les coordonnées des nœuds via la formule Web Mercator.
  4. Triangule les polygones (Ear Clipping) en gérant les "trous" (holes) pour les bâtiments complexes.
  5. Génère un Mesh 3D et l'extrude à la hauteur `buildingHeight`.

### MapTileLoader.cs
- **Rôle** : Télécharge les tuiles (Tiles) PNG depuis l'API standard OSM.
- **Entrées** : Lit *automatiquement* la `latitude`, `longitude` et le `radius` directement depuis `CityGenerator` pour garantir l'absence d'erreurs de frappe.
- **Fonctionnement** :
  1. Calcule la bounding box mathématique requise.
  2. Convertit cette zone en indices de tuiles `(TileX, TileY)` selon le niveau de `zoom` (ex: 18).
  3. Télécharge toutes les tuiles nécessaires via `UnityWebRequestTexture`.
  4. Assemble les tuiles en une seule grande `Texture2D`.
  5. **Génération du Sol** : Au lieu d'utiliser un `Plane` Unity (qui peut avoir des problèmes de pivot ou d'échelle), le script génère un **Mesh Quad 100% personnalisé** dont les 4 sommets sont placés *exactement* aux coordonnées Web Mercator des bords extérieurs des tuiles téléchargées.
  6. Force le Material (`Unlit`) à avoir un `Tiling` de 1 et un `Offset` de 0.

---

## 4. Pièges Fréquents pour les Futurs Développements

### A. L'illusion d'optique (Parallaxe 3D)
Si la hauteur des bâtiments (`buildingHeight`) est supérieure à 0 (ex: 5 mètres), et que la caméra du jeu n'est pas **strictement Orthographique ET orientée à 90° vers le bas (Top-Down)**, les toits des bâtiments masqueront le sol avec un décalage visuel (parallaxe). 
*Note : C'est un effet optique normal en 3D. La base du bâtiment, elle, reste parfaitement alignée sur la route.*

### B. NavMesh et Hauteur des bâtiments
Lors du **Bake du NavMesh**, Unity utilise la géométrie de la scène.
Si les bâtiments ont une hauteur de `0.1m` (pour tester l'alignement plat), l'agent NavMesh risque de les considérer comme un "petit trottoir" (si `Max Step Height` > 0.1) et permettra aux unités de marcher dessus. 
*Solution* : Toujours Baker le NavMesh avec les bâtiments à leur vraie hauteur (ex: 5m).

### C. Mode Édition vs Mode Play
Le `MapTileLoader` effectue des requêtes asynchrones lourdes (Coroutines). Il est conçu pour télécharger et afficher la carte **uniquement lors du lancement du mode Play**. Les modifications manuelles de l'objet `Sol` en mode Édition seront automatiquement écrasées et corrigées par le script au démarrage du jeu.

### D. Génération Dynamique du NavMesh et Pathfinding
Trois éléments critiques ont été corrigés pour assurer que le NavMesh contourne parfaitement les bâtiments générés :
1. **La hauteur du Plancher (Floor) pour le Voxelizer** : Le NavMesh d'Unity "scanne" la scène en voxels (souvent 0.2m de haut). Si le sol interne d'un bâtiment est trop proche du sol de la carte (`Sol`), le Voxelizer les fusionne et rend le bâtiment franchissable. Le sol interne a été remonté à `Y = 1.0m` pour garantir qu'Unity détecte la coupure, tout en empêchant les agents de passer en dessous (hauteur de 1.1m < agent de 2.0m).
2. **Le Static Batching** : Lors de la génération procédurale, assigner `isStatic = true` aux bâtiments verrouille l'accès en lecture à leurs meshes (Combined Mesh) au lancement du mode Play. Le `NavMeshSurface` ne pouvant pas les lire, il les ignorait. Les bâtiments ne doivent **pas** être marqués comme statiques via script.
3. **Synchronisation (Race Condition)** : Si l'agent IA cherche un chemin avant que les bâtiments (qui se téléchargent depuis internet) n'apparaissent et que le NavMesh ne soit mis à jour, l'agent marchera à travers eux. Le flux a été repensé pour que `CityGenerator` lance le téléchargement, attende le `Sol`, cuise (bake) le NavMesh *complet*, puis seulement déclenche un événement `OnNavMeshReady()` autorisant les unités à se déplacer.
