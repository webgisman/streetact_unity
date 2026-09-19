# Architecture actuelle du multijoueur Novgov (2026-09-13) — DOCUMENT DE RÉFÉRENCE

**Ce document remplace la description d'architecture des documents 04 et README.md pour tout ce qui
concerne le calcul de combat et le déploiement.** Les documents 01/02/03/05/06/07 restent valides
(déploiement VPS, auth, protocole réseau, checklist sécurité, plan de test) sauf mention contraire
ci-dessous. Les documents 08 à 12 sont l'historique détaillé, session par session, qui a mené à
l'état décrit ici — utiles pour comprendre POURQUOI, pas pour savoir CE QUI EST VRAI AUJOURD'HUI :
en cas de contradiction entre ce document et un document plus ancien, **celui-ci fait foi**.

## En une phrase

Tous les modes de jeu serveur (Deathmatch, Zone de Contrôle, Conquête, entraînement IA) font
tourner de vraies `UnitAI`/`BuildingStructure` dans la scène du serveur, résolues par le VRAI moteur
Unity (NavMesh + Physics.RaycastAll temps réel) — pas par un résolveur pur/déterministe. Deux
rythmes de jeu coexistent ("fast" 5 min/tour, "async" jusqu'à 6h/tour).

## Ce qui a changé le 2026-09-13, et pourquoi (résumé — détail dans 09/10/11)

Avant cette date, Deathmatch/Zone de Contrôle utilisaient un résolveur pur et déterministe
(`Novgov.TacticalCore.TacticalResolver.Resolve()`) précisément pour éviter le non-déterminisme de
PhysX/NavMesh entre appareils différents, et pour faire tourner des milliers de parties en
parallèle sur un seul processus (aucune vraie scène/GameObject par partie). L'utilisateur a demandé
explicitement de revenir au vrai moteur pour tous les modes, en connaissance de cause de ce que ça
coûte :
- **Un seul match à la fois par processus** (tous modes/rythmes confondus, hors rythme async — voir
  plus bas), la scène étant de nouveau un GameObject partagé. Compensé partiellement en remettant un
  pool de 3 instances (`game-server-1/2/3`).
- **Aucune garantie de déterminisme inter-appareils** — PhysX/NavMesh peuvent en théorie donner un
  résultat légèrement différent selon l'appareil qui les exécute. Comme un seul côté (le serveur)
  calcule et diffuse le résultat aux deux clients (jamais les clients eux-mêmes), ce n'est pas un
  risque de désynchronisation entre joueurs — mais ça reste une garantie en moins par rapport au
  résolveur pur.
- **Chargement de carte plus lent** pour une tuile GPS réelle (toujours "à froid", jusqu'à 60s) — le
  résolveur pur pouvait servir une géométrie déjà en cache disque en ~65ms.

**Mise à jour du 2026-09-13 (plus tard le même jour) : `TacticalResolver.Resolve()` et
`LineOfSight.cs` ont été supprimés du dépôt.** Ils avaient zéro appelant en production depuis le
passage ci-dessus, et le test-harness qui leur était rattaché (11 fichiers
`Assets/Editor/TacticalCoreSelfTest_*.cs` + `Tools/TacticalCoreTests/TacticalCoreSelfTest_FullMatchSim.cs`)
testait exclusivement ce résolveur — retiré avec lui. Ce qui reste dans ce test-harness (A*/
`TacticalGrid` non-régression, hash déterministe, sentinelles client, plafonds de déploiement) ne
dépendait jamais de `TacticalResolver`/`LineOfSight` et a été préservé tel quel, vérifié par un
compile Editor + un build serveur réels après coup. Toute la famille "Pure" (`MatchState.cs`,
`MatchSessionManager_CombatPure.cs`, `RunMatch`/`TryStartMatch` d'origine, `RunDeploymentPhasePure`,
etc.) avait déjà été supprimée plus tôt le même jour, pour la même raison (plus aucun mode ne
l'appelait).

## Comment ça marche concrètement aujourd'hui

### Résolution de combat — `MatchSessionManager_CombatRealEngine.cs`

`RunExecutionPhaseRealEngine` est le SEUL point d'entrée de résolution de tour, pour tous les
modes : Deathmatch/Zone de Contrôle (`RunMatchLive`), Conquête et entraînement IA
(`MatchSessionManager_Conquest.cs`). Il lance réellement `UnitAI.ExecuterOrdres()` (même code que le
bouton "FIN DE TOUR" en Solo, jamais modifié), échantillonne l'état réel des unités à intervalle
régulier (`TickDurationMs`), et envoie ces échantillons au client sous forme de `Snapshot[]` —
protocole réseau identique à avant, le client n'a rien eu à changer.

Le brouillard de guerre est calculé via `UnitAI.IsUnitSpottedByTeam` (la même primitive que
`TacticalAIPlanner` utilise en Solo), pas via l'ancien `ComputeVisibleUnitIds` (qui lisait un
`TacticalWorldState`, structure du résolveur pur).

### Déploiement — `MatchSessionManager_Deployment.cs`

`ResolveDeployment` spawn de VRAIES `UnitAI`/`RoadBarrier` (via `UnitSpawnerUI.SpawnUnitAt`), pour
les deux équipes humaines de Deathmatch/Zone de Contrôle comme pour la Conquête. Le joueur peut
choisir librement parmi les 5 types d'unité (Fantassin/CharLeopard/VehiculeCanon/Mortier/Barricade)
dans la limite de `MaxDeployedCombatUnits` (6, relevé de 4 le 2026-09-13 — voir
`MultiplayerMatchController.OpenDeploymentDock`) et `CombatPointBudget` (8 points).

### Deux rythmes de jeu — `NetMessage.turn_pace`, `MatchSessionManager_AsyncPause.cs`

- **"fast"** (défaut, 5 min max/tour) : comportement d'origine, la scène/le verrou `matchInProgress`
  restent tenus pour toute la durée du match.
- **"async"** (jusqu'à 6h max/tour) : entre deux tours, l'effectif réel (position/rotation/PV/type/
  équipe/garnison/intérieur/guet/camouflage/perché-toit) est sérialisé dans
  `matches.paused_roster_json`, les GameObjects sont détruits, et `matchInProgress` est libéré
  pendant l'attente — sans quoi un seul match async bloquerait tous les autres pendant des heures.
  Au réveil (les deux joueurs ont soumis, ou le délai est écoulé) : reprise du verrou, rechargement
  de la carte, respawn de l'effectif (mêmes noms de GameObject, pour que les ordres déjà soumis
  continuent de désigner les bonnes unités), reprise normale. La progression de Zone de Contrôle
  survit aussi à une pause (`CaptureZone.RestoreProgress`).

Deux joueurs ne sont appariés que s'ils demandent le même mode ET le même rythme (files d'attente
séparées : `waitingDeathmatch(Async)`/`waitingZoneControl(Async)`).

### Notifications — `public.notifications` (schema.sql)

Une simple table Postgres protégée par RLS (pas d'email/push tiers) — le serveur y écrit une ligne
"C'est ton tour" quand un joueur en rythme async attend l'autre. **Rien côté client ne lit encore
ces notifications** — seule l'écriture serveur existe pour l'instant.

## Ce qui n'a PAS été fait (limites connues, assumées)

- ~~`TacticalResolver.cs`/`LineOfSight.cs` ne sont PAS supprimés~~ **FAIT plus tard le 2026-09-13** —
  supprimés avec les 8 fichiers de test qui les testaient exclusivement (`TacticalCoreSelfTest.cs`,
  `_BuildingEntry`, `_BuildingIntrusion`, `_DoorEntry`, `_InfantryPathSim`, `_ResolveMutatesInPlace`,
  `_Roof`, `_VerticalBudget`, `_Wait`, et `Tools/TacticalCoreTests/TacticalCoreSelfTest_FullMatchSim.cs`
  partiellement) ; `TacticalCoreSelfTest.cs`, `_Client.cs` et le FullMatchSim ont été conservés mais
  réduits à ce qui ne dépendait pas du résolveur (A*/`TacticalGrid`, hash déterministe, sentinelles
  client, plafonds de déploiement) — re-vérifié par un compile Editor et un build serveur réels.
- ~~Aucun test réel à 2 joueurs depuis la bascule du 2026-09-13~~ **PARTIELLEMENT FAIT le
  2026-09-19** — un vrai match (matchmaking, déploiement, un tour réel via le vrai moteur) a été
  joué de bout en bout par deux comptes authentifiés distincts, voir
  [13-session-2026-09-19-compile-fixes-real-2p-test.md](13-session-2026-09-19-compile-fixes-real-2p-test.md).
  **Toujours pas testé** : le cycle pause/reprise async, le choix élargi de types d'unité au
  déploiement, et tout rendu VISUEL côté client (le test du 2026-09-19 parle le protocole réseau
  directement, sans client Unity réel à l'écran).
- **Notifications** : écriture serveur seule, aucune lecture/affichage côté client. **Toujours vrai
  au 2026-09-16.**
- **Migration base de données appliquée en production** (`novgov.com`) le 2026-09-13 — voir
  [12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) pour le détail
  de ce qui a été vérifié (et ce qui ne l'a pas été : la correction du gameplay lui-même).
- **L'attribution "qui tire sur qui" affichée au client reste une approximation** (voir
  `MatchSessionManager_CombatRealEngine.CaptureRealEngineSnapshot`, `shooting`/`shoot_target_id`) :
  ces champs reflètent "cet ennemi est visible et à portée ce tick", pas nécessairement l'unité
  exacte qui inflige les dégâts observés ce tick-là. Le tracé/flash rejoué côté client (voir
  correctif du 2026-09-16 ci-dessous) peut donc occasionnellement pointer vers la mauvaise unité —
  connu et accepté comme simplification cosmétique, pas encore résolu à la racine (demanderait de
  faire remonter le VRAI événement de tir depuis `UnitAI_Combat` au lieu d'une reconstruction
  après coup).

## Correctifs du 2026-09-16 — clarté du combat + régression de posture

Retour utilisateur : *"lors de l'action les joueurs ne comprennent rien, il y a des morts sans
savoir pourquoi"*. Audit + corrections :

- **Tir de mortier invisible en rejeu réseau** : `UnitAI_Visuals.PlayNetworkShotEffects` ignorait
  purement et simplement tout tir de mortier (`if (isMortar) return;`, commentaire historique
  évoquant un "faux tireur mortar" — décrivait en réalité l'ANCIEN résolveur "Pure", supprimé la
  veille de l'écriture de ce commentaire). Une unité tuée par un obus mourait donc sans la moindre
  explosion/tremblement de caméra visibles. Corrigé : `PlayNetworkMortarImpactEffects` rejoue une
  explosion cosmétique au point d'impact (même son/flash que `MortarShell.Detonate`), sans prétendre
  reconstruire la trajectoire réelle de l'obus (position du mortier tireur non transmise).
- **Aucun fil de combat** : ajout de `CombatFeedUI.cs` + l'élément `combat-feed` dans
  `InMatchHudScreen.uxml` — chaque mort (solo ET réseau, un seul point d'appel commun dans
  `UnitAI.Die()`) pose une ligne "☠ &lt;Unité&gt; détruit(e)" colorée par équipe, y compris pour une
  mort qui a eu lieu hors du champ de vision de la caméra du joueur.
- **Régression `isGuarding`** trouvée en auditant `08-known-issues-and-todo.md` §19.9.6 : le
  correctif documenté au §19.15 point 4 (2026-09-12) vivait dans `TacticalResolver.Resolve`,
  supprimé le lendemain avec toute la famille "Pure" — le bug d'origine (posture de Guet = -50%
  dégâts subis PERMANENT dès le premier "Guetter" du match, jamais réinitialisé) était donc de
  nouveau actif en production sans qu'aucun document ne le signale. Corrigé dans
  `UnitAI_Movement.cs` : `isGuarding` repasse à `false` dès qu'une unité repart vers son prochain
  checkpoint.
- **Cache de schéma PostgREST périmé** (incident opérationnel, pas un bug de code) : `rest` n'avait
  pas rechargé son schéma depuis ~10 jours, faisant échouer silencieusement toute écriture de
  `profiles.action_points` (`PGRST204`, log `[MatchSessionManager] Mise à jour DB échouée`). Un
  simple `docker compose restart rest` a suffi — confirmé par l'absence totale de nouvelles erreurs
  sur plusieurs cycles de revenu de zone après coup. Aucune donnée perdue (le calcul en mémoire
  restait correct, seule la persistance échouait).

## Historique (pour comprendre le raisonnement, pas l'état actuel)

- [09-real-unity-combat-investigation-2026-09-13.md](09-real-unity-combat-investigation-2026-09-13.md) — pourquoi la Conquête ne faisait PAS déjà tourner "le vrai moteur" avant cette bascule.
- [10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md](10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md) — première étape (Deathmatch/Zone de Contrôle → vraies UnitAI).
- [11-real-engine-async-pace-notifications-2026-09-13.md](11-real-engine-async-pace-notifications-2026-09-13.md) — extension à tous les modes + rythmes + notifications.
- [12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) — déploiement réel sur novgov.com.
- [08-known-issues-and-todo.md](08-known-issues-and-todo.md) — tout l'historique antérieur (2026-08 à début 09), y compris la construction de l'architecture "Option B"/résolveur pur que cette session a remplacée.
