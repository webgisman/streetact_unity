# Intégration côté client Unity

**Ce document décrit le PLAN initial, écrit avant l'implémentation.** L'implémentation réelle a
pris quelques raccourcis différents une fois en pratique — voir le code directement pour la
vérité (`Assets/Scripts/Network/MultiplayerMatchController.cs`) :
- Pas de `LoginUI.cs`/`MatchmakingUI.cs` séparés : **une seule classe**,
  `MultiplayerMatchController`, gère auth + sélection de mode + attente + HUD de match + fin de
  partie, chacun via un écran `UIScreenManager` dédié (`Auth`, `ModeSelect`, `Waiting`,
  `InMatchHud`, `MatchOver`) — pas d'IMGUI/`ProceduralIconFactory`, tout en UI Toolkit (UXML/USS),
  comme le reste du projet aujourd'hui (l'UI est passée d'IMGUI à UI Toolkit après l'écriture de
  ce document).
- L'écran de démarrage (`GameManagerUI`) n'a **que 2 boutons** : "MODE SOLO" (avec un interrupteur
  Hotseat pour jouer à 2 sur le même appareil) et "MODE CAMPAGNE MULTIJOUEUR" (qui enchaîne GPS
  puis login/matchmaking) — pas 3 boutons séparés comme envisagé initialement.
- Le "rejeu" des événements serveur s'appelle `MultiplayerMatchController.PlaySnapshotsCoroutine`
  (pas `LireTurnResultCoroutine` sur `TacticalPathManager`) et l'envoi des ordres se fait via
  `MultiplayerMatchController.SubmitLocalTurn()`, appelée depuis
  `TacticalPathManager.LancerExecutionTour()` quand `MultiplayerMatchController.IsActive` est vrai
  — l'intention ci-dessous ("attendre le message turn_result... nouvelle coroutine") est bien ce
  qui a été fait, juste avec ces noms réels.
- **Mise à jour du 2026-08-29 (session bugs post-test-2-téléphones)** : le déploiement automatique
  et symétrique décrit plus bas (`UnitSpawnerUI.AutoDeployBattlefield()`) a été **remplacé** par une
  vraie phase de placement manuel (jusqu'à 45s, en parallèle pour les deux joueurs) — voir
  `08-known-issues-and-todo.md` §10.2/§10.4 et `03-network-protocol.md` ("submit_deployment"/
  "deployment_result") pour le détail complet. `AutoDeployBattlefield()` reste utilisée en solo
  uniquement ; côté serveur, `AutoDeployTeamFallback()` (repli PAR CAMP) prend le relais si un
  joueur ne soumet rien de valide à temps.

Le reste de ce document (flux cible, répartition des responsabilités serveur/client) reste une
bonne description de l'intention et de ce qui a effectivement été construit — seuls les noms de
fichiers/classes ci-dessus ont changé en pratique.

## Ce qui change dans le flux existant

Aujourd'hui (`GameManagerUI.OnGUI`) : au lancement, l'écran "NOVGOV : CHAMP DE BATAILLE"
propose directement "Combat urbain hors-ligne" ou "Ma position GPS réelle" — pas d'auth, pas de
matchmaking, tout est local et instantané.

Flux cible :

```
Démarrage
  → Écran Login/Inscription (nouveau)
  → Écran choix de mode : "Solo (hors-ligne, existant)" vs "Multijoueur"
      → Solo : flux actuel inchangé, aucune régression.
      → Multijoueur : connexion TCP au serveur (03-network-protocol.md), "join_matchmaking",
        écran d'attente → match_found → déploiement des unités → boucle de tour réseau.
```

**Important : ne pas casser le mode solo existant.** Le mode solo continue à utiliser
`TacticalAIPlanner` en local exactement comme aujourd'hui. Le mode multijoueur est un chemin
parallèle, pas un remplacement.

## Nouveaux scripts côté client

- `Assets/Scripts/Auth/SupabaseAuthClient.cs` — voir
  [02-auth-supabase-unity.md](02-auth-supabase-unity.md).
- `Assets/Scripts/Auth/LoginUI.cs` — écran login/inscription (peut réutiliser le style IMGUI +
  `ProceduralIconFactory` déjà utilisé partout ailleurs dans le projet, pour rester cohérent
  visuellement avec `GameManagerUI`/`UnitSpawnerUI`).
- `Assets/Scripts/Network/GameServerClient.cs` — connexion TCP, encodage/décodage des trames
  définies dans le protocole, dispatch des messages reçus.
- `Assets/Scripts/Network/MatchmakingUI.cs` — écran "Recherche d'adversaire...", affichage du
  timer de tour, notification "Adversaire absent — IA de secours active" (`opponent_ghosted`).

## Modifications sur les scripts existants

### `GameManagerUI.cs`
- `isMapSelectorOpen` : ajouter un état préalable (login) avant que l'écran actuel de sélection
  de carte s'affiche. Le plus simple : un nouveau booléen `isLoginOpen` vérifié avant
  `isMapSelectorOpen` dans `OnGUI()`, suivant le même pattern (fade, `ProceduralIconFactory.
  IconButton`).
- Ajouter un bouton "⚔️ MULTIJOUEUR" à côté des deux boutons existants
  (`OnClickLoadDefaultOfflineMap` / GPS réel), qui lance `MatchmakingUI` au lieu de charger une
  carte locale.

### `TacticalPathManager.cs`
- `LancerExecutionTour()` : en mode multijoueur, au lieu d'appeler directement
  `unit.PlanifierTourIA()` puis `unit.ExecuterOrdres()` localement pour les unités adverses,
  envoyer un message `submit_turn` avec le `tacticalPath` de chaque unité du joueur
  (`isPlayerControlled && teamID == monEquipe`), puis **attendre** le message `turn_result` du
  serveur avant de lancer une coroutine de lecture.
- Nouvelle coroutine `LireTurnResultCoroutine(TurnResultMessage result)` : au lieu de simuler
  localement (NavMeshAgent réel, Update() de combat), elle **rejoue** les événements reçus —
  déplace les transforms le long du chemin en interpolant vers `final_positions`, déclenche les
  effets visuels (`ShootAt` visuel seul, sans recalcul de dégâts) aux timestamps `t` indiqués
  dans `events`. Le calcul de vérité (qui touche qui, qui meurt) ne se refait jamais côté
  client — le client illustre seulement ce que le serveur a décidé.
- Le mode solo garde `ExecuterTourCoroutine` telle quelle, inchangée.

### `UnitSpawnerUI.cs`
**Mise à jour du 2026-08-29 (implémentation réelle, différente de l'intention initiale ci-dessous) :**
le multijoueur V1 utilise bien le dock de déploiement solo existant (`StartPlacingUnit`,
`SpawnUnitAt`), pas un écran séparé — mais verrouillé sur le seul camp du joueur local
(`OpenDockForMultiplayerDeployment(team)`) et limité à un budget de 4 unités de combat + 8
barricades (`maxUnitsPerTeam` forcé à 4 pour la durée du match). Chaque joueur place manuellement
sa propre escouade en parallèle de l'autre (fenêtre de 45s, voir `MatchSessionManager.
RunDeploymentPhase`), le client envoie `submit_deployment`, et **ne spawn jamais ses propres
unités à partir de son placement local** — il attend `deployment_result` (positions validées/
recadrées par le serveur pour les DEUX camps) avant de réellement les instancier, exactement comme
il le fait déjà pour `turn_result`. Voir `03-network-protocol.md` et
`08-known-issues-and-todo.md` §10.2/§10.4 pour le détail complet et la justification (l'ancien
déploiement automatique symétrique, décrit ci-dessous à titre historique, restait fonctionnel mais
ne répondait pas au besoin d'un vrai choix tactique de déploiement).

Intention initiale (dépassée, gardée pour l'historique) : en mode multijoueur, le déploiement
initial des deux équipes doit venir du serveur (positions déterministes ou négociées), pas du
drag & drop libre actuel — sinon rien n'empêche un client de se déclarer des unités
supplémentaires. Le écran de déploiement solo (`StartPlacingUnit`, `SpawnUnitAt` appelés
directement par le joueur) reste tel quel pour le mode solo uniquement. Pour le multijoueur V1, le
plus simple est un déploiement **automatique et symétrique** décidé par le serveur (miroir de
`AutoDeployBattlefield`), sans phase de placement manuel — à enrichir plus tard si vous voulez un
vrai draft de composition d'équipe.

## Ce qui ne bouge pas

- `UnitAI_Combat.cs`, `TacticalAIPlanner.cs`, la logique de ligne de vue/NavMesh : ce code
  tourne **côté serveur** en autorité, et peut continuer à tourner **côté client** tel quel pour
  le mode solo. Aucune duplication de logique de gameplay à écrire.
- L'esthétique (fog de guerre, animations, sons) reste identique — seule la source de vérité du
  *résultat* change entre solo (calculé localement) et multijoueur (calculé serveur, rejoué
  localement).
