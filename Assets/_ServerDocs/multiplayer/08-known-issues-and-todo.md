# État des lieux, problèmes rencontrés et travail restant

Dernière mise à jour : 2026-08-23. Ce document liste **tout** ce qui a été fait, tout ce qui
bloque, et tout ce qu'il reste à faire pour arriver au test à 2 téléphones. À lire avant de
reprendre le travail sur ce projet, dans n'importe quelle session future.

---

## 1. Résumé — ce qui est fait vs ce qui reste

| Composant | État |
|---|---|
| VPS (sécurité, Docker, UFW, fail2ban) | ✅ Fait, live sur `novgov.com` |
| Supabase (Postgres + GoTrue + PostgREST) | ✅ Fait, testé de bout en bout (inscription réelle → JWT → profil auto-créé) |
| Nginx + TLS (Let's Encrypt) | ✅ Fait, `https://novgov.com` valide |
| Code C# serveur (`Assets/Scripts/Server/`) | ✅ Écrit, jamais testé en exécution réelle (le build ne compile pas encore, voir §2) |
| Code C# client (`Assets/Scripts/Network/`, `Assets/Scripts/Auth/`) | ✅ Écrit, jamais testé en exécution réelle |
| Modifications du code existant (isGhosted, guards UNITY_SERVER, fix Handheld, fix shaders null) | ✅ Faites, voir §4 pour le détail exact des fichiers touchés |
| **Build Linux headless du serveur de jeu** | ❌ **BLOQUÉ** — voir §2, c'est le prochain obstacle à lever |
| Déploiement du conteneur `game-server` sur le VPS | ⏳ En attente du point ci-dessus |
| Intégration réseau dans un build client (Android) réel | ⏳ Pas commencé (le code existe mais n'a jamais été buildé/testé sur un téléphone) |
| Test à 2 téléphones (doc 07) | ⏳ Pas commencé |

---

## 2. Problème bloquant : le build Linux headless ne compile pas en ligne de commande

### Symptôme

`Unity.exe -batchmode -nographics -quit -executeMethod ServerBuildScript.BuildLinuxServer` échoue
avec des erreurs **CS0121 "The call is ambiguous between the following methods or properties"**
où les deux signatures affichées sont **strictement identiques** (ex: `TMPro.TMPro_ExtensionMethods.Compare(UnityEngine.Color32, UnityEngine.Color32)` "ambiguous with"
lui-même). Les fichiers en cause sont tous dans `Library/PackageCache/` — des packages Unity
(`com.unity.inputsystem`, `com.unity.ugui` (contient TextMeshPro), `com.unity.collections`) — **pas
notre code**. Ce type d'erreur signifie qu'une même définition se retrouve chargée deux fois dans
la même compilation (deux copies de la même assembly).

### Ce qui a été essayé, dans l'ordre, sans succès stable

1. **Build initial** : échec sur `UnitSpawnerUI.cs:238` — `Handheld.Vibrate()` appelé sans garde
   de compilation (`#if UNITY_ANDROID || UNITY_IOS`), invalide sur le target Dedicated Server.
   → **Corrigé** (voir §4). Le build suivant a réussi à 100% (compilation + assets), le CS0121
   n'existait pas encore à ce stade.
2. Le build réussi a ensuite planté **à l'exécution** (pas à la compilation) : `TacticalVisibility.CreateCutawayMaterial()` et potentiellement `SafeMaterialFactory` faisaient
   `new Material(shader)` avec un shader `null`, parce qu'Unity retire tous les shaders des
   builds Dedicated Server par défaut ("Enable Dedicated Server optimizations"). 375 bâtiments sur
   396 ne se généraient pas. → **Corrigé** (voir §4, `SafeMaterialFactory.cs`,
   `TacticalVisibility.cs`, `UnitTacticalMarker.cs` rendus tolérants à un shader manquant).
3. Tentative de **désactiver** "Dedicated Server optimizations" par réflexion sur
   `PlayerSettings` (pas d'API publique pour ce champ) pour garder les shaders plutôt que de
   patcher le code. **C'est à ce moment précis que les erreurs CS0121 sont apparues pour la
   première fois.** La modification elle-même n'a jamais été persistée sur disque (vérifié : `ProjectSettings/ProjectSettings.asset` n'a jamais changé selon `git diff`), et le code a été
   entièrement retiré du script — sans que cela résolve le problème par la suite.
4. Nettoyage du cache de compilation du **projet** (`Library/Bee`, `Library/ScriptAssemblies`,
   `Library/BuildPlayerData`, `Library/PlayerDataCache`) → échec identique.
5. Nettoyage de `Library/PackageCache` (force la ré-extraction des packages) → échec identique.
6. Suppression complète de `Library/` (réimport à froid total du projet, ~62 000 fichiers) →
   échec identique (parfois pire : plus d'erreurs qu'avant).
7. Nettoyage du cache **global** Unity partagé entre projets
   (`%LOCALAPPDATA%\Unity\Caches\bee`) → échec identique.
8. Retrait du package `com.unity.multiplayer.center` (assistant Editor de configuration
   multijoueur, aucun code runtime) → **a réduit** les erreurs (InputSystem + Collections ont
   disparu), mais **pas éliminé** celles de TextMeshPro/ugui.
9. Ajout explicite de `-buildTarget Linux64` sur la ligne de commande (pour éviter un switch de
   plateforme en cours de session) → pas d'amélioration, parfois pire.
10. Retrait de `EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server`
    (abandon du sous-cible "Dedicated Server", build Standalone classique) → les erreurs
    InputSystem/Collections ont **disparu à nouveau**, mais TextMeshPro/ugui **persiste**.
11. Résolution explicite de version pour `com.unity.collections` dans `manifest.json` (un indice
    fort avait été repéré : `packages-lock.json` résolvait `com.unity.collections` en version
    `6.5.0` "builtin" — un numéro qui ne correspond à aucune vraie release du package, mais au
    numéro de version de l'Editor lui-même — alors que d'autres dépendances demandaient
    explicitement `2.6.8`/`2.4.3` en registry) → **sans effet**, Unity force la résolution
    "builtin" quoi qu'on demande dans le manifest.
12. Retrait des packages toolchain Linux (`com.unity.sdk.linux-arm64`,
    `com.unity.sdk.linux-x86_64`, `com.unity.toolchain.win-x86_64-linux`, ajoutés automatiquement
    par Unity dès qu'on cible Linux64, inutiles ici car le scripting backend Standalone est Mono,
    pas IL2CPP) → Unity les **réinstalle automatiquement** dès qu'un build Linux64 est demandé,
    impossible à empêcher ; pas d'effet sur le CS0121 de toute façon.
13. **Retour à l'état exact d'avant toute modification**, via `git checkout -- Packages/manifest.json Packages/packages-lock.json` (le dépôt est git, `Packages/` était le seul
    dossier modifié — `ProjectSettings/` n'a jamais bougé) combiné à un `Library/` déjà repartis
    à zéro → **échec identique**, alors que ce même état de fichiers avait produit un build
    parfaitement réussi plus tôt dans la session. Ceci élimine toute cause au niveau des fichiers
    du projet (trackés par git) : le problème est forcément dans un état **local à cette machine**
    (installation Unity Editor, cache utilisateur, licence...) qui a changé de façon permanente
    et n'est plus reproductible à l'identique même en remettant les fichiers du projet dans leur
    état d'origine.

### Hypothèse actuelle (non confirmée)

Quelque chose s'est cassé de façon persistante dans l'installation Unity Editor de cette machine
(`C:\Program Files\Unity\Hub\Editor\6000.5.8f1`) ou dans un cache utilisateur non identifié,
**après** l'installation du module "Linux Dedicated Server Support" et les premiers essais de
build Linux64. Le tout premier build (avant l'installation de ce module ou juste après, avant
tout essai de bascule de plateforme) a réussi proprement. Tous les essais après une bascule de
plateforme active vers Linux64 échouent, de façon déterministe, quel que soit l'état des fichiers
du projet.

### Prochaines pistes à essayer (non tentées faute de temps)

- **Builder depuis l'Editor Unity en mode interactif** (`File > Build Settings > Dedicated
  Server` ou `Standalone`, bouton "Build"), plutôt qu'en ligne de commande `-batchmode`. C'est la
  piste la plus prometteuse : le mode interactif peut emprunter un chemin de compilation
  différent du mode batch. **C'est la piste recommandée pour reprendre ce chantier.**
- Réinstaller entièrement Unity 6000.5.8f1 (désinstallation complète + réinstallation propre du
  module Linux Server) pour repartir d'une installation Editor garantie saine.
- Essayer une version différente d'Unity (ex: 6000.5.9f1 ou une LTS) sur laquelle ce bug
  spécifique n'existe peut-être pas.
- Ouvrir un ticket sur le tracker Unity si le problème se reproduit sur une install neuve —
  aucun ticket existant trouvé lors des recherches effectuées (voir historique de la conversation
  pour les termes de recherche déjà essayés, pour ne pas dupliquer la recherche).

### Fichiers concernés par ce chantier
- `Assets/Editor/ServerBuildScript.cs` — script de build, actuellement configuré en Standalone
  classique (pas de sous-cible Server), avec le détail de cette investigation en commentaire.
- `Packages/manifest.json` — actuellement identique à la version git d'origine (avec
  `com.unity.multiplayer.center`, sans les packages toolchain Linux qu'Unity réinstalle de toute
  façon automatiquement dès qu'on cible Linux64).

---

## 3. Une fois le build Linux obtenu — ce qu'il reste à faire

1. **Copier le build** dans `Assets/_ServerDocs/multiplayer/game-server/` : le fichier exécutable
   (`StreetActServer.x86_64`), le dossier `StreetActServer_Data/`, et **`UnityPlayer.so`** (facile
   à oublier — il est à côté de l'exécutable, pas dedans `_Data/`).
2. Transférer sur le VPS (`scp` + `tar`, voir historique de session pour la commande exacte —
   env. 40 Mo compressés) dans `/opt/streetact/game-server/`.
3. `docker compose build game-server && docker compose up -d game-server` sur le VPS.
4. **Vérifier les logs** (`docker compose logs -f game-server`) : le serveur doit charger la
   carte par défaut, générer les ~396 bâtiments (pas seulement 21 — sinon le fix shader n'a pas
   pris), et afficher `[GameServerBootstrap] Serveur de jeu StreetAct à l'écoute sur le port 7777.`
5. **Test de fumée sans téléphone** : ouvrir une connexion TCP brute vers `novgov.com:7777`,
   envoyer un message `auth` avec un JWT valide (récupérable via un vrai login sur
   `/auth/v1/token`), vérifier qu'il n'y a pas de crash serveur. Ceci teste `GameServerBootstrap`
   et `JwtValidator` sans avoir besoin du client mobile.
6. **Builder le client Unity pour Android** avec les nouveaux scripts réseau. Points d'attention :
   - Vérifier que `SupabaseAuthClient.BaseUrl`/`AnonKey` et `GameServerClient.ServerHost`/`ServerPort` (dans `Assets/Scripts/Auth/SupabaseAuthClient.cs` et
     `Assets/Scripts/Network/GameServerClient.cs`) pointent bien vers `novgov.com` (déjà le cas
     par défaut, à re-vérifier si ces valeurs sont modifiées).
   - Le bouton "⚔️ MULTIJOUEUR" ajouté dans `GameManagerUI.cs` doit apparaître sur l'écran de
     démarrage.
7. **Test à 2 téléphones** en suivant `07-test-plan-2-phones.md` (login, matchmaking, tour normal,
   scénario Ghost, fin de partie).

---

## 4. Détail exact des modifications faites au code existant

Toutes additives/minimales, aucune ne devrait affecter le mode solo. Liste complète pour audit :

| Fichier | Changement | Pourquoi |
|---|---|---|
| `Assets/UnitAI.cs` | Ajout `isGhosted`, `IsShootingNow`, `SetNetworkHealth()`, `ApplyNetworkDeath()`, `SetNetworkAnimState()` | Support Ghost + lecture des snapshots réseau côté client (voir doc 04) |
| `Assets/Scripts/AI/TacticalAIPlanner.cs` | Garde assoupli : `if (unit.isPlayerControlled && !unit.isGhosted) return;` (au lieu de bloquer tout `isPlayerControlled`) | Permet au Ghost de planifier les unités d'un joueur absent |
| `Assets/GameManagerUI.cs` | Garde `#if UNITY_SERVER` sur `OnGUI()`, ajout du bouton "⚔️ MULTIJOUEUR" | Pas d'IMGUI côté serveur ; point d'entrée du mode multijoueur |
| `Assets/UnitSpawnerUI.cs` | Garde `#if UNITY_SERVER` sur `OnGUI()` ; fix `Handheld.Vibrate()` non gardé (bug préexistant, cassait tout build Dedicated Server) | Idem + bug de compilation réel trouvé en cours de route |
| `Assets/TacticalPathManager.cs` | Garde `#if UNITY_SERVER` sur `Update()` et `OnGUI()` ; branche multijoueur dans `LancerExecutionTour()` (délègue à `MultiplayerMatchController.SubmitLocalTurn()` si actif) | Idem + aiguillage solo/multijoueur |
| `Assets/SafeMaterialFactory.cs` | `CreateUnlit`/`CreateLit` retournent `null` au lieu de planter si aucun shader n'est disponible | Build Dedicated Server avec shaders retirés — voir §2 point 2 |
| `Assets/TacticalVisibility.cs` | `CreateCutawayMaterial()` retourne tôt si le shader est `null` | Idem |
| `Assets/UnitTacticalMarker.cs` | Garde `mat == null` après `SafeMaterialFactory.CreateUnlit()` | Idem |

Fichiers entièrement nouveaux (non listés ici en détail, voir leur en-tête de commentaire) :
`Assets/Scripts/Network/NetMessage.cs`, `GameServerClient.cs`, `MultiplayerMatchController.cs`,
`Assets/Scripts/Auth/SupabaseAuthClient.cs`, `Assets/Scripts/Server/GameServerBootstrap.cs`,
`PlayerConnection.cs`, `JwtValidator.cs`, `MatchSessionManager.cs`, `Assets/Editor/ServerBuildScript.cs`.

---

## 5. Limitations connues, assumées pour la V1 (pas des bugs, des choix de scope)

- **Un seul match concurrent** à la fois (voir README, "Portée V1"). Scaling documenté dans
  04-unity-headless-server.md mais pas implémenté.
- **Pas de reprise de match après coupure TCP totale** — un joueur qui se déconnecte puis se
  reconnecte rejoint la file d'attente pour un NOUVEAU match ; la partie en cours continue avec
  son camp en mode Ghost jusqu'à la fin (voir commentaire en tête de `MatchSessionManager.cs`).
- **Persistance DB minimale** : seules les tables `matches`/`match_participants` sont écrites par
  le serveur (via PostgREST, rôle `service_role`). Le log détaillé par tour
  (`match_turn_orders`/`match_event_log` du schéma SQL) n'est **pas** encore branché — pas
  bloquant pour tester, mais pas d'historique de partie détaillé pour l'instant.
- **Port de jeu (7777) non chiffré** — TLS seulement sur 80/443 (auth/rest). Acceptable pour un
  test à 2 téléphones (le JWT y transite déjà émis via HTTPS), à corriger avant toute diffusion
  plus large (voir `06-security-checklist.md`).
- **Déploiement multijoueur limité à la carte par défaut hors-ligne** (pas de génération GPS
  procédurale) — nécessaire pour que client et serveur génèrent une géométrie/NavMesh identiques,
  voir `04-unity-headless-server.md`.
- **Rejeu par snapshots**, pas par log d'événements discret — voir `03-network-protocol.md`,
  section "Mapping avec le code existant", pour la justification de ce choix.
- `TacticalCamera`/`CameraStateManager` et d'autres scripts de la scène n'ont **pas été audités**
  un par un pour une éventuelle dépendance non gardée à l'Input System ou à la caméra en
  environnement headless réel — seuls les scripts identifiés comme problématiques pendant cette
  session (`GameManagerUI`, `UnitSpawnerUI`, `TacticalPathManager`, `SafeMaterialFactory`,
  `TacticalVisibility`, `UnitTacticalMarker`) ont été corrigés. **À surveiller au premier vrai
  test d'exécution du serveur.**

---

## 6. Sécurité — action restante

Le mot de passe VPS initial (`5JnJtq7KWEYF-1`) a transité en clair dans la conversation avant la
mise en place de l'auth par clé. Voir `CREDENTIALS.md` (fichier local, gitignored, jamais
committé) pour le détail et la marche à suivre.
