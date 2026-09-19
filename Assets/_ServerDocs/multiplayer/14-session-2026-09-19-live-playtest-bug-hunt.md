# Session du 2026-09-19 (nuit) — chasse aux bugs pendant un vrai test à 2 joueurs en direct

**Contexte** : après la session précédente (voir doc 13 — compilation cassée réparée, premier vrai
test à 2 joueurs), l'utilisateur a testé le jeu EN DIRECT (Multiplayer Play Mode, un "Joueur
Virtuel" + l'Éditeur principal, contre le vrai serveur de production novgov.com), pendant que cette
session continuait de tourner en parallèle. Plusieurs bugs réels ont été trouvés en direct, via le
retour du joueur ET le diagnostic à l'écran (`TapDiagnosticOverlay`). Certains diagnostics initiaux
se sont avérés faux (voir §0) avant de trouver les vraies causes.

## 0. Fausses pistes écartées avant de trouver les vrais bugs

- **Hypothèse "rechargement de domaine Unity en plein test"** : en début de session, les modifications
  de fichiers pendant que l'utilisateur testait en Play Mode ont été soupçonnées de perturber son état
  de jeu. Partiellement vrai en théorie (Unity recompile et recharge le domaine pendant le Play Mode),
  mais ce n'était PAS la cause des bugs réels trouvés ensuite — de vrais bugs de logique existaient
  indépendamment de tout rechargement.
- **Hypothèse "le canon charge le mauvais modèle 3D (Resources.Load échoue)"** : vérifié directement
  (chargement du prefab `engins/canon-vehicle` en conditions réelles) — le modèle se charge
  correctement, 128 pièces de géométrie, complètement différent du char. Cette hypothèse était fausse
  — la vraie cause était ailleurs (voir §3).

## 1. Cercle de déploiement fantôme — corrigé

**Symptôme** : un placement d'unité hors d'un cercle de ±25m autour du centre de la carte était
refusé côté client avec le message *"Hors de votre zone de départ ! Déployez à l'intérieur du
cercle de votre camp."*, alors que cette restriction avait été explicitement désactivée côté serveur
le 2026-09-06 ("le placement manuel est accepté n'importe où sur la carte").

**Cause** : `UnitSpawnerUI.IsInsideDeploymentZone`/`outOfDeploymentZone` — un garde-fou client jamais
retiré quand la restriction serveur (`ClampToDeploymentZone`) avait été désactivée. Une variante
morte du marqueur visuel (`ShowDeploymentZoneMarker`, jamais appelée) existait aussi.

**Corrigé** : garde-fou et marqueur mort supprimés entièrement (`UnitSpawnerUI.cs`).

## 2. Budget de déploiement en points — supprimé sur demande explicite

**Symptôme** : message *"Une partie de votre déploiement dépassait le budget autorisé (unités trop
lourdes) — le reste a été posé tel quel."*, jamais demandé par l'utilisateur.

**Cause** : `MatchSessionManager.CombatPointBudget` (8 points, coût par unité via
`UnitTypeStats.DeploymentCost`) — ajouté lors d'une session précédente (2026-09-06) pour limiter la
"puissance" totale déployée, pas seulement le nombre d'unités.

**Corrigé** : le budget en points est entièrement retiré (serveur ET client, y compris son affichage
"Points : X/8" dans le dock). Les plafonds de COMPTAGE (6 unités de combat, 8 barricades, 2
mortiers) restent inchangés — ce n'est que le calcul de "poids" par type qui a disparu.

## 3. LE bug principal : un Véhicule Canon se transformait en Char Leopard après confirmation du déploiement

**Symptôme rapporté** : *"j'ai le canon lors de déploiement et après il se retransforme en char"*.
Confirmé réel après enquête — l'utilisateur avait raison, malgré une première vérification (voir §0)
qui semblait indiquer le contraire.

**Cause exacte** (tracée dans le code, pas supposée) :

`UnitSpawnerUI.SpawnUnitAt` réglait `isTank = true` de façon SYNCHRONE (immédiatement, dans le même
appel) pour CharLeopard, VehiculeCanon ET Mortier — et réglait AUSSI `isMortar = true` de façon
synchrone pour Mortier. Mais **`isCanonVehicle = true` n'était réglé que plus tard**, dans
`UnitAI.Start()` (détection par le nom de l'objet, `objName.Contains("canon")`) — qu'Unity ne
déclenche qu'à la frame suivante après un `Instantiate()`, jamais dans le même appel synchrone.

`MatchSessionManager_Deployment.ResolveDeployment` (serveur), juste après avoir spawné les unités
d'un joueur, construit la liste `deployed_units` à renvoyer aux DEUX clients en appelant
`UnitTypeStats.InferType(u)` sur chaque unité — **dans le MÊME appel synchrone**, donc AVANT que
`Start()` n'ait eu la moindre chance de tourner. `InferType` teste dans l'ordre `isMortar` →
`isCanonVehicle` → `isTank` → repli sur Fantassin. À cet instant précis, un Véhicule Canon
fraîchement posé a `isTank=true` mais `isCanonVehicle=false` (pas encore réglé) → `InferType`
retombe à tort sur **CharLeopard**.

Ce mauvais type numérique est alors envoyé aux deux clients dans `deployment_result`, qui
respawnent l'unité avec le MAUVAIS MODÈLE 3D (Char au lieu de Canon) — alors que le NOM de l'unité
(`unit_id`, construit séparément à partir du type LOCAL connu au moment du tap, jamais affecté par
ce bug) reste correct ("Canon_Vehicule_X_Y") du début à la fin. **C'est exactement pourquoi la
première vérification (§0, recherche du nom dans les logs) n'avait rien trouvé d'anormal** : le nom
ment jamais, seul le numéro de type utilisé pour choisir le modèle 3D était faux.

**Corrigé** : `ai.isCanonVehicle = true` est maintenant réglé de façon synchrone dans
`SpawnUnitAt`, au même endroit que `isTank`, exactement comme le fait déjà `isMortar` pour le
mortier. Corrige la course à la SOURCE pour tous les lecteurs (serveur `ResolveDeployment`, client
`MultiplayerMatchController.InferUnitType`, barre d'escouade, titre du menu d'ordre) au lieu de
patcher chaque lecteur séparément.

**Verrouillé par un test** : `Assets/Editor/UnitTypeSyncAutoTest.cs` reproduit exactement ce timing
(spawn via `SpawnUnitAt`, lecture IMMÉDIATE de `InferType`, **aucun appel à `Start()`**) pour les 4
types d'unité — CharLeopard/VehiculeCanon/Mortier/Fantassin — empêchant cette régression précise de
revenir un jour sans être détectée.

**Amélioration annexe** : le menu d'ordre affichait le même titre générique "BLINDÉ" pour un char ET
un canon (mais PAS pour le mortier, qui avait déjà son propre titre "ARTILLERIE"). Corrigé pour
afficher "CHAR : ORDRE DE MANOEUVRE" ou "VÉHICULE CANON : ORDRE DE MANOEUVRE" selon le cas — la
barre d'escouade (icônes en haut à gauche), elle, distinguait déjà correctement les 4 catégories
(`RefreshSquadBar`, code préexistant et correct).

## 4. Taps silencieusement absorbés par l'interface — trouvé et généralisé

**Symptôme rapporté** : cliquer/taper sur une unité (ex: un fantassin) "ne fait rien", sans le
moindre message d'erreur, à répétition.

**Diagnostic** (`TapDiagnosticOverlay`, déjà présent dans le projet, amélioré ce soir pour nommer
l'élément responsable) : *"Tap absorbé par : VisualElement 'top-bar' (picking-mode=Position) —
aucune action 3D."* — `top-bar` est un simple conteneur de mise en page (bannière d'équipe) dans
`InMatchHudScreen.uxml`.

**Cause racine** : `UIScreenManager.cs` documentait déjà un piège Unity connu — la propriété USS
`picking-mode: Ignore` déclarée dans le style inline d'un UXML **ne s'applique pas de façon
fiable** au premier `Instantiate()` (vérifié empiriquement : l'élément reste "cliquable"/
`PickingMode.Position` malgré la déclaration). Ce bug était déjà corrigé, mais **seulement pour
l'élément nommé "root" de chaque écran** — tout le reste de l'arbre de chaque UXML restait exposé
au même bug, silencieusement. `top-bar` n'était que le premier symptôme retrouvé ce soir, rien ne
garantissait qu'il soit le seul parmi les ~20 écrans du projet.

**Corrigé en deux temps** :
1. D'abord un correctif ciblé (`top-bar` ajouté à la liste des éléments forcés en `Ignore`, comme
   "root").
2. Puis un correctif **généralisé**, sur demande explicite ("règle tout en profondeur") :
   `UnitSpawnerUI.IsPointerOverOnGUI` fonctionnait comme une LISTE NOIRE ("tout élément avec
   `picking-mode == Position` absorbe, sauf quelques exceptions déjà repérées") — remplacée par une
   **liste blanche**. Recherche exhaustive (`grep` de tous les `.uxml`/`.cs` du projet) : seuls 4
   conteneurs du projet entier veulent réellement absorber un tap (le fond du menu contextuel, le
   cluster de boutons du bandeau tactile du bas, son groupe "exécution", et les panneaux à classe
   `dock-panel`/`context-panel`). Seuls Button/ScrollView/TextField et ces 4 conteneurs précis
   absorbent désormais un tap — n'importe quel autre élément laisse passer, quel que soit son
   `picking-mode` résolu (fiable ou non). Rend cette classe entière de bug structurellement
   impossible au lieu de nécessiter une nouvelle exception nommée à chaque nouveau rapport.
3. Un correctif antérieur dans la même veine (même soir) : un `Label` seul n'est jamais un contrôle
   interactif nulle part dans le projet (confirmé par recherche) — un simple texte décoratif
   (bannière, en-tête, message de statut) ne doit jamais absorber de tap.

## 5. Rappel : mécanisme du jeu, pas un bug (voir aussi la question du joueur)

Un bâtiment détruit par un bombardement écrase instantanément (9999 dégâts, voir
`DestructibleEnvironment.DestroyEnvironmentInternal`) toute unité — alliée OU ennemie — dont la
position tombe dans son empreinte à l'instant de l'effondrement. Une unité (y compris un mortier)
positionnée dans ou près d'un bâtiment qui s'effondre sous un tir d'artillerie meurt de
l'effondrement, pas d'un tir direct. Comportement voulu, déjà présent avant cette session — clarifié
ici car source de confusion en jouant.

## Fichiers modifiés cette session (en plus du doc 13)

- `Assets/Scripts/UI/UnitSpawnerUI.cs` — retrait cercle de déploiement + budget, correctif
  `isCanonVehicle`, généralisation `IsPointerOverOnGUI`.
- `Assets/Scripts/Server/MatchSessionManager_Deployment.cs` — retrait `CombatPointBudget`.
- `Assets/Scripts/Server/UnitTypeStats.cs` — retrait `DeploymentCost` (devenu mort).
- `Assets/Scripts/AI/Input/UnitSelectionResolver.cs` — bonus de tolérance au tap pour l'infanterie.
- `Assets/Scripts/AI/Input/TacticalPathManager_Input.cs` — diagnostic de tap enrichi (nomme
  l'élément absorbant).
- `Assets/Scripts/AI/TacticalPathManager_ContextMenu.cs` — titre de menu distinct char/canon.
- `Assets/Scripts/UI/UIScreenManager.cs` — correctif picking-mode généralisé (root + top-bar).
- `Assets/Editor/UnitTypeSyncAutoTest.cs` (nouveau) — verrou de non-régression pour le bug §3.

Tout reconstruit (client + serveur) et redéployé sur les 3 instances de novgov.com après chaque
lot de correctifs, 5 suites de tests Éditeur systématiquement revérifiées au vert.

## Ce qui reste à surveiller

- La sélection d'unité "semble résolue" au retour du joueur au moment d'écrire ce document, mais
  n'a pas de confirmation définitive sur une session de jeu complète — à re-confirmer.
- Le correctif `isCanonVehicle` ferme la course pour TOUS les lecteurs connus de `InferType`
  (recherché exhaustivement), mais aucune autre unité/drapeau n'a été auditée pour un défaut
  symétrique (un flag réglé seulement par `Start()` et lu ailleurs de façon synchrone) — le test
  `UnitTypeSyncAutoTest` couvre les 4 types actuels, pas un futur 5e type ajouté sans y penser.
