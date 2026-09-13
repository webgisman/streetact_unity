# Deathmatch/Zone de Contrôle basculés vers la famille "vivante" (2026-09-13)

**Statut : implémenté, compile (build serveur Linux headless relancé et vérifié sans erreur), pas
encore testé en conditions réelles (pas de 2ème client disponible dans cette session). À tester au
retour de l'utilisateur — voir "Comment tester" plus bas.**

Ce document explique CE QUI A CHANGÉ suite à la demande explicite de l'utilisateur ("bascule
complètement mon jeu vers ce mode d'Unity sur le serveur, documente tout"), après que
[09-real-unity-combat-investigation-2026-09-13.md](09-real-unity-combat-investigation-2026-09-13.md)
a établi que ce mode ("Live", déjà utilisé par la Conquête) calcule en fait avec le MÊME résolveur
pur déterministe que le mode précédent (`TacticalResolver.Resolve()`) — pas avec le vrai moteur
physique Unity. La bascule a été faite quand même, sur demande explicite réitérée de l'utilisateur.

## Ce qui a changé

Deathmatch et Zone de Contrôle utilisent désormais de vraies `UnitAI`/`BuildingStructure` de scène
côté serveur — exactement comme la Conquête — au lieu de données pures isolées par match
(`MatchState.World`, "Option B" du 2026-08-30).

**Nouveau fichier** : `Assets/Scripts/Server/MatchSessionManager_MatchLive.cs` —
- `TryStartMatchLive` : remplace `TryStartMatch` comme cible des deux files d'attente
  (`waitingDeathmatch`/`waitingZoneControl`) dans `MatchSessionManager.Update()`. Ne démarre un
  nouveau match que si aucun autre (Deathmatch/Zone de Contrôle/Conquête) ne tourne déjà sur ce
  processus (`matchInProgress`).
- `RunMatchLive` : équivalent "vivant" de `RunMatch` (`MatchSessionManager_Matchmaking.cs`, laissé
  intact mais plus appelé) — charge réellement la carte dans la scène serveur
  (`LoadZoneOnServer`/`RestoreDefaultMapOnServer`, comme la Conquête), déploie via
  `RunDeploymentPhaseLive`, boucle `RunPlanningPhaseLiveNoAI`/`RunExecutionPhase` (cette dernière
  INCHANGÉE, déjà partagée avec la Conquête), et détermine la victoire en lisant directement
  `UnitAI.AllLivingUnits`/`CaptureZone.Instance` au lieu de `ms.World`.
- `RunPlanningPhaseLiveNoAI` / `ApplyForPlayerLiveNoAI` : **CORRECTIF IMPORTANT fait pendant cette
  implémentation** — voir section dédiée ci-dessous, ne PAS réutiliser `ApplyForPlayer` tel quel ici.

**Modifié** : `Assets/Scripts/Server/MatchSessionManager_Deployment.cs` — nouvelle méthode
`RunDeploymentPhaseLive` (équivalent vivant de `RunDeploymentPhasePure`, appelle `ResolveDeployment`
au lieu de `ResolveDeploymentPure` — `ResolveDeployment` existait déjà, utilisée jusqu'ici seulement
par la Conquête, généralisée ici aux deux camps humains).

**Modifié** : `Assets/Scripts/Server/MatchSessionManager.cs` — `Update()` appelle maintenant
`TryStartMatchLive` au lieu de `TryStartMatch`.

**Modifié** : `Assets/_ServerDocs/multiplayer/docker-compose.yml` — pool de 3 instances
(`game-server-1/2/3`) rétabli (voir "Compensation de capacité" plus bas).

**Rien de touché côté client** — le protocole réseau (`match_found`/`deployment_result`/
`turn_timer`/`turn_result`/`match_over`) est strictement identique, `RunExecutionPhase` (qui
l'émet) n'a pas changé une seule ligne.

**Familles de code laissées 100% intactes, plus appelées par aucun chemin actif** (rollback
possible, voir plus bas) : toute la famille "Pure" (`RunMatch`, `RunMatchGuarded`, `TryStartMatch`
dans `MatchSessionManager_Matchmaking.cs`, `RunDeploymentPhasePure`/`ResolveDeploymentPure`/
`AutoDeployTeamFallbackPure` dans `_Deployment.cs`, tout `MatchSessionManager_CombatPure.cs`,
`MatchState.cs`).

## Correctif important fait PENDANT cette implémentation (pas dans la demande initiale)

En regardant `ApplyForPlayer` (utilisée par la Conquête pour un joueur absent/en retard), elle
appelle `TacticalAIPlanner`/`unit.PlanifierTourIA()` — une VRAIE IA prend le contrôle des unités du
joueur ghosté. Or l'utilisateur a explicitement demandé, dans une session précédente
(2026-08-30, voir mémoire `project_novgov_scaling_2026-08-30`), **"je ne veux pas d'IA dans le jeu
multijoueur"** — demande déjà satisfaite pour Deathmatch/Zone de Contrôle via `ApplyForPlayerPure`
(un joueur ghosté "tient juste la position", aucune IA).

Réutiliser `ApplyForPlayer` tel quel pour la bascule aurait donc **réintroduit silencieusement une
régression déjà corrigée**. `ApplyForPlayerLiveNoAI` (nouveau, dans `MatchSessionManager_MatchLive.cs`)
reproduit le comportement `ApplyForPlayerPure` sur de vraies `UnitAI` : un joueur ghosté voit
simplement son chemin tactique vidé (`ClearTacticalPath()`, jamais régénéré par une IA) —
`TacticalResolver.Resolve()` évalue quand même les tirs pour toute unité vivante, ghostée ou non,
donc ces unités se défendent normalement sans avancer/flanquer activement.

## Autre correctif fait pendant cette implémentation : Zone de Contrôle n'avait jamais fonctionné côté "vivant"

`CaptureZone.CreateAtMapCenter()` n'était appelé **nulle part** dans tout le projet avant cette
session (recherche exhaustive effectuée) — `CaptureZone.Instance` aurait donc toujours été `null`,
et le garde `if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)` dans
`RunExecutionPhase` aurait silencieusement tout ignoré : la logique de capture de zone (déjà
présente dans ce fichier) n'avait jamais eu de composant réel à piloter. `RunMatchLive` crée
maintenant explicitement une `CaptureZone` fraîche au centre de la carte au début de chaque match
`zone_control`, et la détruit à la fin (le prochain match, quel qu'il soit, repart d'une zone
neutre).

## Conséquences assumées (demandées explicitement par l'utilisateur, redites ici pour mémoire)

1. **Un seul match à la fois par processus, tous modes confondus** (Deathmatch + Zone de Contrôle +
   Conquête partagent maintenant le même verrou `matchInProgress`, tenu pour toute la durée d'un
   match). C'est exactement la contrainte que l'Option B du 2026-08-30 avait supprimée — voir
   `09-real-unity-combat-investigation-2026-09-13.md` pour la discussion complète du pourquoi (le
   calcul est identique dans les deux familles, donc ce changement n'apporte aucune fidélité
   supplémentaire, uniquement cette régression de capacité, en échange de vraies `UnitAI`/
   `BuildingStructure` de scène).
2. **Chargement de carte toujours "à froid"** pour une tuile GPS réelle : contrairement à la famille
   Pure (qui pouvait servir une géométrie déjà en cache disque en ~65ms, voir
   `project_novgov_scaling_2026-08-30`), la famille Live doit toujours recharger la VRAIE scène
   (`LoadZoneOnServer`), jusqu'à ~60s dans le pire cas — même pour une tuile déjà vue. C'est ainsi
   que fonctionne déjà la Conquête, non modifié ici, mais c'est une régression de latence pour
   Deathmatch/Zone de Contrôle qui n'existait pas avant.
3. **`city_verify` (2ème étage d'équité géométrique) non branché sur ce chemin** — voir la limitation
   documentée dans `RunDeploymentPhaseLive`. Le 1er étage (même JSON envoyé aux deux clients dans
   `match_found`) reste actif.

## Compensation de capacité : pool de 3 instances rétabli

`docker-compose.yml` fait de nouveau tourner `game-server-1/2/3` (ports 7777/7778/7779) au lieu
d'une seule instance — donne 3 matchs simultanés au lieu d'un seul sur le même VPS. Toujours très
loin des "milliers" de l'Option B, mais un vrai mieux que 1. **Si un vrai déploiement VPS est fait
depuis cette branche** : rouvrir les ports 7778/7779 dans UFW (fermés lors de la consolidation à 1
instance, voir `01-deployment-vps.md` §10) — `ufw allow 7778/tcp && ufw allow 7779/tcp`.

## Comment tester (à faire au retour, pas fait dans cette session — pas de 2ème client disponible)

1. Build le serveur Linux headless (déjà fait une fois avec succès dans cette session — voir
   `build_server_2026-09-13b.log` à la racine du projet, aucune erreur de compilation) :
   `Unity.exe -batchmode -nographics -quit -buildTarget Linux64 -standaloneBuildSubtarget Server
   -projectPath "E:\NOVGOV\My project" -executeMethod ServerBuildScript.BuildLinuxServer`.
2. Lancer le serveur en local (ou le déployer sur un VPS de test, JAMAIS directement sur
   `novgov.com` en production tant que ce n'est pas retesté).
3. Deux clients (deux comptes distincts) rejoignent Deathmatch OU Zone de Contrôle. Vérifier :
   déploiement manuel des deux côtés, tour de planification, `turn_result` reçu par les deux, mort
   d'unité, victoire par élimination.
4. Spécifiquement pour Zone de Contrôle : vérifier que la progression de capture avance réellement
   (elle n'avait jamais été exercée avant cette session sur ce chemin, voir plus haut).
5. Lancer un match, puis pendant qu'il tourne, essayer d'en démarrer un second (2 autres comptes) :
   doit rester en file d'attente jusqu'à la fin du premier — c'est le changement de comportement
   attendu (1 seul instance) ou tenter un second sur `game-server-2` (port 7778) pour vérifier le
   pool à 3.

## Comment revenir en arrière si besoin

Tout le code d'origine est intact. Il suffit d'annuler UN SEUL changement dans
`MatchSessionManager.cs` (`Update()`) : remplacer `TryStartMatchLive(...)` par `TryStartMatch(...)`
pour les deux files d'attente. Le pool à 3 instances dans `docker-compose.yml` peut rester tel quel
(sans effet négatif) ou être ramené à 1 instance si on revient à la famille Pure.
