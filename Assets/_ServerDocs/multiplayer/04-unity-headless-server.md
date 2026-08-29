# Serveur Unity Headless autoritaire

**État (2026-08-29) : build headless réellement obtenu, déployé et vérifié en direct sur le VPS
(`novgov.com:7777` répond, `MatchSessionManager` charge la carte et écoute).** Voir
[08-known-issues-and-todo.md](08-known-issues-and-todo.md), section 9, pour le détail complet des
blocages réels rencontrés (pas ceux qu'on soupçonnait au départ) et de leur résolution — en
particulier : le sous-cible **Dedicated Server** décrit ci-dessous est bien celui utilisé au final
(`Assets/Editor/ServerBuildScript.cs`), après un détour par un Standalone classique qui s'est
avéré être la mauvaise piste sur cette machine précise (son module d'Éditeur "Linux Build Support"
Standalone n'est en réalité pas installé correctement, seul celui du Dedicated Server l'est).

## Principe

La phase d'exécution d'un tour (`TacticalPathManager.ExecuterTourCoroutine`) fait déjà tourner
une vraie simulation Unity en temps réel pendant plusieurs secondes : NavMeshAgent qui se
déplacent, `Update()` de chaque `UnitAI` qui scanne les cibles visibles 4x/seconde
(`GetVisibleEnemy`, `Physics.RaycastAll`), tirs avec cooldown, dégâts. **Ce n'est pas un calcul
instantané** — c'est pour ça qu'un serveur Unity headless est nécessaire plutôt qu'un service
léger qui recalculerait juste des formules.

Le serveur autoritaire fait tourner exactement cette simulation, dans une scène sans rendu,
et **enregistre** ce qui s'y passe au lieu de l'afficher, pour le renvoyer sous forme de log
(voir [03-network-protocol.md](03-network-protocol.md)).

## Build headless

Unity 6 (2023+) propose un target "Dedicated Server" natif dans Build Settings :
- `File > Build Settings > Dedicated Server` (platform Linux).
- Génère un exécutable qui tourne sans GPU, avec `Application.isBatchMode` = true
  automatiquement.
- Le NavMesh **doit être pré-baké** dans la scène (pas de baking à la volée) — c'est déjà le
  cas ici (`com.unity.ai.navigation` est utilisé pour un bake en edit-time, à vérifier que la
  scène de jeu a bien son NavMesh sauvegardé avec la scène et non régénéré au runtime via un
  script qui dépendrait du rendu).
- Lancement : `./StreetActServer.x86_64 -batchmode -nographics -logFile /dev/stdout` (nom réel de
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
retirer les effets visuels du serveur, mais zéro risque de régression sur le mode solo puisque
`UnitAI_Combat.cs`/`UnitAI_Movement.cs` sont restés intacts à 100%.

## `MatchSessionManager` (implémentation réelle)

Une seule instance active à la fois (V1, cf. README). Responsabilités réelles :

1. `Update()` consomme la file de connexions authentifiées (`GameServerBootstrap.
   AuthenticatedConnections`) et démarre un match dès que 2 joueurs sont en attente.
2. `UnitSpawnerUI.Instance.ClearAllUnits()` puis `AutoDeployBattlefield()` — les DEUX équipes
   sont ensuite forcées à `isPlayerControlled = true` (contrairement au mode solo où l'équipe 2
   est de l'IA) : c'est ce qui permet au garde assoupli de `TacticalAIPlanner` de ne planifier
   que les unités effectivement `isGhosted`, jamais les autres.
3. Boucle de tour (`RunPlanningPhase` puis `RunExecutionPhase`) :
   - Planification : 60s, diffusion de `turn_timer` chaque seconde, réception de `submit_turn`/
     `heartbeat` via `PlayerConnection.TryDequeueMessage`.
   - À l'expiration (ou dès réception des deux `submit_turn`) : tout joueur n'ayant pas soumis
     (ou déconnecté) voit ses unités passer `isGhosted = true` puis planifiées par
     `TacticalAIPlanner.PlanTurnForUnit` — voir section suivante.
   - Exécution : `unit.ExecuterOrdres()` pour toutes les unités vivantes, boucle d'attente
     identique à `TacticalPathManager.ExecuterTourCoroutine` (mouvement puis fenêtre de combat
     de 2s), avec capture d'un `Snapshot` toutes les 100ms pendant toute la durée.
   - `turn_result` envoyé aux deux clients ; persistance minimale du match via PostgREST
     (`/matches`, `/match_participants`, rôle `service_role` — voir plus bas).
4. Fin de partie : un camp à 0 unité vivante → `match_over` + `PATCH /matches` (status,
   winner_team, ended_at).

**Persistance choisie : PostgREST, pas de connexion Postgres directe.** Unity/C# n'a pas de
driver Postgres prêt à l'emploi sans ajouter une dépendance externe (Npgsql + ses dépendances
transitives, parfois délicates en IL2CPP/Mono). Le serveur écrit donc via des requêtes HTTP
`UnityWebRequest` vers PostgREST (`http://rest:3000`, réseau Docker interne) avec le
`SERVICE_ROLE_KEY` en en-tête `Authorization: Bearer` — ce qui contourne RLS exactement comme
une connexion directe l'aurait fait, sans ajouter de dépendance. **Limitation assumée** : le log
détaillé par tour (`match_turn_orders`, `match_event_log` du schéma SQL) n'est pas encore écrit
— seules les tables `matches`/`match_participants` le sont. Suffisant pour le test à 2
téléphones ; à compléter plus tard si un historique de partie détaillé devient nécessaire.

## Le système Ghost = `TacticalAIPlanner` réutilisé

C'est le point le plus important de cette architecture : **pas besoin d'écrire une nouvelle
IA de secours**. `TacticalAIPlanner.PlanTurnForUnit(UnitAI unit)` fait déjà exactement ce
qu'il faut — chercher une cible repérée, se mettre à couvert, tirer, patrouiller sinon.

Actuellement il a un garde-fou en tête de méthode :

```csharp
public static void PlanTurnForUnit(UnitAI unit)
{
    if (unit == null || unit.isDead || unit.isPlayerControlled) return;
    ...
}
```

`unit.isPlayerControlled` bloque volontairement l'IA de jouer les unités du joueur. Pour le
Ghost, il fallait un signal distinct qui autorise l'IA à planifier une unité *appartenant* à un
joueur humain (elle reste `isPlayerControlled = true` pour le scoring/l'affichage — c'est
juste que personne n'a donné d'ordre ce tour-ci).

**Fait** (`UnitAI.cs` a bien le champ `isGhosted`, `TacticalAIPlanner.PlanTurnForUnit` a bien la
condition assouplie ci-dessous) :

```csharp
// UnitAI.cs
public bool isGhosted = false;

// TacticalAIPlanner.cs — condition assouplie
public static void PlanTurnForUnit(UnitAI unit)
{
    if (unit == null || unit.isDead) return;
    if (unit.isPlayerControlled && !unit.isGhosted) return;
    ...
}
```

**Implémentation réelle côté `MatchSessionManager` (légèrement différente de l'esquisse
initiale ci-dessous, voir `ApplyForPlayer` dans le code) :** plutôt que de faire planifier l'unité
par `TacticalAIPlanner` en cas d'absence, le choix final a été plus simple — un joueur absent/pas
soumis voit juste ses unités garder une trajectoire vide (`ClearTacticalPath()`, immobiles ce
tour-ci), sans substitut IA. L'esquisse initialement envisagée était :

```csharp
foreach (var unit in UnitAI.AllLivingUnits.Where(u => u.teamID == disconnectedTeamId))
{
    unit.isGhosted = true;
    TacticalAIPlanner.PlanTurnForUnit(unit);
}
```

Le message réseau `opponent_ghosted` est bien émis vers le client adverse (`reason:
"disconnected"` ou `"timeout"`), pour que l'UI affiche clairement "Adversaire absent". Le nom
d'événement `ghost_activated` évoqué initialement n'a pas été retenu séparément — `opponent_ghosted`
suffit, voir [03-network-protocol.md](03-network-protocol.md).

**Reconnexion pendant qu'un tour Ghost a déjà été résolu** : le tour est définitif dès que
`turn_result` a été calculé et écrit — pas d'annulation rétroactive (cohérent avec un jeu à
tour par tour où on ne peut pas "annuler" un tir déjà résolu). Si le joueur revient au tour
suivant, on repasse `isGhosted = false` et il reprend la main normalement.

## Scaling au-delà d'un match (à ne traiter qu'après le premier test réussi)

Faire tourner plusieurs matchs simultanés dans le même process Unity pose un problème concret :
le NavMesh est en général unique par scène chargée. Deux approches, à évaluer plus tard selon
le volume réel :
1. **Un process serveur headless par match**, orchestré par un petit service de matchmaking qui
   lance `docker run` (ou un pool de processus pré-démarrés) — plus lourd à opérer mais isole
   parfaitement chaque match.
2. **Scènes additives + `NavMeshData` par instance** (`NavMesh.AddNavMeshData`) pour faire
   cohabiter plusieurs simulations isolées dans un seul process — plus économe en ressources
   mais plus complexe à mettre en œuvre correctement (bien vérifier l'isolation des
   `NavMeshQueryFilter`/masks entre instances).

Ne pas implémenter ça avant d'avoir validé le match à 2 joueurs — c'est un problème à résoudre
seulement quand le volume de parties simultanées visées est connu.
