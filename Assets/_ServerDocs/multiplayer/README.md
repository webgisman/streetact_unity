# Novgov Multijoueur — Documentation de déploiement

Ce dossier contient toute la documentation et les fichiers de configuration nécessaires pour
transformer Novgov (un jeu solo Joueur vs IA, **toujours disponible tel quel** — le multijoueur
est un mode additionnel, pas un remplacement) en un jeu multijoueur PvP hébergé sur un VPS Hetzner
via Docker.

**⚠️ LIRE [00-current-architecture-2026-09-13.md](00-current-architecture-2026-09-13.md) EN
PREMIER.** Ce document décrit l'état RÉEL du calcul de combat aujourd'hui (vrai moteur Unity pour
tous les modes, deux rythmes de jeu 5 min/6h, notifications) — il fait foi en cas de contradiction
avec le résumé "Décisions d'architecture" plus bas dans cette page ou avec le document 04, tous les
deux écrits pour l'architecture précédente (résolveur pur/déterministe, remplacée le 2026-09-13).

**État (2026-09-13) : bascule complète vers le vrai moteur Unity + déploiement réel sur
novgov.com**, à la demande explicite de l'utilisateur. Voir
[12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) pour ce qui a été
vérifié en production (et ce qui ne l'a pas été — le gameplay réel à 2 joueurs, toujours pas testé).

<details>
<summary>État précédent (2026-09-08), avant la bascule du 2026-09-13 — historique, plus à jour</summary>

Audit complet de jouabilité §19 rebuild et redéployé sur le VPS, confirmé en direct. Serveur Linux
reconstruit (fraîcheur vérifiée par réflexion .NET, pas seulement l'horodatage), sauvegarde de
l'ancienne image prise avant toute modification (`novgov-game-server:backup_20260908_204728`),
copié sur `novgov.com`, conteneur reconstruit et redémarré, vérifié stable (aucune exception/
crash-loop) et joignable en TCP depuis l'extérieur. Voir
[08-known-issues-and-todo.md](08-known-issues-and-todo.md) section **19** (§19.1 à §19.13).

</details>

## Outillage — lancer les tests sans ouvrir l'Éditeur

Le moteur pur (`Assets/Scripts/TacticalCore/`) a un harnais de tests versionné hors de `Assets/`
(donc invisible pour Unity), qui compile et exécute ses 74 tests avec le SDK .NET livré avec
Unity — pas besoin d'installation séparée, ni d'ouvrir l'Éditeur :

```
.\Tools\run-tests.ps1              # tests + compile-check des 3 configurations (client/serveur/éditeur)
.\Tools\run-tests.ps1 -TestsOnly   # tests seuls (~5 s)
```

À lancer avant tout commit qui touche `Assets/Scripts/TacticalCore/`, `Server/`, `Network/` ou
`AI/`. Voir `Assets/_ServerDocs/multiplayer/08-known-issues-and-todo.md` §19.5 pour pourquoi ce
harnais existe et pourquoi il est désormais versionné (il avait été reconstruit de zéro à trois
sessions différentes après avoir vécu dans un dossier temporaire).

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
11. [08-known-issues-and-todo.md](08-known-issues-and-todo.md) — historique détaillé 2026-08/début
    09, y compris la construction de l'architecture "résolveur pur" remplacée le 2026-09-13.
12. [00-current-architecture-2026-09-13.md](00-current-architecture-2026-09-13.md) — **document de
    référence pour l'état actuel** du calcul de combat/déploiement/rythmes de jeu.
13. [09-real-unity-combat-investigation-2026-09-13.md](09-real-unity-combat-investigation-2026-09-13.md), [10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md](10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md), [11-real-engine-async-pace-notifications-2026-09-13.md](11-real-engine-async-pace-notifications-2026-09-13.md), [12-production-deployment-2026-09-13.md](12-production-deployment-2026-09-13.md) — la bascule du 2026-09-13, dans l'ordre chronologique.

## Identifiants

Tous les identifiants (SSH, Supabase, VPS) sont dans `CREDENTIALS.md` à la racine du projet
(`E:\streetact\My project\CREDENTIALS.md`) — **fichier local, jamais committé** (voir
`.gitignore`). Ne pas déplacer son contenu ailleurs sans le même niveau de précaution.

## Décisions d'architecture (résumé)

**Le tableau ci-dessous décrit l'architecture PRÉCÉDENTE (résolveur pur/déterministe) — remplacée le
2026-09-13, voir [00-current-architecture-2026-09-13.md](00-current-architecture-2026-09-13.md).**
Gardé tel quel pour l'historique du raisonnement (pourquoi un résolveur pur avait été choisi), mais
ne décrit plus ce qui tourne aujourd'hui. Ces choix sont expliqués en détail dans les documents
dédiés, mais voici le résumé :

| Sujet | Décision | Pourquoi |
|---|---|---|
| Transport réseau | **TCP** (pas UDP), messages JSON préfixés par une longueur | Le jeu est request/response (intentions → log rejoué), pas un tick temps réel synchronisé. TCP est plus fiable sur réseau mobile/NAT, plus simple à implémenter (pas de handshake WebSocket nécessaire puisque client ET serveur sont tous les deux du code Unity/.NET). |
| Port serveur de jeu | `7777/tcp` | Ton choix initial, conservé — juste TCP au lieu d'UDP. |
| Moteur de résolution | **Unity Headless** (pas de réécriture en Go/Node) | Le combat utilise déjà NavMesh + Physics.RaycastAll pour la ligne de vue (`UnitAI_Combat.cs`, `TacticalAIPlanner.cs`). Réimplémenter cette logique ailleurs serait un gros travail inutile. |
| Système Ghost/IA | **Réutilisation de `TacticalAIPlanner.PlanTurnForUnit()`** | C'est littéralement le planificateur déjà utilisé pour l'équipe ennemie IA. Un joueur absent = ses unités basculent temporairement en mode "planifiées par l'IA" au lieu d'attendre une intention humaine. |
| Auth | Supabase GoTrue self-hosted, appelé en direct par le client via `UnityWebRequest` | Pas besoin du SDK C# officiel (souvent en retard) — l'API REST de GoTrue est simple et stable. |
| Accès DB | **Décision finale (différente de l'intention initiale ci-contre) : le serveur Unity headless écrit via PostgREST** (`UnityWebRequest` + `SERVICE_ROLE_KEY`, contourne RLS), pas de connexion Postgres directe — voir [04-unity-headless-server.md](04-unity-headless-server.md). Le client, lui, ne parle bien JAMAIS à Postgres directement. | Éviter une dépendance Npgsql externe (parfois délicate en IL2CPP/Mono) alors que PostgREST offre déjà tout ce qu'il faut en HTTP interne au réseau Docker. |
| Portée V1 (dépassée, voir ci-dessous) | ~~Un seul match actif au démarrage~~, scaling multi-matchs documenté mais pas implémenté tout de suite | Évite de sur-ingénierer une orchestration multi-process avant d'avoir un premier match qui fonctionne. |
| **Mise à jour 2026-08-30 : scaling implémenté** | Deathmatch/Zone de Contrôle tournent désormais en **donnée pure** (`Novgov.TacticalCore` + `Assets/Scripts/Server/MatchState.cs`) — plus aucune vraie `UnitAI`/`BuildingStructure` par partie, un seul processus fait tourner des centaines/milliers de parties EN PARALLÈLE (voir [04-unity-headless-server.md](04-unity-headless-server.md)). Chaque partie peut aussi se dérouler sur la vraie tuile GPS d'un joueur (comme la Conquête), pas seulement la carte "Default". Le pool Docker est revenu à **1 seule instance** (`game-server-1`) + un pool de threads (`Task.Run`) pour `TacticalResolver.Resolve()`, le pool de 3 instances du même jour n'étant plus qu'un coût de base dupliqué sans gain de capacité. Conquête reste inchangée (1 combat à la fois, vraies `UnitAI`). | La contrainte réelle qui limitait à "un match à la fois" était le couplage à des GameObjects/une scène partagée, pas l'absence d'un service externe — voir 04 pour le détail. |

## Ce qui existe déjà dans le projet (contexte pour la suite)

- `Assets/Scripts/AI/UnitAI.cs` + `UnitAI_Combat.cs` + `UnitAI_Movement.cs` + `UnitAI_Visuals.cs` : l'unité de jeu (fantassin, char, mortier), avec `teamID`, `isPlayerControlled`, `tacticalPath` (liste de `TacticalPathManager.TacticalNode`).
- `Assets/Scripts/AI/TacticalPathManager.cs` (+ `TacticalPathManager_Input.cs`/`_Selection.cs`/`_ContextMenu.cs`/`_UI.cs`/`_PathDrawing.cs`/`_Execution.cs`, classes partielles, éclatées le 2026-08-29 — voir 08-known-issues-and-todo.md §9.1) : machine à états de tour (`GamePhase.Planification → CreationPath → Execution`), UI tactile de sélection/tracé de chemin, `LancerExecutionTour()` qui déclenche la résolution (et, depuis le 2026-08-29, la détection de victoire/défaite en solo).
- `Assets/Scripts/AI/TacticalAIPlanner.cs` : planificateur IA statique (`PlanTurnForUnit`) — réutilisé tel quel côté serveur.
- `Assets/Scripts/UI/UnitSpawnerUI.cs` : déploiement des unités (Fantassin, Char Leopard 2, Véhicule Canon, Mortier, Barricade), mode Hotseat (2 joueurs humains sur le même appareil en solo).
- `Assets/Scripts/UI/GameManagerUI.cs` : écran de démarrage (2 boutons — Solo / Campagne Multijoueur, ce dernier enchaînant GPS puis login), point d'insertion de l'écran de login.
- Unity **6000.5.8f1** (Unity 6), URP, Input System, `com.unity.ai.navigation` 2.0.14.
