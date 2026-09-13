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

`TacticalResolver.Resolve()` lui-même a zéro appelant aujourd'hui — le code est resté en place
(`Assets/Scripts/TacticalCore/TacticalResolver.cs`/`LineOfSight.cs`) car un test-harness entier lui
est encore rattaché (voir "Ce qui n'a PAS été fait" en bas). Toute la famille "Pure"
(`MatchState.cs`, `MatchSessionManager_CombatPure.cs`, `RunMatch`/`TryStartMatch` d'origine,
`RunDeploymentPhasePure`, etc.) a en revanche été supprimée : elle ne servait plus à rien une fois
plus aucun mode ne l'appelait.

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

- **`TacticalResolver.cs`/`LineOfSight.cs` ne sont PAS supprimés** malgré zéro appelant en jeu : un
  test-harness entier en dépend (`Assets/Editor/TacticalCoreSelfTest_*.cs`, 11 fichiers, et
  `Tools/TacticalCoreTests/`). Les supprimer sans traiter ces tests casserait la compilation. Décider
  du sort de ces tests (les supprimer avec le résolveur, ou les garder comme filet de sécurité si le
  résolveur pur est un jour réactivé) est une décision séparée, pas prise dans cette session.
- **Aucun test réel à 2 joueurs** depuis la bascule du 2026-09-13 — ni pour le vrai moteur, ni pour
  le cycle pause/reprise async, ni pour le choix élargi de types d'unité au déploiement.
- **Notifications** : écriture serveur seule, aucune lecture/affichage côté client.
- **Migration base de données appliquée en production** (`novgov.com`) le 2026-09-13 — voir
  [12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) pour le détail
  de ce qui a été vérifié (et ce qui ne l'a pas été : la correction du gameplay lui-même).

## Historique (pour comprendre le raisonnement, pas l'état actuel)

- [09-real-unity-combat-investigation-2026-09-13.md](09-real-unity-combat-investigation-2026-09-13.md) — pourquoi la Conquête ne faisait PAS déjà tourner "le vrai moteur" avant cette bascule.
- [10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md](10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md) — première étape (Deathmatch/Zone de Contrôle → vraies UnitAI).
- [11-real-engine-async-pace-notifications-2026-09-13.md](11-real-engine-async-pace-notifications-2026-09-13.md) — extension à tous les modes + rythmes + notifications.
- [12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) — déploiement réel sur novgov.com.
- [08-known-issues-and-todo.md](08-known-issues-and-todo.md) — tout l'historique antérieur (2026-08 à début 09), y compris la construction de l'architecture "Option B"/résolveur pur que cette session a remplacée.
