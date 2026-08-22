# Règles d'Importation Mixamo & Mécaniques du Projet

Ce document sert de référence pour toutes les IA et développeurs travaillant sur la partie 3D, Animation, et Mécaniques de ce projet. Il corrige d'anciennes erreurs de diagnostic et liste les outils d'automatisation disponibles.

---

## 1. Importation Mixamo : Le Bug "Moitié Sous Terre" (La Vraie Solution)

Historiquement, de faux diagnostics ont poussé à utiliser le type `Generic` pour importer les FBX Mixamo, par peur de "casser le mesh". **C'était une grave erreur.**

### Le Problème du type `Generic`
Quand on utilise un fichier FBX importé en `Generic` (ex: les animations *Death From The Front.fbx* ou *Firing Rifle.fbx*) sur un `Animator Controller` qui pilote un personnage `Humanoid` :
* **L'animation ne se joue pas** (le personnage fige).
* **Le bassin (Hips) tombe à l'origine (0, 0, 0)** du monde, ce qui a pour effet d'enfoncer la moitié du corps du personnage (les jambes) exactement sous le sol !

### La Solution Définitive (Standard de Projet)
Pour tout nouveau fichier FBX importé depuis Mixamo (personnage OU animation) :
1. **Scale Factor** : Laissez `1.0` (Unity convertit automatiquement les centimètres en mètres). Le personnage fait bien 1.7m. Ne **JAMAIS** modifier le `localScale` par script.
2. **Animation Type** : `Humanoid` (**OBLIGATOIRE**).
3. **Avatar Definition** : `Create From This Model`.

> **Outils Automatiques disponibles :**
> - **`StreetAct > 1. Réparer les Squelettes 3D (Humanoid)`** : Analyse tous les FBX du projet et les convertit en Humanoid avec Avatar.
> - **`Tools > Corriger les Animations (Mettre en Humanoid + Bake Y)`** : Force le type Humanoid et verrouille l'axe vertical Y sur les animations de pose.

---

## 2. Élimination des Saccades de Course (Bake Into Pose)

Pour les animations de déplacement (ex: `Rifle Run.fbx`) :
- Si l'animation n'est pas exportée "In Place", les hanches avancent et se téléportent en arrière à la fin du cycle.
- **Règle absolue** : Cocher `Bake Into Pose` (Position XZ et Rotation).

> **Outil Automatique :**
> - **`StreetAct > 4. Activer le Loop et Fixer les Saccades d'Animation (Bake Into Pose)`** : Applique en un clic le bouclage continu et le verrouillage de position sans toucher au Root Motion de Unity.

---

## 3. Configuration Automatique de l'Animator

Les unités utilisent des `Triggers` et `Booleans` pour déclencher des états spécifiques (`IsShooting`, `Hit`, `Die`, `Climb`).
Au lieu de configurer manuellement le `UnitAnimator.controller` (création des transitions, décochage du "Has Exit Time", etc.) :

> **Outil Automatique :**
> - **`Tools > Configurer Animator Unités (Hit, Death et Escalade)`** : Construit automatiquement tout l'arbre d'état de l'Animator Controller avec les bonnes transitions et paramètres.

---

## 4. Système de Sang Procédural (Gratuit & Zéro Asset)

Inutile de chercher et télécharger des textures de sang sur l'Asset Store.
Le script `UnitAI_Visuals.cs` embarque la fonction `SpawnBloodEffect` qui **génère un système de particules Unity 100% par le code** à chaque fois qu'une balle touche l'unité :
- Le sang (rouge sombre) gicle en forme de cône.
- La direction des particules est calculée dynamiquement selon la trajectoire de la balle entrante.
- L'effet est automatiquement détruit après 1.5 seconde pour optimiser les performances.

---

## 5. Caméra Tactique Hybride (2D / 3D)

La caméra (`TacticalCamera.cs` & `CameraStateManager.cs`) offre une expérience fluide, ultra-légère et sans saccade :
- **Calcul en `LateUpdate()`** : Synchronisation parfaite post-animation pour éliminer tout micro-bégaiement (*stutter*).
- **Mode 2D Commandement** : Vue zénithale à 90°, recul optimal (`orthoSize = 95f`), zoom fluide continu (40f à 180f) et sélection polygonale légère.
- **Mode 3D Action** : Vue isométrique immersive, recul de `65m` (zoom 25m à 180m), rotation orbitale à 360° fluide par clic droit ou twist tactile, `farClip = 400m` et brume atmosphérique linéaire.
- **Contrôles Universels** : Support complet du *New Input System* pour Mobile (Pinch-zoom, Pan tactile, Twist) et PC (Z/Q/S/D, Molette, Glisser-déplacer).

---

## 6. NavMesh et Initialisation Sécurisée

Le NavMesh étant généré dynamiquement au démarrage (`CityGenerator`) :
- Le `NavMeshAgent` de l'unité lance des erreurs s'il s'active avant que le sol ne soit cuit.
- L'agent est désactivé dans `Start()` : `agent.enabled = false;`.
- Le `CityGenerator` appelle `OnNavMeshReady()` sur les unités une fois le bake terminé.
- Le script `UnitAI_Movement.cs` exclut les zones "Not Walkable", configure l'évitement d'obstacles RVO (`HighQualityObstacleAvoidance`), réactive l'agent, et plaque le personnage au sol avec `agent.Warp()`.
