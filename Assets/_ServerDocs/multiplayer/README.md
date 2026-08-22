# StreetAct Multijoueur — Documentation de déploiement

Ce dossier contient toute la documentation et les fichiers de configuration nécessaires pour
transformer StreetAct (actuellement un jeu solo Joueur vs IA) en un jeu **multijoueur PvP
asynchrone à tour par tour**, hébergé sur un VPS Hetzner via Docker.

**Ne PAS build/importer ce dossier dans le jeu.** Il contient uniquement de la doc et des
fichiers de config serveur (yml, sql, conf) — Unity les ignore au build.

## Sommaire des documents

1. [01-deployment-vps.md](01-deployment-vps.md) — Runbook pas-à-pas pour préparer le VPS Hetzner
   (Docker, UFW, Nginx, TLS). C'est le document qu'on suivra ensemble une fois que tu me donnes
   les accès SSH.
2. [`docker-compose.yml`](docker-compose.yml) + [`.env.example`](.env.example) — la stack complète
   (Postgres, GoTrue, PostgREST, serveur Unity headless, Nginx).
3. [`nginx/nginx.conf`](nginx/nginx.conf) — reverse proxy + TLS.
4. [`schema.sql`](schema.sql) — schéma Postgres (auth via Supabase + tables de match).
5. [02-auth-supabase-unity.md](02-auth-supabase-unity.md) — intégration de l'authentification
   Supabase (GoTrue) côté client Unity.
6. [03-network-protocol.md](03-network-protocol.md) — protocole réseau téléphone ↔ serveur
   (format des intentions envoyées, format du log d'événements renvoyé).
7. [04-unity-headless-server.md](04-unity-headless-server.md) — comment structurer le serveur
   Unity autoritaire (build headless Linux, boucle de match, timer + Ghost/IA).
8. [05-client-integration.md](05-client-integration.md) — ce qu'il faut modifier côté client
   (GameManagerUI, TacticalPathManager, UnitSpawnerUI) pour brancher le réseau.
9. [06-security-checklist.md](06-security-checklist.md) — UFW/Docker, secrets, RLS.
10. [07-test-plan-2-phones.md](07-test-plan-2-phones.md) — plan de test final avec 2 téléphones.
11. [08-known-issues-and-todo.md](08-known-issues-and-todo.md) — **à lire en premier en reprenant
    ce chantier** : état exact de ce qui est fait/pas fait, détail complet du blocage actuel (le
    build Linux headless ne compile pas en ligne de commande — piste de résolution recommandée
    incluse), et tout le travail restant jusqu'au test à 2 téléphones.

## Identifiants

Tous les identifiants (SSH, Supabase, VPS) sont dans `CREDENTIALS.md` à la racine du projet
(`E:\streetact\My project\CREDENTIALS.md`) — **fichier local, jamais committé** (voir
`.gitignore`). Ne pas déplacer son contenu ailleurs sans le même niveau de précaution.

## Décisions d'architecture (résumé)

Ces choix sont expliqués en détail dans les documents dédiés, mais voici le résumé :

| Sujet | Décision | Pourquoi |
|---|---|---|
| Transport réseau | **TCP** (pas UDP), messages JSON préfixés par une longueur | Le jeu est request/response (intentions → log rejoué), pas un tick temps réel synchronisé. TCP est plus fiable sur réseau mobile/NAT, plus simple à implémenter (pas de handshake WebSocket nécessaire puisque client ET serveur sont tous les deux du code Unity/.NET). |
| Port serveur de jeu | `7777/tcp` | Ton choix initial, conservé — juste TCP au lieu d'UDP. |
| Moteur de résolution | **Unity Headless** (pas de réécriture en Go/Node) | Le combat utilise déjà NavMesh + Physics.RaycastAll pour la ligne de vue (`UnitAI_Combat.cs`, `TacticalAIPlanner.cs`). Réimplémenter cette logique ailleurs serait un gros travail inutile. |
| Système Ghost/IA | **Réutilisation de `TacticalAIPlanner.PlanTurnForUnit()`** | C'est littéralement le planificateur déjà utilisé pour l'équipe ennemie IA. Un joueur absent = ses unités basculent temporairement en mode "planifiées par l'IA" au lieu d'attendre une intention humaine. |
| Auth | Supabase GoTrue self-hosted, appelé en direct par le client via `UnityWebRequest` | Pas besoin du SDK C# officiel (souvent en retard) — l'API REST de GoTrue est simple et stable. |
| Accès DB | Le serveur Unity headless se connecte à Postgres en direct (port 5432, réseau Docker interne uniquement), le client ne parle JAMAIS à Postgres directement | Le client ne fait confiance à rien ; seul le serveur écrit les résultats de combat. PostgREST sert uniquement à des lectures non critiques (historique de parties, profil) si besoin plus tard. |
| Portée V1 | **Un seul match actif au démarrage** (le temps du test à 2 téléphones), scaling multi-matchs documenté mais pas implémenté tout de suite | Évite de sur-ingénierer une orchestration multi-process avant d'avoir un premier match qui fonctionne. |

## Ce qui existe déjà dans le projet (contexte pour la suite)

- `Assets/UnitAI.cs` + `UnitAI_Combat.cs` + `UnitAI_Movement.cs` + `UnitAI_Visuals.cs` : l'unité de jeu (fantassin, char, mortier), avec `teamID`, `isPlayerControlled`, `tacticalPath` (liste de `TacticalPathManager.TacticalNode`).
- `Assets/TacticalPathManager.cs` : machine à états de tour (`GamePhase.Planification → CreationPath → Execution`), UI tactile de sélection/tracé de chemin, `LancerExecutionTour()` qui déclenche la résolution.
- `Assets/Scripts/AI/TacticalAIPlanner.cs` : planificateur IA statique (`PlanTurnForUnit`) — **c'est notre futur système Ghost**.
- `Assets/UnitSpawnerUI.cs` : déploiement des unités (Fantassin, Char Leopard 2, Véhicule Canon, Mortier, Barricade).
- `Assets/GameManagerUI.cs` : écran de démarrage (carte hors-ligne / GPS réel), futur point d'insertion de l'écran de login.
- Aucune dépendance réseau existante (`Packages/manifest.json` ne contient ni Netcode, ni Mirror, ni Photon) — tout est à construire.
- Unity **6000.5.8f1** (Unity 6), URP, Input System, `com.unity.ai.navigation` 2.0.14.
