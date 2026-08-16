# Règles d'Importation Mixamo & Mécaniques du Projet

Ce document sert de référence pour toutes les IA et développeurs travaillant sur la partie 3D, Animation, et Mécaniques de ce projet. Il corrige d'anciennes erreurs de diagnostic et liste les outils d'automatisation disponibles.

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

> **Outil Automatique :** Pour gagner du temps, utilisez le script personnalisé via le menu **`Tools -> Corriger les Animations (Mettre en Humanoid)`**. Il analysera tout le dossier `Assets` et configurera correctement tous les FBX en Humanoid.

---

## 2. Configuration de l'Animator

Les unités utilisent des `Triggers` pour déclencher des états spécifiques (ex: `Hit` pour la douleur, `Die` pour la mort).
Au lieu de configurer manuellement le `UnitAnimator.controller` (création des transitions, décochage du "Has Exit Time", etc.) :

> **Outil Automatique :** Cliquez sur le menu **`Tools -> Configurer Animator Unités (Hit et Death)`**. Ce script va récupérer les animations FBX, créer les états, et générer le graphe complet de l'Animator automatiquement !

---

## 3. Système de Sang Procédural (Gratuit & Zéro Asset)

Inutile de chercher et télécharger des textures de sang sur l'Asset Store.
Le script `UnitAI.cs` embarque la fonction `SpawnBloodEffect` qui **génère un système de particules Unity 100% par le code** à chaque fois qu'une balle touche l'unité.
- Le sang (rouge sombre) gicle en forme de cône.
- La direction des particules est calculée dynamiquement selon la trajectoire de la balle entrante.
- L'effet est automatiquement détruit après 1.5 seconde pour optimiser les performances.

---

## 4. Caméra Tactique Hybride (2D / 3D)

La caméra (`TacticalCamera.cs` & `CameraStateManager.cs`) offre une expérience fluide, ultra-légère et sans saccade :
- **Calcul en `LateUpdate()`** : Synchronisation parfaite post-animation pour éliminer tout micro-bégaiement (*stutter*).
- **Mode 2D Commandement** : Vue zénithale à 90°, recul optimal (`orthoSize = 95f`), zoom fluide continu (40f à 180f) et sélection polygonale légère.
- **Mode 3D Action** : Vue isométrique immersive, recul de `65m` (zoom 25m à 180m), rotation orbitale à 360° fluide par clic droit ou twist tactile, `farClip = 400m` et brume atmosphérique linéaire.
- **Contrôles Universels** : Support complet du *New Input System* pour Mobile (Pinch-zoom, Pan tactile, Twist) et PC (Z/Q/S/D, Molette, Glisser-déplacer).

---

## 5. NavMesh et Initialisation

Le NavMesh étant généré dynamiquement au démarrage (`CityGenerator`) :
- Le `NavMeshAgent` de l'unité lance des erreurs s'il s'active avant que le sol ne soit cuit.
- L'agent est désactivé dans `Start()` : `agent.enabled = false;`.
- Le `CityGenerator` appelle `OnNavMeshReady()` sur les unités.
- Le script `UnitAI` exclut les zones "Not Walkable", réactive l'agent, et plaque le personnage au sol avec `agent.Warp()`.
