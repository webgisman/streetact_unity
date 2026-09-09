# Serveur Unity Headless autoritaire

**État (2026-08-30) : build headless déployé et vérifié en direct sur le VPS — 1 seule instance
(`game-server-1`, `novgov.com:7777`).** Voir [08-known-issues-and-todo.md](08-known-issues-and-todo.md)
pour l'historique détaillé, mais **ce document a été réécrit le 2026-08-30 pour décrire
l'architecture RÉELLEMENT en place aujourd'hui** — tout ce qui suit sur "Principe"/
"MatchSessionManager"/"Scaling" ci-dessous n'est PAS le texte d'origine (qui décrivait une
simulation NavMeshAgent temps réel, remplacée depuis) : voir §"Historique" tout en bas si besoin de
retrouver l'ancienne description.

## Principe (2026-08-30)

Le calcul de combat lui-même ne tourne plus en temps réel du tout. `Assets/Scripts/TacticalCore/`
(`TacticalResolver`, `TacticalTypes`, `Pathfinding`, `LineOfSight`, `GeometryMath`, `TacticalGrid`,
`TacticalGridBuilder`) est un moteur de résolution **pur** : `TacticalResolver.Resolve(worldState,
ordersEquipe1, ordersEquipe2, mortarStrikes)` prend un état de départ + les ordres des deux camps et
renvoie instantanément (quelques millisecondes à quelques dizaines de ms) la liste complète des
événements du tour (déplacements, tirs, morts) — sans NavMeshAgent, sans `Physics.RaycastAll`,
sans dépendance à une scène Unity vivante. Le rendu (client) rejoue simplement cette liste
d'événements ; il ne recalcule jamais rien.

Pour Deathmatch/Zone de Contrôle, ce calcul tourne même sur un **thread d'arrière-plan**
(`Task.Run`, voir `MatchSessionManager.RunExecutionPhasePure`) — safe car l'état d'une partie
(`MatchState.World`) n'est jamais partagé avec une autre partie.

Le mode **Conquête** (`RunConquestSkirmish`) fait exception : il reste basé sur de vraies
`UnitAI`/`BuildingStructure` de scène (nécessaire pour charger une vraie géométrie GPS via
`CityGenerator`), tourne sur le thread principal, et reste limité à un combat à la fois par
processus — voir "Scaling" plus bas pour le détail de cette distinction.

## Build headless

Unity 6 (2023+) propose un target "Dedicated Server" natif dans Build Settings :
- `File > Build Settings > Dedicated Server` (platform Linux).
- Génère un exécutable qui tourne sans GPU, avec `Application.isBatchMode` = true
  automatiquement.
- Le NavMesh **doit être pré-baké** dans la scène (pas de baking à la volée) — c'est déjà le
  cas ici (`com.unity.ai.navigation` est utilisé pour un bake en edit-time, à vérifier que la
  scène de jeu a bien son NavMesh sauvegardé avec la scène et non régénéré au runtime via un
  script qui dépendrait du rendu).
- Lancement : `./NovgovServer.x86_64 -batchmode -nographics -logFile /dev/stdout` (nom réel de
  l'exécutable produit par `ServerBuildScript.BuildLinuxServer` — voir le `Dockerfile` dans
  `Assets/_ServerDocs/multiplayer/game-server/`, qui utilise exactement cette commande en
  `ENTRYPOINT`).

## Séparation client/serveur dans le code (implémentation réelle)

Décision prise à l'implémentation, plus simple que ce qui était envisagé ci-dessus : **une
seule scène partagée** entre le build client et le build serveur (pas de scène
`GameplayServer.unity` séparée). Le Dedicated Server build target d'Unity 6 désactive déjà tout
le pipeline de rendu/IMGUI/audio automatiquement ; il suffisait d'ajouter des gardes
`#if UNITY_SERVER return; #endif` en tête des `OnGUI()` de `GameManagerUI`, `UnitSpawnerUI` et
`TacticalPathManager` (Unity définit `UNITY_SERVER` automatiquement pour ce build target) pour
éviter tout appel `GUI.*` côté serveur. Aucune asmdef séparée n'a été nécessaire : les scripts
serveur (`Assets/Scripts/Server/*.cs`) référencent simplement les types globaux existants
(`UnitAI`, `TacticalAIPlanner`, `UnitSpawnerUI`, etc.) sans dépendance circulaire, et ne sont
jamais instanciés côté client puisque leur point d'entrée (`GameServerBootstrap.Bootstrap()`)
est lui-même entièrement gardé par `#if UNITY_SERVER`.

Fichiers serveur réels :
- `Assets/Scripts/Server/GameServerBootstrap.cs` — écoute TCP, handshake JWT, point d'entrée
  `[RuntimeInitializeOnLoadMethod]`.
- `Assets/Scripts/Server/PlayerConnection.cs` — une connexion joueur authentifiée (thread de
  lecture dédié + file thread-safe).
- `Assets/Scripts/Server/JwtValidator.cs` — vérification HMAC-SHA256 locale du JWT GoTrue.
- `Assets/Scripts/Server/MatchSessionManager.cs` — orchestration du match (voir ci-dessous).

**`UnitAI_Combat.ShootAt()` n'a PAS été modifié** — contrairement à ce qui était envisagé, le
serveur n'instrumente aucun call site individuel. Il laisse tourner la simulation réelle
(particules/audio inclus — inoffensifs mais inutiles en headless, non bloquants) et se contente
d'échantillonner l'état des unités à intervalle régulier (voir "Rejeu par snapshots" dans
[03-network-protocol.md](03-network-protocol.md)). C'est délibérément moins "propre" que de
retirer les effets visuels du serveur, mais zéro risque de régression sur le mode solo.

**Mise à jour du 2026-08-29** : `UnitAI_Combat.cs` n'est en fait plus resté intact à 100% —
`Update()` a dû recevoir un garde `if (!MultiplayerMatchController.IsActive)` autour de tout son
bloc de combat temps réel (scan de cible, tir, contrôle de l'Animator), voir
`08-known-issues-and-todo.md` §10.4. Ce garde ne change RIEN au comportement décrit ci-dessus côté
serveur (`MultiplayerMatchController.IsActive` y vaut toujours `false`, cette classe n'existant que
côté client) ni en solo (`IsActive` n'est jamais mis à `true` hors multijoueur) — il empêche
seulement chaque CLIENT de faire tourner par erreur sa propre simulation de combat locale (fumée/
tir fantômes, désynchronisés de l'autre téléphone) pendant qu'il attend le `turn_result` du
serveur. `UnitAI_Movement.cs`, lui, est resté intact à 100%.

## `MatchSessionManager` (état réel, 2026-08-30)

Deathmatch et Zone de Contrôle utilisent une famille de méthodes "Pure" (`RunMatchPure`→
`RunDeploymentPhasePure`→`RunPlanningPhasePure`→`RunExecutionPhasePure`, voir `MatchSessionManager.cs`)
qui ne touchent JAMAIS un GameObject de scène après l'instant de démarrage. La Conquête
(`RunConquestSkirmish`) garde l'ANCIENNE famille de méthodes (`RunDeploymentPhase`/
`RunPlanningPhase`/`RunExecutionPhase`, toujours basées sur de vraies `UnitAI`/`BuildingStructure`)
— les deux familles coexistent dans le même fichier, jamais mélangées.

1. `Update()` consomme la file de connexions authentifiées, bucket par mode (deathmatch/
   zone_control/conquest), et démarre un nouveau match dès que 2 joueurs distincts attendent sur
   le même mode — **sans aucune limite sur le nombre de matchs simultanés** pour deathmatch/
   zone_control (`TryStartMatch` ne vérifie plus qu'un seul match tourne déjà).
2. **Géométrie de la partie** (`EnsureTileLoadedAndSnapshot`) : la tuile retenue (celle d'un des
   deux joueurs si l'un a une position GPS connue, "Default" sinon — voir `DetermineMatchCacheKey`)
   est chargée UNE FOIS dans `MatchState.World` (`Novgov.TacticalCore.TacticalWorldState` — murs,
   grille de marche, portes/fenêtres/hauteur par bâtiment). Si cette tuile est déjà en cache
   (mémoire ou disque, `TacticalGridBuilder`), c'est instantané et **aucun verrou n'est nécessaire**
   — plusieurs parties sur des tuiles différentes démarrent alors vraiment en parallèle. Si la
   tuile n'a jamais été vue, une génération réelle (fetch OpenStreetMap + bake NavMesh, comme la
   Conquête) est déclenchée UNE fois (anti-doublon si deux parties visent la même tuile neuve en
   même temps), protégée par le même verrou bref que la Conquête (`matchInProgress`) et une limite
   de fréquence par utilisateur (`CanTriggerTileGeneration`). Après cet instant, `ms.World` ne
   touche plus jamais la scène — deux parties sur deux tuiles différentes ne se marchent jamais
   dessus (voir `Novgov.Server.MatchGeometry`, qui remplace toutes les requêtes géométriques par
   des équivalents purs lisant `ms.World`).
3. Déploiement (`RunDeploymentPhasePure`) : jusqu'à `DeploymentSeconds` = 300 s (300 s de marge
   depuis chaque joueur est PRÊT, pas depuis le début de la phase — voir `MapReadyMaxWaitSeconds`
   plus bas), placement manuel en parallèle (`submit_deployment`), plus AUCUN recadrage de zone
   (`ClampToDeploymentZone` désactivée le 2026-09-06, sur demande explicite), repli automatique par
   camp sinon — mais les unités déployées sont directement des `Novgov.TacticalCore.TacticalUnit`
   (données pures, voir `MatchState.cs`), **aucune vraie `UnitAI` n'est jamais instanciée** pour ces
   deux modes. Voir [03-network-protocol.md](03-network-protocol.md) pour le budget exact
   (6 unités de combat + 8 barricades + un budget en points).
4. Boucle de tour (`RunPlanningPhasePure` puis `RunExecutionPhasePure`) :
   - Planification : `PlanningSeconds` = 300 s (60 s à l'origine, relevé le 2026-09-07 pour être
     cohérent avec le déploiement — c'est la phase qui demande le plus de travail au joueur, et le
     compte à rebours ne s'affiche plus, voir plus bas), `turn_timer` diffusé chaque seconde.
   - Un joueur absent/pas soumis : ses unités "tiennent la position" ce tour-ci (aucun ordre) —
     voir "Ghost" plus bas, différent de la Conquête.
   - Exécution : `TacticalResolver.Resolve(ms.World, ordersEquipe1, ordersEquipe2, mortarStrikes)`
     — appelé sur un thread d'arrière-plan (`Task.Run`), calcule INSTANTANÉMENT (quelques ms à
     quelques dizaines de ms) tous les événements du tour. Le rejeu tick-par-tick envoyé au client
     (`turn_result`/`Snapshot[]`) est reconstruit à partir de ce journal d'événements — le serveur
     ne "joue" plus le tour en temps réel, il calcule le résultat puis raconte l'histoire.
     **Amorcé depuis une copie PAR VALEUR de l'état de début de tour** (`UnitTurnStart`, ajouté le
     2026-09-07) : `TacticalUnit` est une classe que `Resolve` mute EN PLACE, donc garder une
     simple référence pour amorcer le rejeu — ce qui était fait avant cette date — finissait par
     rejouer le tour à partir de son PROPRE résultat (position déjà finale au premier snapshot, PV
     retranchés deux fois). Le chemin "vivant" (Conquête/entraînement, plus bas) n'était pas
     concerné : il résout sur un `TacticalWorldState` distinct des vraies `UnitAI` dont il amorce
     le rejeu.
   - `turn_result` envoyé aux deux clients (filtré par brouillard de guerre, voir
     `ComputeVisibleUnitIds`) ; persistance minimale du match via PostgREST.
5. Fin de partie : un camp à 0 unité vivante (ou plafond de tours atteint) → `match_over` +
   `PATCH /matches`.

**Persistance PostgREST inchangée depuis l'origine** (voir plus bas, non retouché aujourd'hui) :
`UnityWebRequest` + `SERVICE_ROLE_KEY`, pas de driver Postgres direct. **Limitation toujours
assumée** : le log détaillé par tour (`match_turn_orders`/`match_event_log`) n'est toujours pas
écrit — seules `matches`/`match_participants` le sont (RLS activée sur `match_turn_orders` depuis
l'audit de sécurité du 2026-08-30, donc pas accessible directement par un client même si elle
était un jour peuplée par erreur).

## Signal de vie serveur → client (heartbeat), 2026-09-07

Le client applique un `ReceiveTimeout` FINI de 25 s à sa socket (`GameServerClient.Connect`) — 25 s
sans le moindre octet reçu et il se déconnecte avec "connexion perdue". Or le serveur pouvait
rester longtemps sans rien envoyer à une connexion authentifiée : un joueur seul en file d'attente
(avant même l'étape 1), l'attente d'une génération de tuile inédite avant `match_found`, la
Conquête, l'entraînement contre l'IA... Un keepalive existait déjà mais était LOCAL à la phase de
déploiement (`RunDeploymentPhasePure`), donc absent de toutes les autres phases — un joueur seul en
file d'attente pour Deathmatch/Zone de Contrôle était donc **systématiquement déconnecté avant
même de pouvoir être apparié**, rendant ces deux modes pratiquement injouables sans un second
joueur immédiat.

Le signal de vie est désormais une propriété de la **connexion**, pas d'une phase :
`PlayerConnection.PumpKeepalives()`, appelé en toute première ligne de `MatchSessionManager.Update()`,
envoie un `heartbeat` à toute connexion authentifiée restée silencieuse plus de
`PlayerConnection.KeepaliveIntervalSeconds` = 8 s (tout envoi réel repousse d'autant le prochain).
Le client n'a rien à en faire — son `switch` sur les messages entrants ignore ce type — c'est
l'ARRIVÉE de la trame qui réarme le timeout de sa socket. Toute future phase ajoutée au serveur en
bénéficie automatiquement, sans rien avoir à implémenter.

## Le système Ghost — deux implémentations différentes selon le mode

**Solo et Conquête** (inchangé) : `TacticalAIPlanner.PlanTurnForUnit(UnitAI unit)` — vraie IA
active (cherche une cible, se met à couvert, tire, patrouille), avec le garde assoupli
`if (unit.isPlayerControlled && !unit.isGhosted) return;` pour autoriser l'IA à jouer les unités
d'un joueur humain absent sans y toucher sinon.

**Deathmatch/Zone de Contrôle** (2026-08-30, différent — voir `ApplyForPlayerPure`) : pas de vraie
IA de décision (`TacticalAIPlanner` dépend de `UnitAI.AllLivingUnits`, une liste globale
incompatible avec des parties concurrentes) — un joueur absent/en retard voit simplement ses
unités "tenir la position" ce tour-ci (aucun ordre soumis). Ce n'est PAS une unité totalement
passive : `TacticalResolver.Resolve()` évalue les tirs pour TOUTE unité vivante à chaque tick, pas
seulement celles avec un ordre actif — une unité "fantôme" se défend donc normalement si un
ennemi entre à portée, elle n'avance/ne flanque juste pas activement. Compromis assumé
(complexité/risque de réimplémenter l'IA complète en données pures vs. bénéfice pour un cas
marginal), signalé explicitement à l'utilisateur.

Le message réseau `opponent_ghosted` est émis dans les deux cas, inchangé — voir
[03-network-protocol.md](03-network-protocol.md).

## Scaling au-delà d'un match — RÉSOLU le 2026-08-30

La question posée dans la version précédente de ce document ("comment faire tourner plusieurs
matchs simultanés, le NavMesh étant unique par scène ?") a été résolue en éliminant le besoin d'un
NavMesh/d'une scène vivante DU TOUT pour Deathmatch/Zone de Contrôle une fois une partie démarrée
— ni l'option "un process par match", ni "NavMeshData additif" (les deux options envisagées à
l'origine) n'ont été nécessaires :

- La géométrie de pathfinding est une **grille pure** (`TacticalGrid`, voir `TacticalGridBuilder`),
  jamais un vrai NavMesh, mise en cache par tuile (mémoire + disque, partagée entre parties ET
  entre redémarrages de processus).
- Les unités sont des données pures (`TacticalUnit`), jamais de vraies `UnitAI`/GameObjects.
- `TacticalResolver.Resolve()` ne dépend d'aucun état partagé mutable entre parties.

Résultat : **un seul processus Unity fait tourner des centaines/milliers de parties Deathmatch/
Zone de Contrôle en parallèle** — voir la note de mémoire de session
`project_novgov_scaling_2026-08-30.md` pour l'historique complet de cette refonte (Phases 1 à 4).
Le pool Docker est revenu à une seule instance (`game-server-1`) en conséquence — voir
[01-deployment-vps.md](01-deployment-vps.md).

**Ce qui reste une vraie limite, documentée et acceptée** : la Conquête (géométrie GPS réelle
chargée dans la scène vivante, vraies `UnitAI`) reste 1 combat à la fois par processus, et un
combat de Conquête en cours retarde le démarrage de toute NOUVELLE partie Deathmatch/Zone de
Contrôle sur une tuile jamais vue (les deux ont besoin de la scène vivante pour se générer) — les
parties Deathmatch/Zone de Contrôle DÉJÀ démarrées, elles, ne sont jamais affectées. Générer
plusieurs tuiles neuves en parallèle nécessiterait un processus "cartographe" dédié, séparé du
service de parties — pas fait, considéré comme un raffinement v2 si le besoin se confirme.
