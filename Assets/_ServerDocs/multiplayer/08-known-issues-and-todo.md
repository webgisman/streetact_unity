# État des lieux, problèmes rencontrés et travail restant

> **⚠️ ARCHITECTURE DÉPASSÉE PAR LE TRAVAIL DU 2026-08-30 — lire
> [04-unity-headless-server.md](04-unity-headless-server.md) et le README d'abord.** Ce document
> est un JOURNAL HISTORIQUE (jusqu'au 2026-08-29) — utile pour comprendre le cheminement et les
> bugs déjà résolus, mais il décrit une architecture depuis remplacée : simulation temps réel
> NavMeshAgent (remplacée par `TacticalResolver`, un calcul instantané), "un seul match à la fois"
> (remplacé par des parties concurrentes en donnée pure, voir `MatchState.cs`), pool de 3 instances
> Docker (revenu à 1 seule instance + un pool de threads), déploiement toujours sur la carte
> "Default" (Deathmatch/Zone de Contrôle supportent désormais la vraie position GPS du joueur).
> Les mentions ci-dessous de `game-server-2`/`game-server-3`, "un seul match actif", etc. sont
> des artefacts historiques, pas l'état actuel. Le reste du contenu (bugs trouvés/corrigés avant
> le 2026-08-29, détail du build headless) reste fiable en tant qu'historique.

Dernière mise à jour : **2026-09-07/08, §19** (audit complet de jouabilité multijoueur : 19
correctifs, dont la cause la plus probable du blocage "impossible de lancer/tester le multijoueur"
— voir §19.1 — et une revue adversariale du travail de la même session qui a trouvé et corrigé 9
défauts supplémentaires avant de le documenter). **Rien de §19 n'est encore rebuild ni redéployé
sur le VPS, aucune partie à 2 joueurs n'a été rejouée depuis** — c'est le point de reprise. Les
lignes qui suivent (§9/§10, 2026-08-29) restent l'historique du tout premier build/test réel ; ce
document liste **tout** ce qui a été fait, tout ce qui bloque, et tout ce qu'il reste à faire. À
lire en partant de la fin (numéro de section le plus élevé) en reprenant le travail sur ce projet,
dans n'importe quelle session future.

---

## 0. Addendum 2026-09-06 — premier vrai test Deathmatch/Zone de Contrôle à 2 comptes réels

Testé en local via Unity Multiplayer Play Mode (2 comptes de test distincts, `testlille1`/
`testlille2`), pas encore à 2 téléphones physiques — mais la PREMIÈRE fois que ces deux modes sont
réellement joués de bout en bout par 2 joueurs vivants plutôt que vérifiés par lecture de code
(voir §14, "jamais vérifié en conditions réelles"). Aucun bouton client n'y menait avant cette
session (ajouté : `MultiplayerMatchController.StartDeathmatch/StartZoneControl`,
`ModeSelectScreen.uxml`). Bugs trouvés et corrigés en conséquence :

- **Déconnexion pendant le chargement de carte** : le serveur pouvait rester jusqu'à 60s sans
  envoyer le moindre message pendant l'attente `deployment_ready`, alors que le client coupait après
  15s sans réception — signal de vie serveur→client ajouté (`MatchSessionManager_Deployment.cs`),
  timeout client relevé en conséquence (`GameServerClient.cs`).
- **Unités auto-déployées sans placement manuel** : le compte à rebours de déploiement démarrait
  pour les deux joueurs dès que 60s s'écoulaient, MÊME pour celui dont la carte n'était pas encore
  prête — son délai s'écoulait donc en partie dans le vide. Chaque joueur a maintenant son propre
  décompte, démarré à SA PROPRE readiness.
- **Unités qui ne démarrent pas ensemble** : `TacticalResolver` gaspillait le reste du budget de
  déplacement d'un tick dès qu'un point de cheminement A* (souvent à moins d'1m) était atteint —
  reporté sur le(s) point(s) suivant(s) dans le même tick désormais.
- **Char pouvant foncer dans un bâtiment** : `TacticalResolver`/`TacticalUnit` n'avaient aucune
  notion de type de véhicule ; un char est désormais redirigé vers le point praticable le plus
  proche hors de tout bâtiment visé.
- **Mortier indisponible pour un camp si l'autre en a un** : le contrôle de caserne
  (`UnitSpawnerUI.StartPlacingUnit`) était câblé en dur sur l'équipe 1 uniquement — corrigé pour
  s'appliquer symétriquement aux deux camps.
- **Unités affichées ailleurs qu'à leur position réelle** : `UnitSpawnerUI.SpawnUnitAt` recalculait
  systématiquement (anti-triche "posé sur un bâtiment") même une position DÉJÀ authentifiée par le
  serveur (résultat de déploiement, apparition d'un ennemi repéré) — ce recalage est maintenant sauté
  pour ces rejeux serveur.
- Session de connexion et tuile domicile (PlayerPrefs) étaient partagées via le Registre Windows
  entre l'Éditeur principal et ses clones Multiplayer Play Mode — chaque identité a maintenant sa
  propre case (`Novgov.Core.EditorPlayerPrefsScope`), pertinent uniquement pour ce type de test
  local, sans effet en production (2 vrais appareils n'ont jamais ce problème).
- `MatchSessionManager.cs` (3384 lignes) découpé en 7 fichiers `partial class` par domaine
  (Matchmaking/Deployment/CombatPure/CombatLive/Conquest/Persistence), même convention que
  `TacticalPathManager_*.cs` — aucun changement de comportement.

Non revérifié depuis cet addendum : un vrai test à 2 téléphones physiques (toujours en attente,
voir §10/§14 plus bas).

---

## 1. Résumé — ce qui est fait vs ce qui reste

| Composant | État |
|---|---|
| VPS (sécurité, Docker, UFW, fail2ban) | ✅ Fait, live sur `novgov.com` |
| Supabase (Postgres + GoTrue + PostgREST) | ✅ Fait, testé de bout en bout (inscription réelle → JWT → profil auto-créé) |
| Nginx + TLS (Let's Encrypt) | ✅ Fait, `https://novgov.com` valide |
| Code C# serveur (`Assets/Scripts/Server/`) | ✅ Écrit, **exécuté réellement en prod** — voir §9 (logs vérifiés : matchmaking, `MatchSessionManager`, port 7777 confirmé joignable depuis l'extérieur) |
| Code C# client (`Assets/Scripts/Network/`, `Assets/Scripts/Auth/`) | ✅ Écrit, pas encore testé depuis un vrai build Android (le serveur seul a été vérifié, voir §9) |
| Modifications du code existant (isGhosted, guards UNITY_SERVER, fix Handheld, fix shaders null) | ✅ Faites, voir §4 pour le détail exact des fichiers touchés |
| **Build Linux headless du serveur de jeu** | ✅ **RÉSOLU le 2026-08-29** — voir §9, §2 est désormais un historique (la vraie cause n'était pas celle soupçonnée) |
| Déploiement du conteneur `game-server` sur le VPS | ✅ **Fait et vérifié en direct** — voir §9 |
| Intégration réseau dans un build client (Android) réel | ✅ **Buildé et testé en conditions réelles** (voir §10) — mais un NOUVEAU build est requis avant de retester, le code a changé depuis (correctifs §10) |
| Test à 2 téléphones (doc 07) | 🟡 **Fait une première fois le 2026-08-29** (voir §10) — 4 bugs trouvés (boutons, déploiement, déconnexion, désync combat), tous corrigés dans le code, mais **aucun correctif n'a encore été rebuild/redéployé ni reconfirmé par un nouveau test** |

---

## 2. Ancien problème bloquant (historique — voir §9 pour la résolution réelle)

**Mise à jour du 2026-08-29 : cette section est conservée pour l'historique de l'investigation,
mais sa conclusion s'est révélée fausse.** Le CS0121 décrit ci-dessous ne s'est **pas reproduit**
lors d'un nouvel essai de build le 2026-08-29 — il semble avoir été un état transitoire propre à
cette machine à ce moment-là (cache, licence ou état d'Éditeur non identifié), exactement comme
l'hypothèse du point ci-dessous le suspectait déjà. Le vrai obstacle rencontré le 2026-08-29 était
d'une toute autre nature (incohérences `#if UNITY_SERVER` dans le code, puis un module d'Éditeur
manquant) — voir §9 pour le détail complet et la résolution.

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

**Étapes 1 à 4 ci-dessous : faites et vérifiées le 2026-08-29, voir §9 pour le détail exact des
commandes et des logs obtenus.**

1. ~~**Copier le build** dans `Assets/_ServerDocs/multiplayer/game-server/`~~ : le fichier exécutable
   (`NovgovServer.x86_64`), le dossier `NovgovServer_Data/`, et **`UnityPlayer.so`** (facile
   à oublier — il est à côté de l'exécutable, pas dedans `_Data/`).
2. ~~Transférer sur le VPS~~ (`scp`, voir §9 pour la commande exacte utilisée) dans
   `/opt/novgov/game-server/`.
3. ~~`docker compose build game-server-1 && docker compose up -d game-server-1 game-server-2 game-server-3` sur le VPS.~~
4. ~~**Vérifier les logs**~~ (`docker compose logs -f game-server`) : confirmé — le serveur charge
   la carte par défaut, génère 758 bâtiments, et affiche
   `[GameServerBootstrap] Serveur de jeu Novgov à l'écoute sur le port 7777.` +
   `[MatchSessionManager] Carte par défaut (hors-ligne) chargée pour le serveur.`
5. **Test de fumée sans téléphone** : ouvrir une connexion TCP brute vers `novgov.com:7777`,
   envoyer un message `auth` avec un JWT valide (récupérable via un vrai login sur
   `/auth/v1/token`), vérifier qu'il n'y a pas de crash serveur. Ceci teste `GameServerBootstrap`
   et `JwtValidator` sans avoir besoin du client mobile. **Fait uniquement au niveau TCP brut
   (port ouvert, accepte une connexion) le 2026-08-29 — le handshake applicatif avec un vrai JWT
   n'a pas encore été testé.**
6. **Builder le client Unity pour Android** avec les nouveaux scripts réseau. Points d'attention :
   - Vérifier que `SupabaseAuthClient.BaseUrl`/`AnonKey` et `GameServerClient.ServerHost`/`ServerPort` (dans `Assets/Scripts/Auth/SupabaseAuthClient.cs` et
     `Assets/Scripts/Network/GameServerClient.cs`) pointent bien vers `novgov.com` (déjà le cas
     par défaut, à re-vérifier si ces valeurs sont modifiées).
   - Le bouton "⚔️ MULTIJOUEUR" ajouté dans `GameManagerUI.cs` doit apparaître sur l'écran de
     démarrage.
7. ~~**Test à 2 téléphones**~~ **Fait une première fois le 2026-08-29 — voir §10** : 4 bugs
   trouvés et corrigés dans le code (boutons, déploiement manuel ajouté, déconnexion, désync
   combat). Il faut maintenant refaire les étapes 5-6 (nouveau build client Android + redéploiement
   du `game-server` sur le VPS avec les correctifs) avant de reboucler sur `07-test-plan-2-phones.md`.

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

---

## 7. Audit de préparation production (2026-08-23)

Audit complet (sécurité, robustesse serveur, config de build, performance client, logique de
gameplay) fait le 2026-08-23. Les points critiques (triche sur le rating via PostgREST, déni de
service pré-authentification sur le port 7777, blocage définitif du serveur sur un message
malformé, Deathmatch sans plafond de tours, `applicationId` encore celui du template Unity,
absence de keystore de signature) ont déjà été corrigés — voir `06-security-checklist.md` section
"Audit de production (2026-08-23)" pour le détail des correctifs serveur/DB, et le
`.gitignore`/`CREDENTIALS.md` pour le keystore Android.

Restant à faire avant un build de diffusion large (pas bloquant pour continuer les tests) :

### Build Android
- [ ] **Development Build actif dans le profil de build sauvegardé**
      (`Assets/Settings/Build Profiles/Android™.asset` : `m_Development: 1`,
      `m_AllowDebugging: 1`) — désactiver avant de builder la version à diffuser (build plus
      lent/plus gros/débogable, refusé tel quel par la review du Play Store).
- [ ] **Le profil génère un APK, pas un AAB** (`m_BuildAppBundle: 0`) — le Play Store exige un
      `.aab` pour toute nouvelle fiche/mise à jour.
- [ ] **Minification Android désactivée** (`AndroidMinifyRelease: 0`) — pas de réduction
      R8/ProGuard sur le build release, taille d'AAB inutilement plus grosse.
- [ ] **`AndroidTargetSdkVersion: 0` (Automatic)** — vérifier/fixer explicitement une version qui
      respecte l'exigence de target API glissante du Play Store au moment du build (elle change
      chaque année).
- [ ] `managedStrippingLevel` non fixé explicitement pour IL2CPP — vérifier que la valeur par
      défaut effective est suffisante.
- [ ] Cosmétique, à nettoyer avant publication : `companyName: DefaultCompany`,
      `projectName`/champs Metro encore à "My project" dans `ProjectSettings/ProjectSettings.asset`
      (les champs Metro/UWP ne sont pas utilisés par ce projet, sans urgence).
- [ ] Seule `TacticalCamera`/`CameraStateManager` et quelques scripts de scène n'ont pas été
      audités un par un pour une éventuelle dépendance non gardée à l'Input System hors du
      chemin déjà corrigé cette session (menu de démarrage, dock de déploiement, menus
      contextuels tactiques) — à surveiller si un autre écran/interaction ne répond pas au
      tactile sur appareil réel.

### Performance client (mobile)
- [ ] **Chargement de carte bloquant le thread principal** (`MapTileLoader.cs`) — allocation et
      copie de pixels sans `yield` sur la branche "déjà en cache", risque de gel (ANR Android)
      proportionnel au rayon/zoom de la carte.
- [ ] **Effets de combat sans pooling** (`UnitAI_Visuals.cs` : traceurs, sang, impacts, fumée) —
      `Instantiate`/`Destroy` à chaque tir/impact, à remplacer par un pool d'objets réutilisables
      si les accrochages à beaucoup d'unités deviennent un point chaud de performance.
- [ ] **`Camera.main` non mis en cache**, appelé chaque frame par unité/instance dans
      `UnitAI_Combat.cs` et `RoadBarrier.cs` — à mettre en cache une fois dans `Awake()`/`Start()`.
- [ ] **`Physics.RaycastAll` alloue à chaque scan de ligne de vue** (`UnitAI_Combat.cs`,
      ~4×/seconde par unité) — envisager `Physics.RaycastNonAlloc` avec un buffer réutilisable si
      le nombre d'unités simultanées augmente.
- [ ] **`TacticalRadarUI.cs` reste en IMGUI** (`OnGUI()`), seul écran non converti en UI Toolkit
      (décision assumée cette session — c'est un widget d'affichage non interactif, aucun
      conflit avec le reste de l'UI Toolkit) ; alloue un `GUIStyle` par repaint pendant qu'il est
      affiché — coût mineur mais facile à éliminer si converti un jour.
- [ ] Quelques `GetComponent`/`FindObjectsByType` par frame au lieu d'être mis en cache
      (`TacticalPathManager.cs`, `RoadBarrier.cs`) — `UnitAI.AllLivingUnits` existe déjà comme
      cache et n'est pas toujours réutilisé partout où un scan de toutes les unités serait
      possible.

### Gameplay
- [ ] Le rating ELO calculé peut théoriquement descendre sous 0 pour un joueur à rating très bas
      qui perd contre un adversaire beaucoup mieux classé (`MatchSessionManager.cs`, pas de
      `Mathf.Max(0, ...)` sur le résultat) — cosmétique/affichage, pas un bug de calcul.

---

## 8. Session du 2026-08-23 (soir) — build Android sur tablette + bug de sélection + UI

### Build Android : signature du keystore

- **Symptôme 1** : popup Unity "Unable to sign the Android application — No keystore passwords
  were found" à chaque tentative de build. Cause : Unity ne persiste jamais les mots de passe du
  keystore dans `ProjectSettings/ProjectSettings.asset` (par sécurité) — il faut les ressaisir dans
  **Edit > Project Settings > Player > Android > Publishing Settings** à chaque nouvelle session
  d'Editor. Mot de passe dans `CREDENTIALS.md` section 6 (fichier local, gitignored).
- **Symptôme 2** (après avoir saisi les mots de passe) : échec Gradle
  `Keystore file '...\Library\Bee\Android\Prj\IL2CPP\Gradle\launcher\novgov-release.keystore' not
  found for signing config 'release'`, y compris après un nettoyage complet de
  `Library/Bee/Android` (export Gradle entièrement neuf, 35/35 tâches exécutées). Cause réelle :
  avec **Custom Keystore** actif, Unity ne copie le fichier `.keystore` dans le projet Gradle
  exporté **que pour un build Release** — jamais pour `assembleDebug` (Development Build), même si
  la config de signature du variant Debug pointe quand même vers ce même keystore. C'est une
  limitation connue d'Unity, pas un bug du projet.
  - **Pour tester rapidement** (build de dev) : décocher **Custom Keystore** dans Publishing
    Settings — Unity signe alors avec une clé de debug auto-générée.
  - **Pour un vrai build signé** (Play Store) : décocher **Development Build** dans *File > Build
    Settings* avant de builder, pour forcer `assembleRelease`.

### Build Android : `stripEngineCode` cassait le module Physics

- **Symptôme** : sur la tablette réelle, sélectionner une unité tactile ne fonctionnait plus du
  tout (fonctionnait dans l'Editor), et le logcat affichait `Can't add component because class
  'SphereCollider' doesn't exist!` lors de la génération des props de rue
  (`StreetPropsGenerator.CreateTree`/`CreateStreetLight`).
- **Cause** : `PlayerSettings.stripEngineCode` ("Strip Engine Code", Android > Optimization) était
  actif. L'analyseur statique d'Unity n'a pas détecté d'usage direct du module Physics et l'a
  retiré du build IL2CPP — tous les `Collider` deviennent alors non fonctionnels au runtime, ce qui
  casse silencieusement tout raycast de sélection.
- **Correction** : `stripEngineCode` mis à `0` dans `ProjectSettings/ProjectSettings.asset`. Coût :
  APK légèrement plus gros, mais tous les modules moteur restent intacts.

### Bug de sélection tactile : deux causes cumulées

Le symptôme ("impossible de sélectionner une unité sur appareil réel, marche dans l'Editor") avait
en fait **deux causes distinctes**, trouvées l'une après l'autre en instrumentant le code avec des
logs temporaires (`Debug.Log` ajoutés à chaque point de sortie de `TacticalPathManager.Update()`
et dans `IsPointerOverOnGUI()`, retirés une fois le diagnostic confirmé) et en comparant avec le
logcat en direct pendant que l'utilisateur reproduisait le bug sur son téléphone (`adb logcat`) :

**Cause n°1 — piège du mode Placement.** `TacticalPathManager.Update()` retourne immédiatement
tant que `UnitSpawnerUI.IsPlacingUnit == true` — un mode "Placement" resté armé après un clic sur
un bouton du dock de déploiement (ex. "🛡️ Char Leopard 2") absorbe alors tous les taps suivants
pour déployer de nouvelles unités, sans jamais les transmettre à la sélection tant qu'on ne
l'annule pas explicitement. Correctifs (`Assets/UnitSpawnerUI.cs`) : taper directement sur une
unité déjà posée pendant que ce mode est actif l'annule désormais (au lieu de risquer de
redéployer une unité par-dessus, voir `tappedOnExistingUnit` dans `HandlePlacementPreview()`) ;
bannière "MODE PLACEMENT" rendue beaucoup plus visible (voir section UI ci-dessous).

**Cause n°2 — `IsPointerOverOnGUI()` bloquait presque tout tap, même hors mode Placement.** Cette
méthode (`UnitSpawnerUI.cs`) sert à ignorer un tap qui tombe sur un bouton d'UI Toolkit plutôt que
sur le monde 3D. Son implémentation faisait `panel.Pick(pos) != rootVisualElement` — mais
`VisualElement.panel.Pick()` fait un **test géométrique pur qui ignore `picking-mode: Ignore`** :
n'importe quel conteneur de mise en page plein écran (`flex-grow: 1`), même explicitement marqué
`Ignore` en UXML et vide de tout contenu à cet endroit précis, est quand même renvoyé par `Pick()`
dès qu'un écran `UIScreenManager` est actif — ce qui est **en permanence** le cas pendant une
partie (`TacticalBottomBar`/`DeploymentDock` toujours affichés). Résultat : la toute première
sélection après un chargement de carte fonctionnait (rien n'avait encore eu le temps d'être
"Pické"), puis **tous les taps suivants sur une zone vide de l'écran étaient silencieusement
ignorés**, y compris loin de tout bouton réel — d'où l'impression d'un bug intermittent et
inexplicable. Confirmé en ajoutant un log listant l'écran `UIScreenManager` réellement affiché au
moment du blocage (`DeploymentDock`, alors qu'aucun de ses boutons n'était visuellement sous le
doigt) et le nom/type de l'élément renvoyé par `Pick()` (`name='root', type=VisualElement,
classes=[]` — un pur conteneur de layout, pas un contrôle interactif).
**Correction** : `IsPointerOverOnGUI()` ne bloque désormais que si l'élément trouvé est
effectivement un `Button` (`picked is Button`), plus un simple conteneur plein écran.

### Refonte visuelle : menu d'action contextuel + dock de déploiement

Aucun fichier `.uss` n'existait dans tout le projet avant cette session — tout l'UI Toolkit
runtime (`UIScreenManager`) reposait uniquement sur le thème par défaut d'Unity et des styles
inline minimaux, d'où un rendu très brut ("horrible" au retour utilisateur).

- **Nouveau** `Assets/Resources/UI/ContextMenuScreen.uss` + UXML mis à jour : panneau sombre
  arrondi façon bottom-sheet (poignée en haut), boutons avec liseré coloré à gauche au lieu de
  texte multicolore peu lisible, retour visuel au tap (`:active`), et fond assombri cliquable pour
  fermer le menu en tapant à côté (`TacticalPathManager.cs` : callback `ClickEvent` sur le nouvel
  élément `backdrop`).
- **Nouveau** `Assets/Resources/UI/DeploymentDockScreen.uss` + UXML mis à jour : même traitement
  visuel pour le dock de déploiement (panneau, boutons d'unité, indicateur d'équipe active via
  `EnableInClassList`), et bannière "MODE PLACEMENT" recolorée en orange vif avec bordure pour
  qu'elle soit impossible à manquer.

### Fichiers touchés cette session
| Fichier | Changement |
|---|---|
| `ProjectSettings/ProjectSettings.asset` | `stripEngineCode: 0`, config keystore (`AndroidKeystoreName`/`AndroidKeyaliasName`) |
| `Assets/UnitSpawnerUI.cs` | Détection de tap sur unité existante pendant le mode Placement → annulation au lieu de redéploiement ; classes USS sur les boutons du dock ; état actif équipe |
| `Assets/TacticalPathManager.cs` | Backdrop cliquable pour le menu contextuel ; boutons du menu contextuel migrés vers la classe USS `context-button` |
| `Assets/Resources/UI/ContextMenuScreen.uxml` + `.uss` (nouveau) | Refonte visuelle du menu d'action contextuel |
| `Assets/Resources/UI/DeploymentDockScreen.uxml` + `.uss` (nouveau) | Refonte visuelle du dock de déploiement |

---

## 9. Session du 2026-08-29 — refonte UI/UX, barricades, menu de démarrage, **build serveur réellement résolu et déployé**

### 9.1 Lisibilité du code — éclatement de `TacticalPathManager`

`TacticalPathManager.cs` (~1750 lignes) éclaté en classes partielles, même convention que
`UnitAI`/`UnitAI_Movement`/`UnitAI_Combat`/`UnitAI_Visuals` :
- `TacticalPathManager.cs` — enums, struct `TacticalNode`, champs partagés, `AnyOrderMenuOpen`,
  `IsSoloGameOver`, `Awake`/`Start`/`Update`.
- `TacticalPathManager_Input.cs` — toute la cascade de détection de tap (unité/sol/porte/fenêtre/
  bâtiment/barricade), radius tolérant, anti-double-tir.
- `TacticalPathManager_Selection.cs` — sélection d'unité, unité blessée suivante.
- `TacticalPathManager_ContextMenu.cs` — ouverture/fermeture centralisée du menu contextuel,
  confirmation d'ordres, menus barricade.
- `TacticalPathManager_UI.cs` — barre d'escouade, câblage des boutons UI Toolkit.
- `TacticalPathManager_PathDrawing.cs` — tracé des lignes de trajectoire.
- `TacticalPathManager_Execution.cs` — lancement/coroutine/fin de tour, **et depuis cette session
  la détection de victoire/défaite solo** (voir 9.3).

### 9.2 Corrections d'interaction (sélection, menus, tir de char)

Plusieurs bugs d'enchaînement tap → sélection → menu → confirmation corrigés dans la même série :
mauvaise unité recevant les ordres, menu resté ouvert confirmant une cible obsolète, double
déclenchement d'un même tap physique (appui **et** relâchement comptaient chacun comme un tap),
faux rejets sur les chars/bâtiments (`BuildingStructure.FindBuildingAt` trop permissif, remplacé
par un test raycast+collider), et une fuite d'un même frame entre un clic de bouton UI Toolkit et
le tap 3D brut sous-jacent — corrigée via un drapeau `suppressPointerInputUntilFrame` basé sur
`Time.frameCount` (pas `Time.time` : un cooldown temporel avait été essayé et rejeté, il retardait
aussi les actions suivantes légitimes). Centralisation de la fermeture de menu dans
`FermerMenuContextuel()` pour ne plus jamais laisser une cible obsolète confirmable.

### 9.3 Condition de victoire/défaite en mode solo (n'existait pas avant)

Le mode solo tournait indéfiniment, même un camp entièrement anéanti. Ajout dans
`TacticalPathManager_Execution.cs` : effectif de chaque camp capturé en début de tour
(`team1CountAtTurnStart`/`team2CountAtTurnStart`, pour ne jamais déclarer une victoire dès le tout
premier tour juste parce qu'un camp n'a encore rien déployé), comparé à l'effectif après
résolution du tour dans `CheckSoloGameOver()`. Nouvel écran `GameOverScreen.uxml` (résultat,
détail, bouton "Rejouer" qui recharge la scène), enregistré dans `UIScreenManager`. Ce check est
explicitement sauté si une partie multijoueur est active (`MultiplayerMatchController.IsActive`) —
la fin de partie multijoueur est gérée côté serveur (`MatchSessionManager`), pas ici.

### 9.4 Barricades — mécanique corrigée après plusieurs itérations erronées

Design final (après plusieurs allers-retours, dont une version à glisser-déposer explicitement
rejetée par l'utilisateur — "il faut la même méthode que les autres unités" — et refaite en tap
comme toutes les autres unités) : **la première barricade d'une session se pose directement**
(pas de menu, si l'emplacement est faisable) ; **chaque tap suivant** calcule une trajectoire
depuis la dernière barricade posée jusqu'au nouveau point, vérifie le stock restant
(`RemainingBarricadeStock`, 8 par équipe) et la faisabilité (raycast+collider, pas le test
polygonal `BuildingStructure.FindBuildingAt`, trop permissif), puis affiche un menu de
confirmation "DÉPLOYER CETTE EXTENSION" (même système `ShowContextMenu` que les ordres tactiques
normaux, pas d'UI séparée). Bouton DÉPLOIEMENT recoloré pour être visuellement identique à FIN DE
TOUR (même classe USS, même icône). Voir `UnitSpawnerUI.cs`
(`lastPlacedBarricadeAnchor`/`pendingBarricadeExtension`/`ComputeBarricadeExtensionPositions`) et
`TacticalPathManager_ContextMenu.cs` (`ShowBarricadeExtensionMenu`).

### 9.5 Refonte visuelle : thème rouge, bandeau supprimé, icônes

- Bandeau noir opaque en haut de l'UI tactique supprimé (`TacticalBottomBarScreen.uxml` :
  `picking-mode` passé de `Position` à `Ignore`, fond retiré), bouton vue 3D agrandi et rendu
  visible (`btn-glass`, 48px).
- Bouton FIN DE TOUR ajouté en vue 3D (`ActionViewBackScreen.uxml` + `CameraStateManager.cs`) —
  auparavant inaccessible dans ce mode de caméra.
- Thème d'accent entièrement changé de doré/jaune vers rouge (`Theme.tss`, `NovgovTheme.cs` —
  seule vraie duplication tolérée, C# miroir manuel des variables CSS).
- Icône dédiée pour le véhicule canon (partageait auparavant l'icône générique char) dans la
  barre d'escouade — bucket `activeCanonVehicles` vérifié **avant** le bucket char générique
  puisque `isTank` est vrai pour les deux.
- Son ajouté sur les boutons d'option du menu contextuel.

### 9.6 Menu de démarrage réduit à 2 boutons + mode hotseat

Refonte complète de `StartupMenuScreen.uxml` : plus que **MODE SOLO** et **MODE CAMPAGNE
MULTIJOUEUR** (le bouton GPS séparé a été supprimé, fusionné dans le flux multijoueur — activer le
GPS puis télécharger la carte/les polygones fait désormais partie du parcours "Campagne
Multijoueur", voir `GameManagerUI.StartDeviceGPS(thenConnectMultiplayer:true)`). Mode Solo propose
un interrupteur **Hotseat** (`UnitSpawnerUI.HotseatMode`, static bool) : activé, l'équipe 2 devient
elle aussi `isPlayerControlled` (au lieu d'être pilotée par `TacticalAIPlanner`) pour jouer à deux
sur le même appareil, carte déjà présente. Bouton "RETOUR" ajouté sur les écrans de connexion/
inscription (`AuthScreen.uxml` + `MultiplayerMatchController.BindUI()`), qui appelle
`GameManagerUI.ReturnToStartupMenu()` (nouvelle méthode publique).

### 9.7 Le vrai blocage du build serveur, résolu — ce que le §2 ne disait pas

En relançant réellement un build headless (`Unity.exe -batchmode -nographics -quit -buildTarget
Linux64 -executeMethod ServerBuildScript.BuildLinuxServer`), le CS0121 historique du §2 **ne s'est
pas reproduit du tout**. Trois blocages bien plus mondains ont été trouvés et corrigés à la place,
un par un, en relançant le build après chaque correction :

1. **`TacticalPathManager_Execution.cs`** — `using UnityEngine.UIElements;` manquant (utilisé pour
   `.Q<Label>()`/`.Q<Button>()` dans `ShowSoloGameOver()`, ajouté avec la fonctionnalité §9.3).
2. **`UnitSpawnerUI.cs`** — `IsPointerOverOnGUI()` déclarée à l'intérieur d'un bloc
   `#if !UNITY_SERVER` mais appelée depuis du code non gardé de `TacticalCamera.cs` et
   `TacticalPathManager_Input.cs`. Corrigé en sortant la méthode du bloc, avec un garde interne
   (`#if UNITY_SERVER return false; #else ... #endif`) plutôt que de dupliquer l'appelant.
3. **`Assets/Scripts/Network/MultiplayerMatchController.cs`** — la quasi-totalité de la classe
   (login, matchmaking, HUD, tout ce qui touche `UIScreenManager`/UI Toolkit) était appelée depuis
   des méthodes non gardées (`Update`, `BeginLoginFlow`, `HandleSignIn`, `ConnectToGameServer`,
   `OnMatchFound`, `OnMatchOver`, etc.) alors que les méthodes UI (`SetUiState`,
   `RefreshHudDynamicFields`, `BindUI`...) qu'elles appellent, elles, étaient déclarées sous
   `#if !UNITY_SERVER`. Corrigé en restructurant tout le fichier : seuls les membres statiques
   réellement référencés depuis d'autres fichiers compilés inconditionnellement (`Instance`,
   `IsActive`, `IsFlowActive`, `EnsureInstance()`, `SubmitLocalTurn()` — ce dernier ne dépend
   d'aucun type UI Toolkit) restent hors garde ; tout le reste de la classe (Awake/Update/auth/
   matchmaking/HUD/BindUI) est maintenant sous un seul `#if !UNITY_SERVER` couvrant tout le corps
   de la classe. Un appel non gardé similaire dans `GameManagerUI.StartDeviceGPS()` a aussi été
   corrigé (`Novgov.Network.MultiplayerMatchController.EnsureInstance().BeginLoginFlow();` mis
   sous `#if !UNITY_SERVER`).

Une fois ces trois corrections faites, la compilation passait, mais le build échouait ensuite pour
deux raisons **de configuration Éditeur/projet, pas de code** :

4. **`ProjectSettings/GraphicsSettings.asset`** ne référençait aucun pipeline de rendu par défaut
   (`m_CustomRenderPipeline: {fileID: 0}`) alors que le Quality Level "Mobile" du projet référence
   un asset URP (`Mobile_RPAsset`) — Unity refuse de builder avec ce mélange
   ("BuildFailedException: assets in its associated Quality levels and Graphics Settings that
   belong to different render pipelines"). Corrigé en assignant ce même asset URP par défaut dans
   `GraphicsSettings.asset`.
5. **Le module "Linux Build Support" (Standalone Player) de cette installation Unity Editor est en
   réalité un fragment cassé** — vérifié en comparant le contenu de
   `Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/` : seul
   `linux64_player_development_mono` existe côté Player (et ne contient que `Data/Managed`, sans
   `LinuxPlayer`/`UnityPlayer.so` — pas un runtime jouable), alors que les 4 variantes
   `linux64_server_{development,nondevelopment}_{mono,il2cpp}` sont, elles, complètes. Autrement
   dit : **seul le module "Linux Dedicated Server Build Support" a réellement été installé sur
   cette machine, jamais celui du Standalone classique.** Or `ServerBuildScript.cs` avait
   spécifiquement été configuré (voir ancien §2, point 10) pour éviter le sous-cible Dedicated
   Server à cause du CS0121 — qui ne s'est pas reproduit cette fois. **Solution : basculer
   `ServerBuildScript.cs` sur `StandaloneBuildSubtarget.Server`**, le seul réellement installé et
   complet sur cette machine. Build immédiatement réussi (`build/LinuxServer/NovgovServer.x86_64`,
   ~118 Mo, `Build Finished, Result: Success.`).

**Conclusion pour toute session future** : ne pas se fier à l'ancien §2 pour décider quel
sous-cible utiliser — **vérifier d'abord quelles variantes existent réellement** sous
`Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/` avant de choisir Player vs
Server, plutôt que de supposer que Server est cassé sur la base d'une session précédente.

### 9.8 Déploiement réel sur le VPS, vérifié en direct

Le nouveau build a été transféré et mis en production sur `novgov.com` (54.36.100.151) :
```bash
scp -i ~/.ssh/streetact_vps NovgovServer.x86_64 UnityPlayer.so ubuntu@54.36.100.151:/opt/novgov/game-server/
scp -i ~/.ssh/streetact_vps -r NovgovServer_Data ubuntu@54.36.100.151:/opt/novgov/game-server/
ssh -i ~/.ssh/streetact_vps ubuntu@54.36.100.151 "cd /opt/novgov && docker compose build game-server-1 && docker compose up -d game-server-1 game-server-2 game-server-3"
```
Vérifié après coup : `docker compose logs game-server` montre `[GameServerBootstrap] Serveur de
jeu Novgov à l'écoute sur le port 7777.` et `[MatchSessionManager] Carte par défaut (hors-ligne)
chargée pour le serveur.`, génération de 758 bâtiments, aucune exception. `novgov.com:7777`
confirmé joignable depuis l'extérieur (`Test-NetConnection` → `TcpTestSucceeded: True`). Les 4
autres conteneurs (`db`, `auth`, `rest`, `nginx`) n'ont pas été touchés, toujours up depuis 6
jours au moment du déploiement.

### 9.9 Vérification que la simulation est bien autoritaire côté serveur (audit demandé explicitement)

Point important à ne pas re-découvrir plus tard : `MatchSessionManager.cs` ne fait *pas confiance*
au client — chaque client envoie uniquement des **ordres** (chemin voulu), le serveur les valide
(rejette NaN/Infinity/valeurs d'action hors énumération/chemins > 200 points) puis exécute
lui-même `unit.ExecuterOrdres()`, **exactement le même code** que le mode solo
(`UnitAI_Movement.cs`/`UnitAI_Combat.cs`, aucun garde `#if UNITY_SERVER` dessus) : mouvement
NavMesh, ligne de vue par raycast contre les polygones de bâtiments (`GetVisibleEnemy`/
`IsUnitSpottedByTeam`), tir depuis/vers une fenêtre ou une porte avec cône de vision, mortiers avec
dégâts de zone réels (`MortarShell.ApplySplashDamage`, `Physics.OverlapSphere`), blocage physique
du passage par les barricades (`RoadBarrier` = vrai `NavMeshObstacle` carvé), et checkpoints
d'attente/guet/embuscade/camouflage — tout est recalculé serveur, les clients ne font que rejouer
les snapshots de positions/PV reçus (`MultiplayerMatchController.PlaySnapshotsCoroutine`). Seule
nuance : le serveur ne revérifie pas que le chemin soumis est géométriquement légal (seulement les
valeurs aberrantes) — mais le mouvement réel reste contraint par le NavMesh serveur, qui respecte
la vraie géométrie, donc un client modifié ne peut pas faire traverser un mur/une barricade à une
unité même en soumettant n'importe quel point.

### 9.10 Fichiers touchés cette session (résumé)

| Fichier | Changement |
|---|---|
| `Assets/Scripts/AI/TacticalPathManager.cs` + 6 fichiers `_Xxx.cs` (nouveaux) | Éclatement en classes partielles (voir 9.1) |
| `Assets/Scripts/AI/TacticalPathManager_Execution.cs` | Victoire/défaite solo (9.3) + `using UnityEngine.UIElements;` |
| `Assets/Scripts/UI/UnitSpawnerUI.cs` | Barricades (9.4), hotseat (9.6), `IsPointerOverOnGUI()` sorti du garde `#if !UNITY_SERVER` |
| `Assets/Scripts/UI/GameManagerUI.cs` | Menu 2 boutons (9.6), `ReturnToStartupMenu()`, appel `BeginLoginFlow()` gardé |
| `Assets/Scripts/Network/MultiplayerMatchController.cs` | Restructuration complète des gardes `#if UNITY_SERVER` (9.7) + bouton retour (9.6) |
| `Assets/Editor/ServerBuildScript.cs` | Bascule vers `StandaloneBuildSubtarget.Server` (9.7) |
| `ProjectSettings/GraphicsSettings.asset` | Assignation du pipeline de rendu par défaut (9.7) |
| `Assets/UI/Theme.tss`, `Assets/Scripts/UI/NovgovTheme.cs` | Thème rouge (9.5) |
| `Assets/Resources/UI/GameOverScreen.uxml` (nouveau) | Écran de fin de partie solo (9.3) |
| `Assets/Resources/UI/StartupMenuScreen.uxml`, `AuthScreen.uxml` | Menu 2 boutons + hotseat (9.6), bouton retour |
| `Assets/Resources/UI/TacticalBottomBarScreen.uxml`, `ActionViewBackScreen.uxml`, `DeploymentDockScreen.uxml/.uss` | Refonte visuelle (9.5) |
| `Assets/Scripts/Combat/RoadBarrier.cs` | `RemoveByPlayer()` |
| `Assets/Scripts/Camera/CameraStateManager.cs` | Bouton fin de tour en vue 3D |
| `Assets/Scripts/UI/UIScreenManager.cs` | Enregistrement de l'écran `GameOver` |

### 9.11 Toujours pas fait après cette session

- ~~Test à 2 téléphones réel (doc 07)~~ **Fait juste après cette session — voir §10.**
- Handshake applicatif JWT réel sur le port 7777 (seul un test TCP brut a été fait, voir §3 point 5).
- Audit `TacticalCamera`/scripts de scène non listés en §5 pour dépendances non gardées — toujours
  pas fait, à surveiller.

---

## 10. Session du 2026-08-29 (suite) — premier vrai test à 2 téléphones, 4 bugs trouvés et corrigés

Le test à 2 téléphones (doc 07) a enfin été fait réellement par l'utilisateur. 4 bugs rapportés,
tous diagnostiqués en lisant le code (pas de repro locale possible, pas d'accès à 2 appareils dans
cette session) puis corrigés. **Aucun de ces correctifs n'a encore été redéployé sur le VPS ni
rebuildé côté Android — à faire avant de retester.**

### 10.1 Conflits de boutons (boutons déclenchant leur action plusieurs fois)
Cause : `GameManagerUI.BindStartupUI()` rebranchait `clicked +=` sur les boutons "MODE SOLO"/"MODE
CAMPAGNE MULTIJOUEUR" à CHAQUE rechargement de scène (bouton "Rejouer" en fin de partie solo ou
multijoueur), sans le garde anti-double-abonnement que toutes les autres classes UI ont déjà
(`uiBound`/`deploymentUiBound`/`tacticalUiBound`) — ces boutons persistent, eux, pour toute la
session (`UIScreenManager` est `DontDestroyOnLoad`). Un abonnement s'accumulait à chaque partie
rejouée. Idem pour `ConnectToGameServer()` (`MultiplayerMatchController`) qui réabonnait
`GameServerClient.OnMessage`/`OnDisconnected` sans jamais se désabonner. **Fix** : garde statique
`startupButtonsBound` dans `GameManagerUI.cs` ; `-=` défensif avant chaque `+=` dans
`MultiplayerMatchController.ConnectToGameServer()`.

### 10.2 "Le bouton déploiement n'existe pas" en multijoueur
Confirmé volontaire à l'origine (déploiement auto anti-triche, voir §5) — mais l'utilisateur a
demandé un vrai placement manuel. **Ajouté** : phase de déploiement dédiée (jusqu'à 45s, en
parallèle pour les deux joueurs) avant le premier tour — voir §10.4 pour le détail complet, et
`03-network-protocol.md` (nouveaux messages `submit_deployment`/`deployment_result`).

### 10.3 Déconnexion/fermeture de l'app mal gérée
Deux causes cumulées :
- Client (`GameServerClient.cs`) : aucun `OnApplicationPause`/`OnApplicationQuit` — la socket ne se
  fermait proprement que sur `OnDestroy()` (jamais en mise en arrière-plan Android/iOS). **Fix** :
  les deux callbacks ajoutés, appellent `Disconnect(...)`.
- Serveur (`GameServerBootstrap.HandleHandshake`) : `client.ReceiveTimeout` remis à `0` (infini)
  après l'auth — un client mort silencieusement (sans FIN/RST) pouvait laisser le thread de lecture
  bloqué indéfiniment, et surtout risquait de bloquer un futur `PlayerConnection.Send()` (appelé
  depuis le thread principal) si le tampon socket se remplissait — gelant tout le serveur (un seul
  match à la fois). Déjà documenté comme risque connu en §7/06-security-checklist.md, jamais corrigé
  jusqu'ici. **Fix** : `ReceiveTimeout=20000`/`SendTimeout=10000` + `SocketOptionName.KeepAlive`
  après l'auth, combiné à un vrai heartbeat client toutes les 5s (`GameServerClient.Update()`,
  auparavant décrit dans la doc mais jamais implémenté — `MatchSessionManager` lisait déjà
  `LastHeartbeat` mais rien ne l'écrivait jamais côté client) pour ne jamais couper un joueur juste
  silencieux en pleine réflexion.

### 10.4 Combat désynchronisé entre les deux téléphones (fumée/tir visible sur un seul écran)
**Cause racine, la plus subtile des 4** : `TacticalPathManager.LancerExecutionTour()` met
`phaseActuelle = GamePhase.Execution` AVANT même de vérifier si le multijoueur est actif — en
multijoueur elle délègue ensuite à `SubmitLocalTurn()` et `return`, mais `phaseActuelle` reste à
`Execution` sur CE client pendant tout l'aller-retour serveur (jusqu'à la fin de
`PlaySnapshotsCoroutine`). Or `UnitAI_Combat.Update()` (tourne SUR CHAQUE CLIENT, à chaque frame,
pour chaque unité, sans aucun garde multijoueur) traite `phaseActuelle == Execution` comme "on est
en exécution réelle, scanner + tirer" — un comportement pensé pour le solo/hotseat, où
`ExecuterOrdres()` fait réellement tourner le combat. Résultat : dès que ce client entrait dans son
attente réseau, TOUTES ses unités (des deux camps) se remettaient à scanner/tourner leur tourelle
et **tirer réellement en local** (fumée, traceur, dégâts) sur la base de positions/raycasts propres
à CE téléphone — complètement indépendant de ce que l'autre téléphone voyait au même instant,
d'où l'antique "je vois un tir et de la fumée, pas mon adversaire". Le joueur qui appuyait sur "FIN
TOUR" en premier (ou dont la fenêtre d'attente réseau était la plus longue) était celui qui voyait
cette simulation fantôme. **Fix** (`UnitAI_Combat.cs`) : tout ce bloc (scan de cible, rotation de
visée, tir, contrôle direct de l'Animator) est désormais entièrement sauté si
`MultiplayerMatchController.IsActive` est vrai — en multijoueur, l'Animator/la vie/la mort sont
exclusivement pilotés par `UnitAI.SetNetworkAnimState`/`SetNetworkHealth`/`ApplyNetworkDeath`, jamais
par une simulation locale. `IsActive` vaut toujours `false` côté serveur headless, donc la vraie
simulation continue de tourner normalement là où elle doit avoir lieu.

### 10.5 Fichiers touchés cette session (résumé)
| Fichier | Changement |
|---|---|
| `Assets/Scripts/AI/UnitAI_Combat.cs` | Bloc combat temps réel entièrement sauté si `MultiplayerMatchController.IsActive` (10.4) |
| `Assets/Scripts/UI/GameManagerUI.cs` | Garde `startupButtonsBound` anti double-abonnement (10.1) |
| `Assets/Scripts/Network/MultiplayerMatchController.cs` | `-=`/`+=` défensif (10.1) ; nouvel état `UiState.Deployment` + `IsDeploymentPhaseActive` + `SubmitLocalDeployment()`/`OnDeploymentResult()` (10.2) |
| `Assets/Scripts/Network/GameServerClient.cs` | Heartbeat toutes les 5s ; `OnApplicationPause`/`OnApplicationQuit` (10.3) |
| `Assets/Scripts/Server/GameServerBootstrap.cs` | `ReceiveTimeout`/`SendTimeout` finis + `KeepAlive` après l'auth (10.3) |
| `Assets/Scripts/Server/PlayerConnection.cs` | `PendingDeployment`/`HasSubmittedDeployment` (10.2) |
| `Assets/Scripts/Server/MatchSessionManager.cs` | `RunDeploymentPhase`/`ResolveDeployment`/`IsRosterValid`/`ClampToDeploymentZone` (10.2) |
| `Assets/Scripts/UI/UnitSpawnerUI.cs` | `SpawnUnitAt(..., forcedName)` ; `AutoDeployTeamFallback()` ; `OpenDockForMultiplayerDeployment()` ; dock adapté au contexte PvP (10.2) |
| `Assets/Scripts/Network/NetMessage.cs` | Messages `submit_deployment`/`deployment_result`, types `UnitPlacement`/`DeployedUnit` (10.2) |
| `Assets/Resources/UI/DeploymentDockScreen.uxml` | Bouton `btn-mp-confirm` ajouté (caché par défaut) (10.2) |
| `Assets/_ServerDocs/multiplayer/03-network-protocol.md` | Nouveaux messages documentés |

### 10.6 Pas encore fait après cette session
- **Aucun de ces correctifs n'a été testé** (ni build client Android, ni redéploiement du
  `game-server` sur le VPS) — à faire avant de reprendre le test à 2 téléphones.
- Le placement manuel (10.2) n'a pas de retour visuel de la zone de déploiement légale à l'écran
  (le serveur recadre silencieusement une position hors zone) — amélioration possible plus tard
  (dessiner le cercle de zone au sol pendant le déploiement), pas bloquant.
- Pas de compte à rebours affiché pendant les 45s de déploiement (le serveur a bien un timeout,
  mais aucun message `deployment_timer` n'est envoyé) — amélioration UX possible plus tard.

---

## 11. Refonte GPS -> Zones de Conquête (grille Slippy Map fixe) — 2026-08-30

**Remplace entièrement** l'ancienne génération procédurale à rayon dynamique (`around:250,lat,lon`
Overpass + rectangle englobant lat/lon pour les tuiles raster). Motivation : en multijoueur, deux
téléphones à des positions GPS légèrement différentes généraient chacun une ville OSM différente
(bâtiments/NavMesh non identiques), désynchronisant le combat côté serveur. Voir §10.4 pour le
symptôme originel ("un tour d'exécution local fantôme").

**Nouvelle architecture :**
- `CityGenerator.ZONE_ZOOM = 17` (const) : une "Zone de Conquête" = exactement 1 tuile Slippy Map à
  ce zoom (~195m de côté à la latitude de Lille). Le GPS de l'appareil n'est converti en
  `(tileX, tileY)` qu'**une seule fois**, au tout premier lancement (`ZoneManager.
  InitializeHomeZoneFromGps`, persisté via PlayerPrefs) — ensuite le jeu ne raisonne plus qu'en
  index de tuile, plus jamais en GPS brut.
- `CityGenerator.zoneTileX/zoneTileY` remplacent `radius`/`latitude`/`longitude` (devenus une
  propriété calculée en lecture seule = centre de la bbox de la Zone). La requête Overpass utilise
  une bbox exacte `(south,west,north,east)` (voir `GeoProjection.TileBoundingBox`) au lieu de
  `around:250,...`. Cache disque : `CityCache_Z17_{tileX}_{tileY}.json` (déterministe, plus de
  troncature à 4 décimales GPS).
- `MapTileLoader` charge les 16 sous-tuiles raster fixes (Zoom 19, grille 4x4) qui composent
  exactement la Zone, via `GeoProjection.TileToSubTileRange` — plus de calcul de rectangle
  englobant à partir d'un rayon.
- **La ville par défaut (hors-ligne/secours réseau)** reste totalement indépendante de ce système
  (constantes `CityGenerator.DefaultOfflineLatitude/Longitude` fixes, cache `CityCache_Default.json`
  séparé) — sans cette séparation, `LoadDefaultOfflineCity()` appelée après une vraie Zone aurait
  rechargé le cache disque de CETTE Zone au lieu de la ville par défaut de `Resources`.
- Nouveau `CityGenerator.IsCityReady` (bâtiments + sol + NavMesh baké, pas juste le sol) — signal de
  complétion propre, utilisé côté serveur pour savoir précisément quand ouvrir le déploiement.

**Nouvelle jouabilité multijoueur — conquête territoriale (portée V1) :**
- Nouvelle table `zones` (schema.sql §6, migration à appliquer sur le VPS + `grant select on
  public.zones to authenticated;` — bootstrap-db.sh ne le fait pas automatiquement pour une table
  ajoutée après coup, voir le commentaire dans schema.sql).
- Nouveau mode `"conquest"` dans `join_matchmaking` (champs `zone_tile_x`/`zone_tile_y` ajoutés à
  `NetMessage`) : Zone neutre -> capture instantanée (pas de combat) ; Zone déjà possédée par un
  autre joueur -> combat contre une **garnison IA** (réutilise `TacticalAIPlanner`/
  `UnitAI.PlanifierTourIA`, exactement le mécanisme des ennemis du mode Solo), sur la géométrie
  RÉELLE de la Zone attaquée (chargée à la fois côté serveur et côté client via le même
  `(tileX,tileY)`, jamais transmise sur le réseau). Voir `MatchSessionManager` §"Conquête
  territoriale" et `ZoneManager.cs` (nouveau, `Assets/Scripts/Generation/`).
- **PAS fait** (limitation V1 assumée) : combat PvP en direct contre le propriétaire réel d'une Zone
  s'il est en ligne au moment de l'attaque — nécessiterait un système de notification/siège
  asynchrone (le propriétaire n'est pas forcément connecté). Les modes `deathmatch`/`zone_control`
  existants restent le seul vrai PvP humain pour l'instant, inchangés (gardent la carte par défaut
  fixe).
- ~~**PAS fait** : aucune UXML/UI pour attaquer une Zone~~ **CORRECTION (2026-08-30, §12) : fait plus
  tard la même session** — `ZoneMapScreen.uxml`/`ZoneResultScreen.uxml`/`ZoneMapController.cs`
  existent et sont câblés de bout en bout. Cette ligne était restée fausse dans ce fichier pendant un
  moment (le doc n'avait pas été remis à jour après coup) — voir §12 pour l'audit qui l'a repéré.

**À faire avant de tester :** migration `zones` + grant sur le VPS, rebuild client Android + rebuild
`game-server` (voir gotcha Bee stale-build, §9.8/[[project-streetact-vps-live]]), redéployer.

## 12. Audit jouabilité/gamification + corrections — 2026-08-30 (soir)

Un audit demandé explicitement ("expert de gamification, trouve les bugs et problèmes de
jouabilité") a relu directement le code (pas seulement cette doc) et trouvé plusieurs divergences
doc/code + bugs + trous de game design. Toutes les corrections ci-dessous ont été appliquées dans la
foulée (autorisation explicite reçue). Détail complet dans la transcription de la conversation de
cette session — résumé ici pour la prochaine session.

**Divergence doc/code trouvée et corrigée :**
- Le Ghost/AFK (`UnitAI.isGhosted` + `TacticalAIPlanner`) avait disparu du code à un moment non
  documenté — `MatchSessionManager.ApplyForPlayer` laissait un joueur absent totalement immobile,
  sans IA de secours, contrairement à ce que cette doc et `04-unity-headless-server.md` décrivaient
  encore. **Restauré** : `UnitAI.isGhosted` existe de nouveau, `TacticalAIPlanner.PlanTurnForUnit`
  planifie pour une unité "ghost" comme pour un ennemi Solo, remis à `false` dès que le joueur soumet
  de nouveau un ordre valide.

**Bugs corrigés (voir `MatchSessionManager.cs` sauf mention contraire) :**
1. **Course entre instances du pool sur la capture d'une Zone** — deux joueurs attaquant la même
   Zone au même instant sur deux instances différentes pouvaient tous les deux "gagner" et écraser
   silencieusement le résultat de l'autre (upsert `merge-duplicates` sans condition). `CaptureZoneInDb`
   fait maintenant une écriture CONDITIONNELLE : INSERT strict pour une Zone neutre (échoue si prise
   entre-temps), PATCH filtré sur `owner_user_id=eq.<propriétaire vu au début du combat>` pour une
   Zone contestée (n'affecte aucune ligne si elle a changé de mains depuis) — voir aussi le nouveau
   `reason="zone_lost_race"` sur `match_over` pour le cas "combat gagné mais Zone perdue à la course".
2. **Aucune validation serveur de l'adjacence** — un client modifié pouvait attaquer/capturer
   n'importe quelle Zone du monde. Le serveur exige maintenant que la Zone visée soit adjacente
   (4 directions) à une Zone déjà possédée par l'attaquant, sauf pour sa toute première capture
   (bootstrap, aucun territoire dont être adjacent). Nouveau `reason="not_adjacent"`.
3. **Auto-appariement possible** — `TryStartMatch` ignorait que `queue[0]`/`queue[1]` puissent
   partager le même `UserId` (double connexion avec le même JWT). Cherche maintenant la première
   paire d'`UserId` distincts dans la file.
4. Rating ELO jamais clampé à 0 (`UpdateRatings`) — `Mathf.Max(0, ...)` ajouté.
5. `MapTileLoader.DownloadAndApplyMap` bloquait un tick entier sur la branche "tuile en cache" (pas
   de yield) — impactait maintenant aussi le SERVEUR headless (`MatchSessionManager.LoadZoneOnServer`
   appelle ce même chemin à chaque combat de conquête), pas seulement le mobile. Un `yield return
   null;` par sous-tuile a été ajouté.
6. Double-clic possible sur les boutons d'attaque de Zone (`ZoneMapController`) — verrou
   `attackInFlight` ajouté, levé à chaque réouverture de l'écran.
7. `match_over` en conquête ignorait `zone_tile_x/y`/`success` côté client — le chemin "victoire
   avec combat" affichait un message générique moins informatif que la capture instantanée sans
   combat. `MultiplayerMatchController.OnMatchOver` distingue maintenant les 4 issues (capturée /
   garnison vaincue mais Zone perdue à la course / garnison vaincue / défaite) avec le détail de la
   Zone concernée.

**Trou de game design corrigé — la conquête n'avait AUCUNE récompense mesurable ni frein à
l'expansion :**
- **Renfort de garnison selon la taille du territoire du défenseur** (NOUVEAU,
  `GarrisonExtraInfantryForZoneCount` dans `MatchSessionManager`) : +1 fantassin à partir de 3 Zones
  possédées, +2 à partir de 6, +3 à partir de 10 — un empire plus grand devient mécaniquement plus
  dur à continuer d'agrandir, sans construire un système d'économie complet. Combiné à la règle
  d'adjacence ci-dessus (expansion forcément contiguë, plus "attaquer n'importe où"), ça referme
  directement le scénario "rich get richer sans mécanique de retour" identifié par l'audit.
- **Petit ajustement de classement pour les combats de conquête** (NOUVEAU,
  `ApplyConquestRatingDelta`) : +8 si Zone réellement capturée, -4 si le combat est perdu, 0 si gagné
  mais Zone perdue à la course (pas la faute du joueur) — un delta fixe, pas un calcul ELO complet
  (l'"adversaire" est une garnison IA sans rating). Jusqu'ici capturer une Zone n'avait absolument
  aucun effet visible pour le joueur (juste une ligne invisible dans la table `zones`).
- **L'écran "CARTE DES ZONES" n'affichait aucune information avant d'attaquer** (4 boutons de
  boussole + coordonnées de tuile brutes) — `ZoneMapController.RefreshNeighborOwnership` interroge
  maintenant le statut des 4 Zones voisines (Neutre / À vous / Ennemi) et l'affiche directement sur
  chaque bouton avant que le joueur ne clique. Reste volontairement simple (texte, pas une vraie
  tuile de carte) — suffisant pour lever l'aveuglement total sans reconstruire tout l'écran.

**Explicitement PAS traité dans cette session (décisions produit, pas des bugs) :**
- Le vrai propriétaire d'une Zone attaquée n'est toujours pas notifié ni mis en jeu (siège
  asynchrone PvP réel — limitation V1 déjà assumée en §11, toujours hors scope).
- Pas de plafond dur sur le nombre de Zones qu'un joueur peut posséder — le frein reste la difficulté
  croissante de la garnison, pas une limite explicite.
- Pas de vraie tuile cartographique visuelle (juste du texte par bouton) — une vraie mini-carte
  resterait un chantier UI à part entière si souhaité plus tard.

**À faire avant de tester ces corrections :** rebuild `game-server` (Linux) ET client Android, puis
redéployer sur le VPS (voir [[project-novgov-vps-live]]) — aucun de ces changements n'est encore en
production au moment d'écrire cette section.

## 13. Bug de déploiement PvP + vrai brouillard de guerre réseau — 2026-08-30 (suite)

Retour utilisateur direct après le déploiement du §12 : en multijoueur (deathmatch/zone_control), le
joueur voit la carte, peut faire quelques manipulations, puis reçoit D'UN COUP des unités bleues ET
rouges, certaines posées sur des polygones de bâtiments difficiles à manier, sans avoir jamais pu
placer les siennes ni distinguer clairement lesquelles étaient les siennes.

### 13.1 Cause racine du déploiement "soudain"

`MatchSessionManager.RunMatch` envoie `match_found` puis démarre IMMÉDIATEMENT le compte à rebours
de 45s de `RunDeploymentPhase` — sans jamais attendre que le CLIENT ait fini de charger sa propre
carte (génération procédurale OSM, potentiellement plus longue que 45s à elle seule). Résultat : le
timer pouvait expirer avant même que `OpenDeploymentDock()` (appelé côté client seulement après
`LoadDefaultMapThenOpenDeployment`/`LoadConquestZoneThenOpenDeployment`) ne s'exécute — le joueur
n'avait alors JAMAIS vu son propre dock de placement, et le serveur appliquait le repli automatique
(`AutoDeployTeamFallback`) aux DEUX camps à la fois, d'où l'apparition simultanée bleu+rouge sans
étape de placement manuel.

**Fix** : nouveau message `"deployment_ready"` (client -> serveur), envoyé par
`MultiplayerMatchController.OpenDeploymentDock()` une fois le dock réellement affiché.
`RunDeploymentPhase`/`RunConquestDeploymentPhase` attendent maintenant ce signal des DEUX joueurs
(un seul pour la conquête) — plafonné à `MapReadyMaxWaitSeconds = 60f` en filet de sécurité — AVANT
de démarrer le vrai compte à rebours de 45s. Nouveau champ `PlayerConnection.MapReady`.

### 13.2 "Unités posées sur des polygones, difficiles à manier"

`UnitSpawnerUI.SpawnUnitAt` plaçait les unités via un simple `NavMesh.SamplePosition` (point NavMesh
le plus PROCHE, qui peut être un toit de bâtiment praticable pour l'IA mais faux pour un spawn) au
lieu du helper existant `FindGroundLevelNavPoint` (échantillonne un anneau de points, garde le Y le
plus bas) — déjà utilisé pour l'ancrage de `AutoDeployTeamFallback` mais PAS pour chaque unité
individuelle. **Fix** : `SpawnUnitAt` appelle maintenant `FindGroundLevelNavPoint` avant son
`NavMesh.SamplePosition` — corrige d'un coup TOUS les chemins de spawn (déploiement manuel, repli
auto, renfort de garnison, apparition d'unité repérée en jeu) puisqu'ils passent tous par cette
même méthode.

### 13.3 Vrai brouillard de guerre réseau (demande explicite : "le joueur ne doit pas voir les unités ennemies avant qu'elles ne soient repérées")

Avant cette session, le serveur envoyait l'état COMPLET des deux camps, identique, aux deux clients
à CHAQUE tick (`turn_result`) et au déploiement (`deployment_result`) — un joueur voyait donc la
position exacte de chaque unité ennemie en permanence, repérée ou non (confirmé par l'audit du
§12, jamais corrigé jusqu'ici). Le calcul de repérage existait déjà (`IsUnitSpottedByTeam` en Solo,
son jumeau `Novgov.TacticalCore.LineOfSight.CanBeSpotted` déjà porté dans le moteur multijoueur) mais
n'était utilisé QUE pour le ciblage de l'IA, jamais pour décider quoi envoyer au réseau.

**Fix (voir `MatchSessionManager.cs`) :**
- `deployment_result` : chaque client ne reçoit plus que SA PROPRE escouade (les unités adverses
  n'existent pas encore côté client) — SAUF les barricades, qui restent visibles des deux côtés dès
  le départ (ce sont des éléments de terrain physiques comme un bâtiment, pas un renseignement sur
  les forces adverses ; les cacher aurait créé des murs invisibles dont l'effet de couverture reste
  bien réel côté serveur sans jamais se voir à l'écran).
- `turn_result` : deux messages DISTINCTS construits par tick (`FilterSnapshotsForTeam` +
  `ComputeVisibleUnitIds`) au lieu d'un seul partagé — chaque destinataire ne voit que ses propres
  unités + les unités adverses VIVANTES repérées par au moins une de ses unités à cet instant précis
  (`LineOfSight.CanBeSpotted`, portées/blocages muraux identiques au Solo). Un ennemi mort reste
  visible (le résultat d'un combat ne doit pas disparaître).
- Nouveaux champs `unit_type`/`team_id` sur `NetMessage.UnitState` : nécessaires pour que le client
  puisse faire apparaître dynamiquement une unité ennemie la toute première fois qu'elle est
  repérée (`MultiplayerMatchController.PlaySnapshotsCoroutine`), puisqu'elle n'existe pas encore en
  scène à ce moment-là. Une unité ennemie déjà apparue mais absente d'un tick est masquée (jamais
  détruite : peut réapparaître si elle redevient visible) via `UnitAI.SetVisualsVisibility(false)`.
- `FogOfWarEntity` (jusqu'ici câblé "toujours visible à 100%", commentaire d'origine : "Fog of War
  désactivé") force maintenant la visibilité en continu UNIQUEMENT hors multijoueur
  (`!MultiplayerMatchController.IsActive`) — sinon il annulait le masquage ci-dessus dès la frame
  suivante.
- `UnitSpawnerUI.maxUnitsPerTeam` (plafonné à 4 pendant le dock de déploiement) est relevé à 8 une
  fois `deployment_result` reçu — sans ça, une garnison de conquête renforcée (jusqu'à 4+3=7 unités,
  voir §12) aurait pu se voir refuser silencieusement l'apparition de ses derniers renforts au
  moment d'être repérée.

**Approximation assumée (documentée dans le code)** : la géométrie des murs/barricades utilisée pour
`CanBeSpotted` pendant la relecture tick par tick est celle de FIN de tour (après résolution), même
pour les premiers ticks — un mur détruit PENDANT ce tour est donc traité comme déjà détruit pour
TOUTE la relecture. Le sens de l'erreur reste toujours prudent (peut seulement masquer une unité un
peu plus longtemps que la réalité, jamais révéler une position qui aurait dû rester cachée). De même,
le camouflage utilisé pour le calcul est celui de FIN de tour (`worldState.units` post-résolution),
pas l'état exact à chaque tick intermédiaire.

**Non traité (hors scope de cette session)** : le mode Zone de Contrôle (`zone_control`) affiche déjà
une barre de progression par équipe (`zone_progress_team1/2`) — ce chiffre reste communiqué aux DEUX
joueurs sans brouillard (ce n'est pas une position d'unité), inchangé.

**À faire avant de tester :** rebuild + redeploy serveur ET Android (aucun de ces changements n'est
encore en production au moment d'écrire cette section) — puis un vrai test à 2 téléphones est le seul
moyen de vérifier que le rythme de révélation "spotted/pas spotted" est satisfaisant en jeu (portées
de repérage inchangées par rapport au Solo, mais jamais testées en conditions PvP réelles avant ce
changement).

## 14. Reste de la session du 2026-08-30 — bugs trouvés par simulation réelle, perf, aperçu de trajectoire

Le §13 ci-dessus a été rebuild/redéployé puis testé via un client de simulation TCP brut
(`sim_client.js`, scratchpad de session, jamais committé) parlant directement le protocole contre le
pool de production, plutôt qu'un vrai test à 2 téléphones (aucun accès matériel depuis cette
session) — logs serveur réels lus avec des fenêtres `--since`/`--until` précises. Plusieurs tours de
retours utilisateur + simulation ont suivi dans la même journée, résumés ici (jamais écrits dans ce
fichier jusqu'à présent, seulement dans les notes de session).

**Bugs trouvés et corrigés :**
1. **`AudioClip.SetData failed` loggé à CHAQUE spawn d'unité/barricade, sur TOUTES les parties, en
   continu** — `UnitSpawnerUI.SpawnUnitAt`/branche Barricade jouaient un son de confirmation sans
   garde `#if !UNITY_SERVER` ; un Dedicated Server n'a pas de device audio. Corrigé (deux sites
   d'appel gardés), confirmé par logs : zéro warning après le fix sur 4 spawns testés (contre 1 par
   spawn avant).
2. **Une unité ennemie masquée par le brouillard de guerre (§13.3) gardait un `Collider` actif** —
   `UnitAI.SetVisualsVisibility(false)` ne désactivait que le rendu, pas le collider. Le raycast
   tap-to-move (`TacticalPathManager_Input`, sans layer mask) pouvait alors intercepter un clic
   destiné au sol derrière une unité invisible, corrompant la destination cliquée — cause probable
   des retours "problème de trajectoire"/"les unités sont toujours là". Corrigé : `SetVisualsVisibility`
   désactive maintenant aussi tous les `Collider` enfants.
3. **Le corps d'une unité morte disparaissait UN TOUR après sa mort** — `RunExecutionPhase` filtre
   `allUnits` sur `!isDead`, donc un cadavre est absent de tout futur tour ; le code de masquage du
   §13.3 traitait alors ce cadavre comme "non repéré" et le cachait. Corrigé côté client uniquement
   (zéro risque, aucune règle serveur touchée) : `PlaySnapshotsCoroutine` ne masque plus jamais une
   unité déjà `isDead`.
4. **Carte par défaut "carré clair dans une mer sombre de bâtiments"** — `DefaultCityData.json`
   (snapshot Overpass figé) datait du 2026-08-14, capturé sous l'ANCIEN système GPS-radius (~500m)
   jamais régénéré après le passage aux Zones Slippy Map de 195m (§11) : le sol raster (bien
   redimensionné) se retrouvait au milieu d'un jeu de bâtiments bien plus grand et périmé.
   Régénéré pour la tuile Zoom 17 exacte (tileX 66693, tileY 44057) — 87 bâtiments contre 758
   avant. **Important** : le log historique "396 -> 758 bâtiments" vu dans les sessions précédentes
   n'était PAS un signal de bonne santé, juste la reproduction fidèle de ce même jeu de données
   périmé à chaque déploiement. Le nouveau chiffre de référence est **"87 éléments OSM -> 236
   bâtiments"** — si ce nombre change un jour sans qu'on ait touché `DefaultCityData.json`
   volontairement, c'est suspect.
5. **Bannière "DÉPLOIEMENT (...)" restait affichée par-dessus le HUD de combat** —
   `MultiplayerMatchController.IsDeploymentPhaseActive` n'était remis à `false` que dans
   `SubmitLocalDeployment()` (clic manuel du joueur), jamais dans `OnDeploymentResult` — un
   déploiement résolu par repli automatique (timer expiré sans clic) laissait le flag bloqué à
   `true` pour tout le reste de la partie. Corrigé : `OnDeploymentResult` le remet à `false`
   inconditionnellement.
6. **La bannière de déploiement fuitait le nombre d'unités adverses déployées** (`"DÉPLOIEMENT (X vs
   Y)"`, `Y` = effectif équipe 2) — affichage hérité du Solo (comparer à une IA à effectif fixe),
   incohérent maintenant que le brouillard de guerre réseau (§13.3) rend `Y` généralement 0.
   `UnitSpawnerUI.RefreshDeploymentDockUI` affiche maintenant `"DÉPLOIEMENT (X/max)"` en
   multijoueur, laisse l'affichage Solo `"X vs Y"` inchangé.

**Perf — turn 1 d'une partie coûtait ~2s réels au lieu d'un calcul "quasi instantané" comme prétendu
par le commentaire du code :** mesuré précisément (logs `[Timing]`) : `TacticalGridBuilder.
BuildFromScene()` seul coûtait ~2050ms, `TacticalResolver.Resolve()` seulement 8-70ms — chaque tour
reconstruisait à partir de zéro les 1538 segments de mur + la grille de marche des 236 bâtiments,
alors que cette géométrie ne change jamais entre deux tours (sauf destruction d'un bâtiment).
**Fix** : cache statique dans `TacticalGridBuilder` (murs + grille construits une fois par
ville/Zone chargée, réutilisés chaque tour ; seul l'état santé/détruit de chaque bâtiment est relu
en direct, ~236 lectures de champ, très bon marché). Invalidation explicite à chaque endroit où la
géométrie de scène change réellement (`CityGenerator.CancelActiveGenerationAndClearCity`, chokepoint
partagé côté client ET serveur). **Gain mesuré** : tour 1 encore ~2085ms (premier build,
inévitable), tour 2 tombe à **1.7ms**, tour 3 à **1.6ms** — `RunExecutionPhase` complet passe de
~2230ms à ~57-64ms, un gain d'environ **35x** pour chaque tour après le premier.

**Bug structurel — la ligne bleue d'aperçu ne correspondait pas au déplacement RÉELLEMENT exécuté :**
le tracé de prévisualisation (`TacticalPathManager_PathDrawing.DessinerTousLesChemins`) utilisait
`NavMesh.CalculatePath` (précis, suit les rues) alors que le déplacement OFFICIEL en multijoueur
était déjà calculé par `Novgov.TacticalCore.Pathfinding` (grille A* déterministe, introduite pour
éviter toute divergence NavMesh entre appareils) — deux algorithmes de routage complètement
indépendants sur la même géométrie, qui pouvaient diverger nettement dans les virages. **Fix de
CETTE session (partiel)** : `TacticalPathManager_PathDrawing.cs` appelle désormais la MÊME
`Pathfinding.FindPath()` que le serveur pour dessiner l'aperçu dès que
`MultiplayerMatchController.IsActive` est vrai (nouveau `AppendGridPathSegment`) — l'aperçu affiché
correspond enfin au calcul serveur. **Ce correctif ne réglait que l'APERÇU** : `TacticalResolver.
Resolve()` lui-même continuait à déplacer chaque unité en LIGNE DROITE entre les points bruts d'un
ordre, sans jamais relancer ce même calcul A* pour l'EXÉCUTION — la vraie cause du bug "l'unité
prend un raccourci" remontée par l'utilisateur début septembre, seulement corrigée à la racine au
§16 ci-dessous. À noter aussi : ce correctif a nécessité de rendre le cache de
`TacticalGridBuilder` conscient qu'il tourne dans un process CLIENT ou SERVEUR distinct (chacun sa
propre instance de cache) et d'ajouter un préchauffage proactif (`OnDeploymentResult`) pour que le
coût ~2s du tout premier `BuildFromScene()` tombe à un moment de transition attendu plutôt qu'un gel
imprévisible pendant que le joueur trace son premier chemin.

**Pool de serveurs de jeu — construit puis réduit à 1 seule instance le même jour :** en réponse à
une demande de scaling ("je veux des milliers de joueurs"), un vrai pool de 3 conteneurs
`game-server-N` indépendants (matchmaker léger + table `server_instances`, voir schema.sql §7) a été
construit et déployé — avant qu'une réécriture ultérieure la même session (donnée pure, voir
`04-unity-headless-server.md` "Option B") ne rende ce pool obsolète : un seul processus fait
maintenant tourner des centaines/milliers de parties Deathmatch/Zone de Contrôle EN PARALLÈLE (pool
de threads `Task.Run` pour `TacticalResolver.Resolve()`), donc dupliquer les conteneurs n'apportait
plus de capacité réelle, juste un coût de base répété. Le pool a été ramené à **1 seule instance**
(`game-server-1`), toujours vraie au 2026-09-02 (voir `docker-compose.yml`, un seul service
`game-server-1` défini). La Conquête reste la seule limitation "un combat vivant à la fois par
instance" (`matchInProgress`), inchangée — voir §15 ci-dessous pour l'Entraînement IA, qui partage
cette même contrainte.

**Vérifié en fin de session** : serveur + Android recompilés (0 erreur) et redéployés à chaque étape
ci-dessus, logs de production confirmant les correctifs (zéro warning `AudioClip`, "87 éléments OSM
-> 236 bâtiments" au lieu de "396 -> 758", timings `[Timing]` conformes aux chiffres annoncés).
**Non vérifié en conditions réelles à 2 téléphones** (aucun accès matériel pendant cette session,
seulement via le client de simulation TCP) : le rythme de révélation du brouillard de guerre, le
ressenti "le jeu est un peu en retard" (piste probable : bug #5 ci-dessus, deux rafraîchisseurs UI
Toolkit se battant chaque frame — pas confirmé indépendamment), et si "certaines unités n'ont aucune
ligne bleue" (candidats jamais isolés : mortier avec un ordre `TirMortier`, qui vise une cible et non
une destination de marche, ou simplement une unité sans ordre du tout — pas nécessairement un bug).

## 15. Session du 2026-09-02 — IA/unités "posées sur les polygones" (vraie cause) + mode Entraînement contre l'IA

Retour utilisateur : l'IA place "toujours" ses unités sur les polygones de bâtiments, surtout les
chars/véhicules lourds et les barricades — un défaut différent (et non couvert) du §13.2 : ce
correctif-là avait ajouté `FindGroundLevelNavPoint` (échantillonnage d'anneau, garde le point
NavMesh le plus bas) à `UnitSpawnerUI.SpawnUnitAt`, mais deux failles restaient :

1. **La branche Barricade de `SpawnUnitAt` ne passait par AUCUN recalage** — elle instanciait
   directement à la position brute demandée puis faisait `return null` (une barricade n'est pas une
   `UnitAI`) AVANT d'atteindre le bloc de recalage partagé plus bas dans la méthode. Seul chemin de
   spawn de barricade réellement affecté par la garnison IA : `UnitSpawnerUI.SpawnEnemyWave()`
   (mode Solo classique, toujours appelée avec une position brute).
2. **Même pour les unités déjà recalées, "le point NavMesh le plus bas parmi un anneau proche" ne
   suffit pas** : le rez-de-chaussée d'un bâtiment est LUI AUSSI un point NavMesh bas et valide (les
   toits sont praticables pour les snipers, donc valides sur le même NavMesh) — rien ne testait
   explicitement "ce point tombe-t-il dans l'empreinte 2D d'un bâtiment ?".

**Fix** (`UnitSpawnerUI.cs`) : nouvelle méthode `FindSafeSpawnPoint(desired, type, baseSearchRadius)`
qui exclut explicitement tout candidat dont `BuildingStructure.FindBuildingAt(...)` (le test
point-dans-polygone déjà utilisé ailleurs dans le jeu, pas une heuristique de hauteur) renvoie un
bâtiment — avec un rayon de dégagement adapté au type (3m Char Leopard, 2.5m Véhicule Canon, 1.8m
Barricade, 0.6m Fantassin) et un élargissement progressif de la recherche (6 → 12 → 24 → 48m) si le
point visé tombe en pleine zone bâtie dense. Appelée UNE SEULE FOIS, tout en haut de `SpawnUnitAt`,
pour TOUS les types y compris Barricade — corrige donc les deux failles à la fois et couvre
uniformément déploiement manuel, repli auto, garnison de Conquête et `SpawnEnemyWave`.

**Amélioration IA au passage** : la garnison IA (équipe 2, `AutoDeployTeamFallback`) ne posait
jusqu'ici JAMAIS de barricade — `TacticalAIPlanner.PlanInfantryBehavior` a pourtant une vraie
"STRATÉGIE BARRICADE" (se retrancher derrière une barricade alliée) qui n'avait donc jamais rien à
utiliser en dehors d'un placement manuel de joueur. La garnison pose maintenant une barricade
orientée vers le centre de la carte (direction d'approche la plus probable d'un attaquant).

### Nouveau mode `practice_ai` — jouer contre l'IA en attendant un adversaire

Demande explicite : proposer à un joueur seul en file d'attente Deathmatch/Zone de Contrôle de
jouer contre l'IA en attendant qu'un vrai adversaire se présente, sur la même carte. Implémenté
comme un mode `join_matchmaking` à part entière (voir `03-network-protocol.md`) plutôt qu'une
bascule automatique en cours de partie (pas de siège/reprise de partie existant, voir §11) :

- **Client** (`MultiplayerMatchController`) : un bouton "🤖 JOUER CONTRE L'IA EN ATTENDANT" apparaît
  dans l'écran d'attente (`WaitingScreen.uxml`) après 6s passées en file (`PlayVsAiOfferDelaySeconds`),
  jamais pour la Conquête (déjà résolue instantanément, pas une vraie file). `StartPracticeVsAI()`
  ferme volontairement la connexion en file (`GameServerClient.Disconnect`, aucun message "annuler
  la file d'attente" n'existe dans le protocole) puis en rouvre une nouvelle avec
  `mode="practice_ai"` — le serveur nettoie automatiquement l'ancienne entrée de file via son
  `RemoveAll(c => c.IsDisconnected)` habituel. Un drapeau `isSwitchingToPractice` empêche
  `HandleServerDisconnected` de traiter cette déconnexion volontaire comme une vraie perte réseau.
- **Serveur** (`MatchSessionManager`) : `practice_ai` est traité comme la Conquête — PAS ajouté à
  `waitingDeathmatch`/`waitingZoneControl`, résolu immédiatement par `HandlePracticeAiMessage` ->
  `RunPracticeVsAI`, qui réutilise TEL QUEL le moteur "vivant" 1-joueur-contre-garnison déjà éprouvé
  par la Conquête (`RunConquestDeploymentPhase`/`RunConquestPlanningPhase`/`RunExecutionPhase`,
  `TacticalAIPlanner`) — jamais le moteur "Option B" en donnée pure (celui-ci ne sait pas piloter
  d'IA). Différences avec la Conquête : toujours la carte par défaut (`RestoreDefaultMapOnServer`,
  jamais `LoadZoneOnServer`), garnison toujours à sa force de base (`defenderOwnerId=null`, pas de
  `GarrisonExtraInfantryForZoneCount`), et **aucune** interaction avec la table `zones` ni le
  classement (`your_new_rating` reste à 0 côté client — partie hors-score, affichée comme telle).
  Hérite de la même contrainte `matchInProgress` que la Conquête : un seul combat "vivant"
  (Conquête OU Entraînement) à la fois par instance — un second joueur qui déclenche l'entraînement
  pendant que l'autre tourne déjà reçoit un refus immédiat (`match_over` avec `reason="server_busy"`),
  pas une attente indéfinie.
- Aucun changement de schéma DB ni de protocole réseau : `mode` est déjà un champ texte libre
  (`NetMessage.mode`), `practice_ai` s'y glisse exactement comme `"conquest"` l'a fait avant lui.

**Vérifié en conditions réelles contre novgov.com** (client de simulation TCP, scratchpad
`sim_practice_ai.js`, jamais committé) : `join_matchmaking(practice_ai)` -> `match_found`
(`opponent_username="IA (Entraînement)"`) -> `deployment_result` (4 unités, positions Y=0/0.25,
plus aucune sur un toit) -> 60 tours joués sans ordre -> `match_over` (`reason="practice"`,
`winner_team=0`, match nul comme attendu sans combat) -> zéro exception dans les logs serveur,
conteneur stable après. Serveur Linux rebuild + redéployé sur `novgov.com` (`game-server-1`),
Android rebuild (compile propre) mais changement purement UI côté client — aucun changement
serveur-authoritatif ne dépend de la version installée sur l'appareil.

**Explicitement pas fait** : pas de bascule automatique vers un vrai adversaire humain pendant une
partie d'entraînement en cours (le joueur doit la terminer avant de retourner en file d'attente) —
nécessiterait un système de reprise de partie qui n'existe pas (même limitation que le siège PvP
asynchrone de Conquête, §11).

## 16. Session du 2026-09-02 (suite) — correctif "prend le raccourci" : trajectoire ET commandes de checkpoint

Retour utilisateur, décrit comme critique : "les unités doivent suivre EXACTEMENT la trajectoire
définie lors de la planification, pas prendre un raccourci — il y a à chaque checkpoint des
commandes à exécuter." Root-causé au niveau du moteur (`Novgov.TacticalCore`), pas un simple bug
d'affichage — deux failles distinctes, cumulées :

1. **`TacticalResolver.Resolve()` déplaçait chaque unité en LIGNE DROITE entre les points bruts
   d'un ordre** (`UnitOrders.path`, une simple `List<Vector2>`) — c'était déjà la cause identifiée
   (mais seulement pour l'APERÇU, pas l'exécution) au §14 ci-dessus : le §14 avait corrigé la ligne
   bleue affichée pour qu'elle suive le vrai calcul A* de la grille (`Pathfinding.FindPath`), mais
   `TacticalResolver.Resolve()` lui-même n'a jamais été touché à ce moment-là et continuait à
   ignorer ce calcul pour l'exécution réelle — d'où le "raccourci" : l'aperçu montrait la bonne
   route, l'unité en suivait une autre.
2. **Toutes les commandes de checkpoint d'un même ordre (Guetter, Garnison, Escalade...) étaient
   aplaties en un UNIQUE jeu de drapeaux** (`UnitOrders.setGuarding`/`enterOverwatchAtEnd`/etc.),
   appliqué une seule fois via `ApplyEndOfPathEffects` — DÈS QUE le dernier point du chemin était
   atteint, jamais avant. Une commande posée sur un checkpoint intermédiaire (pas le dernier du
   trajet) n'avait donc silencieusement aucun effet tant que l'unité n'avait pas fini TOUT son
   trajet, y compris les points suivants sans rapport avec cette commande.

**Fix, au niveau des structures de données ET du moteur (`TacticalCore/TacticalTypes.cs` +
`TacticalCore/TacticalResolver.cs` + `Server/MatchSessionManager.cs`, `BuildUnitOrders`/
`BuildUnitOrdersPure`) :**
- `UnitOrders.path` (liste de positions) + son unique jeu de drapeaux de fin de chemin sont
  remplacés par `UnitOrders.checkpoints` (`List<PathCheckpoint>`) — un `PathCheckpoint` par nœud
  réellement posé (miroir direct d'un `TacticalPathManager.TacticalNode`), chacun portant SA
  PROPRE commande (mêmes champs qu'avant : `setGuarding`, `enterOverwatchAtEnd`+`overwatchToSet`,
  `setCamouflaged`, `setGarrisonWindow`+`windowNormalToSet`, `setGarrisonDoor`, `enterBuildingId`,
  `exitBuilding`, `setPositionY`). La descente implicite de toit (climb-down automatique si le
  prochain ordre ne ré-escalade pas) reste un effet de FIN D'ORDRE entier
  (`UnitOrders.implicitDescentY`, pas liée à un checkpoint précis — fidèle au comportement
  d'origine, elle ne correspond à aucun `NodeAction` posé explicitement).
- `TacticalResolver.Resolve()` expanse chaque ordre UNE FOIS (avant la boucle de tick, pas à
  chaque pas) via une nouvelle `ExpandOrder` : relie chaque paire de checkpoints consécutifs par le
  VRAI chemin `Pathfinding.FindPath` (la même grille A* que le serveur utilise déjà partout
  ailleurs), en mémorisant à quel INDEX exact, dans ce chemin étendu, chaque checkpoint est
  réellement atteint. Le dernier pas de chaque tronçon est ramené EXACTEMENT sur la position
  demandée par le checkpoint (pas le centre de cellule de grille, décalé de jusqu'à ~1m) — important
  pour les commandes liées à une géométrie précise (fenêtre, porte). Repli sur une ligne droite si
  la grille est absente ou si aucun chemin n'existe (zone coupée par des décombres) — mieux vaut un
  mouvement direct qu'une unité totalement bloquée. `ApplyEndOfPathEffects` renommée
  `ApplyCheckpointEffects` et appelée à CHAQUE checkpoint atteint, plus seulement au dernier —
  y compris l'armement d'Overwatch, qui surveille désormais depuis le checkpoint où il a été posé
  même si l'unité continue ensuite vers d'autres checkpoints.
- **Aucun changement de protocole réseau** : le format `submit_turn`/`path`/`action` reste
  identique (voir `03-network-protocol.md`, mis à jour pour documenter explicitement que chaque
  point est un checkpoint indépendant exécuté sur place). Le client n'a besoin d'AUCUNE
  modification pour ce correctif — l'aperçu (§14) suivait déjà le bon tracé, seule l'exécution
  serveur a changé.
- **Effet de bord bénéfique pour l'IA** : `TacticalAIPlanner` construit ses ordres via le même
  `BuildUnitOrders`/`TacticalResolver` — une unité IA (char qui recule, mortier qui se replie,
  infanterie qui rejoint une barricade) bénéficie automatiquement de la même correction, sans
  aucune modification du planificateur IA lui-même : ses ordres à un seul checkpoint étaient déjà
  logiquement corrects, seule leur EXÉCUTION (le trajet réel emprunté pour l'atteindre) pouvait
  auparavant couper à travers un bâtiment.
- **Garde-fou anti-abus resserré** : chaque checkpoint déclenche maintenant une vraie recherche A*
  (coût réel, contrairement à l'ancien simple segment de ligne droite) — `MaxOrderPathNodes` abaissé
  de 200 à 40 par précaution (aucun joueur légitime n'en pose autant en un tour avec un budget de
  déplacement de 50m).

**Testé à trois niveaux avant déploiement :**
1. **14/14 tests automatisés** (`Assets/Editor/TacticalCoreSelfTest.cs`, `Novgov/Tests/TacticalCore
   Self-Test`) — 11 tests déjà existants toujours au vert (aucune régression), + 3 nouveaux écrits
   spécifiquement pour ce correctif : le mouvement résolu contourne bien un bâtiment placé entre
   départ et checkpoint (pas de ligne droite au travers) ; une commande de checkpoint intermédiaire
   (Overwatch) déclenche BIEN avant que l'unité n'atteigne un checkpoint suivant volontairement très
   lointain (preuve qu'elle n'est plus reportée en fin de trajet) ; un ordre à 3 checkpoints les
   visite dans l'ordre exact.
2. **Test réel contre novgov.com** (`sim_trajectory_test.js`, scratchpad, jamais committé) : une
   unité envoyée avec 2 checkpoints (Guetter à 8m à l'est, puis continuer 12m plus au nord) a
   parcouru **20,1m** au total sur 24 points de trajectoire distincts reçus (contre ~14,4m si elle
   avait coupé directement vers le point final), en passant confirmé près du checkpoint
   intermédiaire, pour finir EXACTEMENT (0,0m d'écart) sur le second checkpoint. Logs serveur
   confirmant `Fantassin_1_1:2pt` reçu et traité, zéro exception.
3. **Performance mesurée en conditions réelles** (logs `[Timing]` de production) : le tour incluant
   ces 2 checkpoints + 4 ordres à 1 checkpoint de la garnison IA (6 recherches A* réelles au total)
   s'est résolu en **25,5ms** — les tours suivants (sans nouvel ordre) retombent à ~0,2-0,4ms comme
   avant ce correctif. Négligeable au regard du gain de ~35x déjà obtenu au §14.

**Déployé** : serveur Linux rebuild (3 fois dans la session : correctif principal, puis le
resserrement de `MaxOrderPathNodes`) et redéployé sur `game-server-1`/novgov.com à chaque étape,
conteneur vérifié sain (logs propres, aucune exception) après chaque déploiement. **Android non
rebuild pour ce correctif** — aucun fichier client (`Assets/Scripts/Network`, `Assets/Scripts/UI`)
n'a été touché, uniquement `TacticalCore`/`Server` (compilés dans l'APK mais jamais exécutés
côté client, qui se contente de rejouer les snapshots envoyés par le serveur) : le correctif est
donc déjà pleinement actif pour tous les joueurs, quelle que soit la version d'APK installée.

**Non traité (hors scope de cette session, pas demandé) :** le mode Solo (`TacticalPathManager_
Execution.cs`) n'a jamais eu ce bug — il pilote un vrai `NavMeshAgent` qui route déjà correctement
autour des obstacles et exécute chaque nœud de `tacticalPath` séquentiellement en conditions réelles
(simulation Unity live, pas le moteur `TacticalCore` déterministe) — vérifié par lecture de code,
pas modifié.

## 17. Session du 2026-09-02 (suite) — tests réels sur 2 émulateurs Android, radar agrandi, correctif d'orientation

Retour utilisateur : "le radar est petit", demande explicite de tester interfaces/jouabilité/bugs
"sur deux émulateurs" plutôt que par simulation TCP seule, et de corriger le support du mode
portrait. Première session de ce chantier à réellement lancer l'APK sur un émulateur Android (SDK
`platform-tools`/`emulator` déjà installés sur la machine, deux AVD utilisés : `Medium_Phone_API_36.0`
en 1080×2400 et `flutter_emulator` en 1080×1920) plutôt que de s'appuyer uniquement sur un client de
simulation TCP headless (§14-16).

### 17.1 Radar agrandi

`TacticalRadarUI.radarSize` était une constante fixe (120px) réglée à l'œil dans la fenêtre Game de
l'Éditeur — beaucoup plus petite en pixels réels sur un vrai/faux téléphone (bien plus de pixels que
cette fenêtre) ET ne s'adaptant pas entre portrait (largeur étroite) et paysage (largeur large).
**Fix** : recalculé chaque frame dans `OnGUI()` comme 26% de la plus PETITE dimension virtuelle de
l'écran (celle qui contraint réellement l'espace disponible, quelle que soit l'orientation), borné
[140, 230]px. `TacticalBottomBarScreen.uxml` (colonne réservée pour ne pas chevaucher la barre
d'escouade/FIN DE TOUR) mise à jour avec le MÊME pourcentage/bornage pour rester synchronisée
(démontré mathématiquement sûr dans les deux orientations : en portrait les deux calculs utilisent
littéralement la même valeur (largeur d'écran), en paysage la colonne UI Toolkit réserve toujours
AU MOINS autant que le radar réel, jamais moins).

### 17.2 Bug trouvé pendant les tests réels : texte du radar qui se chevauche

En testant sur émulateur, le pied de page du radar ("ALLIÉS: X | CONTACTS: Y") apparaissait sur 2
lignes superposées illisibles — invisible sur les captures d'écran issues du simulateur TCP (qui ne
rend jamais l'UI). Cause : `totalH` (hauteur totale du panneau radar) ajoutait un `+16px` FIXE pour
le pied de page, jamais mis à jour quand la taille de police du texte a été rendue proportionnelle
au radar (§17.1) — à la taille de police max (radar à 230px), le texte ne tenait plus sur une ligne
dans cet espace fixe, IMGUI le repliait sur 2 lignes qui se chevauchaient. **Fix** : police dédiée du
pied de page (`footerFontSize`, plafonnée BIEN PLUS BAS que l'en-tête — 14 max contre 20 — puisque
son texte est plus long) et `footerBandHeight` calculée à partir de cette police plutôt qu'une
valeur fixe.

### 17.3 Bug trouvé pendant les tests réels : bannière équipe/minuteur qui aurait chevauché le radar agrandi

`InMatchHudScreen.uxml` dégageait la bannière ÉQUIPE BLEUE/minuteur sous le radar via un
`padding-top: 180px` FIXE, calibré sur l'ANCIEN radar (120px) — resté inchangé pendant tout le
travail du §17.1, serait devenu insuffisant une fois le radar réellement agrandi en jeu (jusqu'à
230+ px de haut). **Fix** : `MultiplayerMatchController.RefreshHudDynamicFields()` recalcule ce
padding à chaque frame depuis la vraie hauteur du radar (nouvelle propriété statique
`TacticalRadarUI.BottomEdgeVirtualY`, dans l'espace UI Toolkit — distincte de `BottomEdgeScreenY`,
déjà existante mais en pixels écran réels, PAS la bonne échelle pour un style UI Toolkit). Le
`180px` dans le fichier `.uxml` reste comme simple repli d'une fraction de frame au tout premier
affichage — ne jamais l'ajuster pour "corriger" un chevauchement, corriger le calcul C#.

### 17.4 Mode portrait/paysage — cause racine trouvée : le manifeste Android forçait une orientation restreinte

Réglages Unity déjà corrects et vérifiés (`ProjectSettings.asset`) : `defaultScreenOrientation: 4`
(AutoRotation) + les 4 `allowedAutorotateToX: 1`. Pourtant `aapt dump xmltree` sur l'APK généré
montrait `android:screenOrientation` figé à une valeur restreinte (`0xd`), et forcer la rotation au
niveau OS (`adb shell settings put system user_rotation`, puis la commande console
`adb emu rotate`) restait sans AUCUN effet visible, même le `rotation` rapporté par
`dumpsys display` restait à 0 — confirmant que le blocage est bien au niveau de l'attribut du
manifeste, pas un souci d'émulateur qui ignore une entrée sensor.

Cause exacte non identifiée avec certitude dans les réglages PlayerSettings de cette version
d'Unity (6000.5.8f1, entrée Android "GameActivity" — `androidApplicationEntry: 2`) : essayé
`androidResizeableActivity: 0` (aucun effet sur `screenOrientation`, confirmé par une nouvelle
recompilation et un nouveau `aapt dump`) et `androidAutoRotationBehavior: 0` (champ dont la
sémantique exacte n'a pas pu être confirmée). Plutôt que de continuer à deviner un champ non
documenté dans `ProjectSettings.asset`, **fix appliqué au niveau du manifeste généré lui-même** :
nouveau `Assets/Editor/AndroidManifestOrientationFix.cs`
(`IPostGenerateGradleAndroidProject`, s'exécute après la génération du projet Gradle, avant
l'assemblage final de l'APK) qui force `android:screenOrientation="fullSensor"` sur l'activité
principale — les 4 orientations, jamais restreint par le verrou de rotation système (contexte jeu
tactique plein écran, pas un souci pour un joueur qui verrouille son écran en portrait).

**Vérifié au niveau du fichier** (`aapt dump xmltree` sur l'APK final montre bien
`android:screenOrientation=(type 0x10)0xa`, soit `fullSensor`) — **pas vérifié visuellement en jeu**,
faute d'un émulateur dont la rotation (OS et GameActivity) répond aux commandes disponibles en
ligne de commande dans cette session. `fullSensor` est un comportement Android standard et
documenté ; à confirmer sur un vrai téléphone au prochain test.

### 17.5 Confirmé fonctionnel en conditions réelles (captures d'écran à l'appui, pas une simulation)

- Connexion (`testlille1@novgov.test`) puis file d'attente Deathmatch → bouton "🤖 JOUER CONTRE L'IA
  EN ATTENDANT" apparaît après le délai prévu, bien formaté, lisible.
- Partie d'entraînement contre l'IA (§15) jouée de bout en bout depuis l'appareil : déploiement,
  dock QG RENFORTS bien positionné (aucun chevauchement avec le radar agrandi), match résolu,
  4 alliés déployés, radar affichant les contacts en temps réel.
- Écran de connexion, sélection de mode, et matchmaking tous rendus correctement en portrait sur
  les deux résolutions testées (1080×2400 et 1080×1920).

**Bug d'automatisation de test (pas un bug du jeu), noté pour la prochaine session** : le clavier
virtuel Android autocorrige/tronque parfois la saisie via `adb shell input text` sur les champs
email/mot de passe (un premier essai de connexion a échoué avec "Invalid login credentials" alors
que les identifiants étaient corrects, confirmé indépendamment par un appel direct à l'API GoTrue) —
toujours vérifier le contenu réel d'un champ (bouton "Afficher" pour le mot de passe) avant de
soumettre un formulaire de test automatisé.

**État du dernier build testé en direct (`android_build4`, actuellement installé sur l'émulateur
`Medium_Phone_API_36.0`)** : contient BIEN les 4 correctifs de ce §17 (radar agrandi, chevauchement
pied de page §17.2, padding dynamique §17.3, manifeste `fullSensor` §17.4) — le pied de page du
radar sur une seule ligne propre a été reconfirmé visuellement sur cet appareil APRÈS ce build.
**Reste à faire avant la prochaine session** : un vrai test de rotation sur un appareil physique ou
un émulateur dont les commandes de rotation fonctionnent réellement (seul point du §17 jamais
confirmé visuellement, uniquement au niveau du fichier manifeste).

## 18. Session du 2026-09-04/05 — chasse aux régressions "jouabilité cassée" + équité géométrique multijoueur

**Contexte** : le joueur a rapporté après la session du 2026-09-03 (§ précédent implicite, roof/
mouvement) "tu as cassé la jouabilité et la sélection des unités", puis "j'ai des doutes, il y a des
bugs" après une première vague de correctifs. Deux audits successifs (14 correctifs + vérification
adversariale, puis 6 nouveaux bugs sur les dimensions jamais couvertes) ont été menés — détail complet
dans la conversation de session, pas reproduit ici pour ne pas dupliquer ; retenir seulement le plus
grave : un ordre "Garnison Fenêtre" sur une fenêtre d'étage (≥1) était intercepté par le code
d'escalade et envoyait le soldat sur le toit au lieu de le mettre en garnison à couvert
(UnitAI_Movement.cs, isClimbUp/branche toit n'excluaient pas NodeAction.GarnisonFenetre). Une notion
d'attente réelle ("ATTENDRE 30 SECONDES") a aussi été ajoutée au moteur pur multijoueur
(PathCheckpoint.waitSeconds, TacticalResolver.Resolve) — elle n'existait auparavant que côté solo,
l'action tombait dans le cas par défaut du switch serveur et ne faisait rien de plus qu'un simple
déplacement en ligne. 55+ tests ajoutés au harnais hors-éditeur pour tout ce qui précède.

**Question posée ensuite par l'utilisateur, sujet principal de ce §18** : « comment les joueurs
reçoivent les cartes et comment elles sont élaborées en bâtiments ? Je veux que les deux joueurs
aient les mêmes polygones/bâtiments/disposition, cette vérification devrait être au niveau serveur. »

**Diagnostic confirmé (lecture complète de `CityGenerator.cs`, `MatchSessionManager.cs`,
`ZoneManager.cs`, `GeoProjection.cs`, `NetMessage.cs`, `docker-compose.yml`)** — l'architecture des
Zones de Conquête (§11) avait un trou d'équité connu mais jamais corrigé, documenté noir sur blanc
dans son propre commentaire (`MatchSessionManager.cs`, ancien commentaire de `RunConquestSkirmish`) :
la géométrie d'une vraie tuile GPS est chargée « à la fois côté serveur et côté client via le même
(tileX,tileY), **jamais transmise sur le réseau** » — chacun refait sa PROPRE requête Overpass
indépendante, en pariant que « même tuile + même code déterministe ⇒ même résultat ». Trois failles
concrètes identifiées :

1. **Aucune vérification ni transmission** : le client (`ZoneManager.LoadZone` →
   `CityGenerator.GenerateCity()`) et le serveur (`MatchSessionManager.LoadZoneOnServer`, même
   appel) interrogent chacun Overpass, potentiellement deux miroirs différents parmi les 3 de repli
   (`overpass.openstreetmap.fr`/`lz4.overpass-api.de`/`overpass-api.de`, pas garantis synchronisés),
   sans aucune garde-fou si les deux réponses diffèrent (édition OSM entre les deux appels, retard de
   réplication). Aggravant : `CityGenerator.FetchCityData` écrivait un cache disque par tuile
   (`CityCache_Z17_{x}_{y}.json`) mais ne le relisait **jamais** — chaque combat sur une vraie tuile
   déclenchait donc systématiquement un nouvel appel réseau, des deux côtés, à chaque fois.
2. **Mobilier urbain non déterministe avec colliders réels** (`StreetPropsGenerator.cs`) :
   lampadaires/arbres/bancs/poubelles placés via `Random.value`/`Random.Range` non seedé, intégrés au
   bake NavMesh Unity (`CityGenerator.cs`, `surface.useGeometry = PhysicsColliders`) — deux
   générations du même JSON produisaient un mobilier différent en nombre et position. Sans
   conséquence sur la résolution AUTORITAIRE d'un tour (`TacticalGridBuilder.BuildFromScene` ne lit
   que `BuildingStructure.AllBuildings`, jamais les props), mais bien une vraie différence de
   "disposition" visible/NavMesh entre deux joueurs — exactement ce que la question posait.
3. **Hauteur de toit par hash trigonométrique** (`DeterministicLotHeight`/`JitterBuildingColor`,
   `Mathf.Sin(...) * grand_facteur` puis `Mathf.Floor`) : déterministe sur UNE plateforme donnée, mais
   `sin()` n'est pas garantie bit-identique par IEEE754 entre la libm Android/ARM du client et la
   glibc Linux du serveur dédié — un écart d'un seul bit, amplifié par le facteur, pouvait en théorie
   faire basculer `Floor()` d'une unité et changer la hauteur d'un toit (donc la position Y d'un
   tireur perché) de ~3m entre client et serveur, pour le MÊME bâtiment.

**Correctifs appliqués — le serveur devient la seule autorité géométrique, comme demandé** :

- **`Novgov.Core.DeterministicHash`** (nouveau, `Assets/Scripts/Core/DeterministicHash.cs`) : hash
  entier pur (FNV-1a + finalisateur type MurmurHash3, uniquement XOR/shift/multiplication sur des
  `uint` 32 bits) remplaçant le hash trigonométrique — bit-identique sur TOUTE plateforme .NET/Mono/
  IL2CPP, contrairement à `Mathf.Sin`. `DeterministicLotHeight`/`JitterBuildingColor` l'utilisent
  maintenant. 4 tests dédiés (déterminisme, plage [0,1), distribution, insensibilité à un bruit
  flottant sous le millimètre).
- **`UnityEngine.Random` réamorcé déterministement** (`CityGenerator.ProcessDataCoroutine`, tout au
  début) : seed dérivé de `zoneTileX`/`zoneTileY` (jamais de `string.GetHashCode()`, non garanti
  stable entre process/plateformes) pour une vraie Zone, constante fixe pour "Default". Couvre les
  tirages de matériaux ET `StreetPropsGenerator` — déterministe car l'ORDRE et le NOMBRE d'appels à
  `Random` qui suivent sont une fonction pure du JSON, désormais garanti identique des deux côtés
  (point suivant).
- **`CityGenerator.LoadZoneFromServerData(tileX, tileY, json)`** (nouveau, remplace l'appel côté
  client à `GenerateCity()`/Overpass pour un VRAI match) : rejoue le JSON fourni par le serveur dans
  EXACTEMENT le même pipeline que `FetchCityData` (extrait dans `FinishZoneLoadFromJson`, partagé par
  les trois origines de JSON — réseau direct, cache disque, serveur). Repli explicite et bruyamment
  journalisé sur l'ancien comportement (requête Overpass locale indépendante) si le serveur n'a
  exceptionnellement rien fourni.
- **Cache disque de tuile enfin relu** (`CityGenerator.TryReadZoneCacheFromDisk`, appelé en tête de
  `FetchCityData` avant toute requête réseau) — et **relocalisé** dans le même sous-dossier persistant
  que `TacticalGridBuilder` (`TacticalGridCache/`, déjà monté sur le volume Docker nommé
  `tactical-grid-cache`) au lieu de la racine de `Application.persistentDataPath` (effacée à chaque
  redéploiement du conteneur) — survit maintenant aux redéploiements, sans le moindre changement de
  `docker-compose.yml`.
- **`NetMessage.city_data_json`** (nouveau champ) + **serveur** (`MatchSessionManager.cs`, les deux
  sites d'envoi de `match_found` — Deathmatch/Zone de Contrôle ET Conquête) : lit le JSON qu'il vient
  lui-même d'utiliser (`CityGenerator.TryReadZoneCacheFromDisk`, toujours disponible à ce point
  puisque `EnsureTileLoadedAndSnapshot`/`LoadZoneOnServer` ont déjà résolu la géométrie AVANT l'envoi
  de `match_found` dans les deux chemins — vérifié par lecture, aucun changement de séquencement
  réseau nécessaire) et le joint au message. Jamais pour la carte "Default" (bundle Resources
  identique des deux côtés). **`ZoneManager.LoadZoneFromServerData`** + **`MultiplayerMatchController.
  LoadMatchMapThenOpenDeployment`** (nouveau paramètre `serverCityDataJson`) côté client consomment ce
  champ. Documenté dans `03-network-protocol.md`.
- **`TacticalGridBuilder.DiskCacheVersion` : 2 → 3** — trouvaille de l'audit adversarial de ce même
  correctif. Le passage du hash trigonométrique à `DeterministicHash` change la valeur de
  `DeterministicLotHeight` pour un même bâtiment ; sans ce bump, tout cache disque L2 écrit AVANT ce
  correctif aurait continué à servir indéfiniment des hauteurs calculées avec l'ANCIENNE formule
  pendant qu'un client appliquant le `city_data_json` du serveur calcule la NOUVELLE — recréant, par
  la péremption du cache et non par `Sin()`, exactement le bug que ce correctif visait à éliminer.
  Même règle explicitement documentée par le commentaire du v2 précédent (2026-09-03), simplement pas
  appliquée du premier coup cette fois.

**Non traité, signalé au joueur (mobilier urbain)** : le mobilier urbain (lampadaires/arbres/bancs)
reste protégé de tout mélange entre deux générations concurrentes uniquement par un effet de bord non
documenté comme tel (`matchInProgress`, un verrou pensé pour la sécurité de la scène, pas pour le
déterminisme de `UnityEngine.Random`) — sans conséquence aujourd'hui (cosmétique seulement : jamais le
footprint ni la hauteur d'un bâtiment, protégés séparément), mais à surveiller si ce verrou est un jour
assoupli pour scaler la Conquête (voir le paragraphe précédent).

**Non traité, signalé au joueur (triangulation/subdivision)** : le seuil epsilon quasi nul de la triangulation (`Snip`, ear-
clipping) et le tri non stable de `BuildingSubdivider` (égalité exacte de longueur d'arête) restent
des sources résiduelles TRÈS improbables de divergence (diagonales de maillage / découpage parcellaire
rare), non corrigées faute de temps — impact jugé mineur (ne change ni le contour d'un bâtiment ni sa
marchabilité). Autre limite structurelle **déjà connue et explicitement assumée par la session du
2026-08-30** (pas nouvelle, pas traitée ici) : la génération d'une VRAIE tuile jamais vue reste
sérialisée par `matchInProgress` (verrou global du processus serveur unique) — un combat de Conquête
sur une tuile inédite bloque toute AUTRE génération de tuile inédite pendant jusqu'à 60s ; sans
conséquence sur Deathmatch/Zone de Contrôle "Option B" (résolution pure-donnée, aucune scène vivante).
Si la Conquête doit un jour supporter des milliers de tuiles inédites simultanément, ceci nécessiterait
un processus de génération dédié, séparé de la résolution de match — hors budget de cette session.

**Non testé en conditions réelles** (pas d'accès à 2 téléphones/VPS depuis cette session, comme les
sessions précédentes) : compile-check propre sur les 3 configurations (client Android/serveur dédié/
éditeur) + tests unitaires du hash déterministe, mais le flux réseau complet (match_found portant
`city_data_json`, application côté client, absence de divergence visible) n'a pu être vérifié que par
lecture de code, pas par une vraie partie à 2 joueurs.

---

## 19. Session du 2026-09-07/08 — audit complet de la jouabilité multijoueur (10 correctifs + 9 trouvés en revue + retour joueur §19.11)

**Demande** : « comprends le jeu et corrige la jouabilité du mode multijoueur ». Cartographie
complète des 8 sous-systèmes multijoueur (cycle de vie serveur, déploiement, moteur de résolution,
chemins de combat pur/vivant, rejeu client + HUD, saisie des ordres, Conquête/IA, carte +
progression), puis vérification une par une des anomalies relevées AVANT tout correctif — plusieurs
se sont révélées fausses (voir « Signalé mais NON retenu » en fin de section).

### 19.1 Le vrai blocage : une file d'attente silencieuse déconnectait tout le monde en 25 s

**C'est très probablement la cause du « je n'arrive pas à lancer/tester le multijoueur » resté
non résolu au §0 (2026-09-06).** Le client applique un `ReceiveTimeout` FINI de 25 s à sa socket
(`GameServerClient.Connect`) : 25 s sans le moindre octet reçu et son thread de lecture lève, ce qui
le déconnecte avec « connexion perdue ». Or le serveur passait de longues périodes à n'envoyer
**strictement rien** :

- un joueur seul dans `waitingDeathmatch`/`waitingZoneControl` n'était l'objet d'AUCUN envoi tant
  qu'aucun adversaire ne se présentait — **Deathmatch et Zone de Contrôle étaient donc injouables
  dès qu'un second joueur ne rejoignait pas la file dans les 25 secondes** ;
- entre l'appariement et `match_found`, `RunMatch` récupère deux pseudos par HTTP puis peut générer
  une tuile inédite (Overpass + bake NavMesh, jusqu'à ~60 s) sans rien émettre ;
- la Conquête et l'entraînement contre l'IA n'avaient aucun signal de vie du tout.

La phase de déploiement avait bien reçu son propre signal de vie (correctif du 2026-09-06) — mais
LOCAL à cette phase. **C'est l'erreur de conception à ne pas reproduire** : un keepalive par phase
oblige chaque nouvelle phase à y penser, et trois d'entre elles ne l'avaient pas fait.

Le signal de vie est désormais une propriété de la CONNEXION, pas d'une phase :
`PlayerConnection.PumpKeepalives()`, appelé en tête de `MatchSessionManager.Update()`, envoie un
`heartbeat` à TOUTE connexion authentifiée restée silencieuse plus de 8 s
(`KeepaliveIntervalSeconds`, ~1/3 du timeout client : deux trames consécutives peuvent se perdre).
Tout envoi réel repousse d'autant le prochain signal. Le keepalive local au déploiement a été retiré.

### 19.2 Le rejeu des deux modes PvP était amorcé sur son PROPRE résultat

`TacticalUnit` est une **classe**, et `TacticalResolver.Resolve` la mute EN PLACE. Or
`RunExecutionPhasePure` capturait `unitsBeforeResolution` (de simples RÉFÉRENCES) avant la
résolution et s'en servait APRÈS pour amorcer `BuildSnapshotsFromEventsPure`. Après la résolution
ces objets portaient l'état de FIN de tour : chaque tour de Deathmatch/Zone de Contrôle était donc
rejoué à partir de son propre résultat.

- le snapshot `t=0` plaçait déjà chaque unité à sa position FINALE, puis le premier événement `Move`
  la ramenait brutalement en arrière pour la faire re-marcher ;
- les PV étaient amorcés à leur valeur d'APRÈS combat, puis chaque événement `Shot` retranchait ses
  dégâts une SECONDE fois — barres de vie fausses toute la partie ;
- une unité tuée ce tour-ci était marquée morte dès la première image du rejeu, avant même le tir
  qui la tue.

Correctif : nouvelle struct `UnitTurnStart`, copie PAR VALEUR prise avant `Resolve`. Le chemin
« vivant » (Conquête/entraînement) n'était PAS touché — il résout sur un `TacticalWorldState`
distinct des vraies `UnitAI` dont il amorce le rejeu.

### 19.3 Entrer dans un bâtiment ne fonctionnait jamais en multijoueur

`CityGenerator` génère une porte à `outwardNormal * 0.05f` de sa façade — donc **5 cm en dehors** de
l'empreinte (et `DoorInteraction.GetOutsidePosition` 1.2 m de plus). Le `PointInPolygon` STRICT de
`MatchGeometry.FindBuildingAt` renvoyait donc systématiquement -1 pour un point de porte :

- `checkpoint.enterBuildingId` restait à -1 : l'unité n'entrait JAMAIS. Tout le mécanisme
  d'entrée/sortie corrigé le 2026-09-06 (tests à l'appui) était correct mais **inatteignable**, car
  rien ne lui fournissait jamais un identifiant de bâtiment valide ;
- GUETTER PAR LA PORTE posait `isGarrisoned = true` (-75 % de dégâts subis) SANS rattachement de
  bâtiment — exactement ce que le rattachement était censé empêcher : un soldat planté dans la rue,
  en couverture maximale, impossible à toucher en retour.

Pourquoi les 5 tests d'entrée du 2026-09-06 ne l'ont pas vu : ils plaçaient tous leur `doorPoint`
1 m À L'INTÉRIEUR de l'empreinte (`Vector2(31, 6)` pour un bâtiment de x=30 à x=42). Le moteur était
testé sur une entrée qui ne se produit jamais telle quelle en jeu.

Correctif en trois points : `MatchGeometry.FindBuildingAtOrNear` (+ son jumeau de scène
`BuildingStructure.FindBuildingAtOrNear`) rattache un point au bâtiment le plus proche dans un rayon
de `DoorAttachToleranceMeters = 2 m` ; `GeometryMath.SqrDistancePointToSegment`/
`SqrDistanceToPolygonEdge` (arithmétique pure, aucune trigonométrie) le rendent possible ; et le
franchissement du seuil pose maintenant l'unité DEDANS (`NearestCellInsideFootprint`) au lieu de la
laisser sur le point de porte, géométriquement dehors tout en étant marquée « à l'intérieur ».

### 19.4 Les autres correctifs

- **`x` perdu dans le découpage du 2026-09-06** (`MatchSessionManager_CombatLive.CaptureTacticalSnapshot`) :
  le commit f458e65, annoncé « aucun changement de comportement », avait supprimé `x = p.x`. En
  Conquête et en Entraînement, le rejeu ramenait donc **toutes** les unités sur la ligne x=0. Ligne
  restaurée. Un audit systématique (monolithe d'avant le découpage vs les 7 fichiers actuels) n'a
  révélé aucune autre perte.
- **`PlanningSeconds` 60 s -> 300 s** : le 2026-09-06, `DeploymentSeconds` et `MapReadyMaxWaitSeconds`
  étaient passés à 300 s « même demande/même raison » que le retrait de l'affichage du compte à
  rebours — mais pas la planification, de loin la phase qui demande le plus de travail au joueur. Un
  joueur dépassant 60 s voyait, SANS avertissement possible (le minuteur n'est plus affiché, à sa
  demande explicite) : ses unités ne recevoir aucun ordre, son adversaire notifié qu'il avait
  « ghosté », et tous ses tracés effacés. **Le minuteur reste masqué** — c'est le plafond qui devient
  généreux, pas l'affichage qui revient.
- **Aucun moyen d'annuler un point sur un téléphone** : `RemoveLastTacticalNode` n'avait qu'UN seul
  appelant, la branche `Mouse.current.rightButton` — donc rien sur la plateforme cible. Un checkpoint
  mal placé était définitif pour le tour. Ajout d'un bouton « ↶ » (`undo-node-button`) dans la barre
  tactique, visible dès que l'unité sélectionnée a au moins un point ; logique factorisée dans
  `TacticalPathManager.AnnulerDernierPoint()`, partagée avec le clic droit.
- **Marqueurs holographiques orphelins** : annuler un point raccourcissait la ligne bleue mais
  laissait son marqueur au sol jusqu'au lancement du tour — le joueur croyait son checkpoint encore
  posé. Les marqueurs portent maintenant `owner`/`nodeIndex` et sont détruits avec leur nœud.
- **Soft-lock permanent du rejeu** : `PlaySnapshotsCoroutine` posait `isPlayingSnapshots = true` sans
  `try/finally`. N'importe quelle exception (un `SpawnUnitAt` qui renvoie null, deux unités de même
  nom faisant lever `ToDictionary`) laissait le verrou bloqué : le client ignorait DÉFINITIVEMENT
  tous les tours suivants, figé sur « en attente de l'adversaire », pendant que le serveur le
  fantômisait à chaque tour. Corps délégué à `PlaySnapshotsBody`, verrou relâché dans un `finally`.
- **Fausse « connexion perdue » après une Conquête instantanée** : le serveur referme la socket juste
  après `zone_captured`/`zone_attack_result` (`attacker.Close()`), fermeture NORMALE que le client
  traitait comme une panne — il renvoyait au menu avec « Connexion au serveur perdue » une frame
  après l'ouverture du panneau de résultat, que le joueur n'avait donc jamais le temps de lire.
  Nouveau drapeau `expectingCloseAfterZoneResult`.
- **Téléportation de 50 m après chaque apparition d'unité** : `AutoCheckNavMeshCoroutine` appelle
  `OnNavMeshReady()` 0.2 s après chaque spawn (et `CityGenerator` la rappelle sur TOUTES les unités
  après chaque bake), ce qui réactivait le `NavMeshAgent` — que le multijoueur désactive
  délibérément — puis appelait `agent.Warp()` vers le point de NavMesh le plus proche **dans un rayon
  de 50 mètres**. Seconde cause, indépendante et jamais identifiée, du symptôme déjà signalé
  « unités affichées ailleurs qu'à leur position réelle » (§0), et cause des unités qui « glissent »
  sans animation de marche (l'agent réactivé lutte contre les positions envoyées par le serveur).
  `OnNavMeshReady` sort maintenant immédiatement, agent désactivé, si `IsFlowActive`.
- **Budget de déplacement enfin visible** : le serveur tronque le chemin au budget de l'unité et
  SUPPRIME tout checkpoint au-delà de la coupe (`TruncateToMovementBudget`) — la posture finale
  (GUETTER, garnison, SE CACHER) était donc purement annulée si elle était hors de portée, sans que
  rien ne le dise au joueur. La portion hors budget de la ligne bleue est maintenant tracée en rouge.
  Le barème (42 char / 46 canon / 34 mortier / 50 fantassin) est désormais une SOURCE UNIQUE
  (`UnitTypeStats.InferType`/`MovementBudgetFor`), lue par le serveur ET par l'aperçu client — une
  table dupliquée qui divergerait recréerait exactement le bug qu'elle rend visible.

### 19.5 Outillage : le compile-check ne compilait plus 12 fichiers

Les trois `*.check.csproj` contiennent une liste EXPLICITE de fichiers générée par Unity. Elle était
**périmée** : `SupabaseDatabaseClient.cs`, `Core/DeterministicHash.cs`, `Core/VectorSentinel.cs`, les
6 `MatchSessionManager_*.cs` issus du découpage et 6 fichiers de tests n'étaient plus compilés du
tout — le compile-check passait « au vert » sans les voir. Remplacée par des globs
(`Tools/globify_check_csproj.py`), qui ne peuvent pas se périmer.

Le harnais de tests hors-éditeur (shim `UnityEngine` + compilation de `Novgov.TacticalCore` avec le
SDK .NET livré avec Unity) avait été reconstruit de zéro à trois sessions différentes parce qu'il
vivait dans un dossier temporaire. Il est désormais **versionné** dans `Tools/TacticalCoreTests/`,
hors de `Assets/` (donc invisible pour Unity). Une seule commande :

```
.\Tools\run-tests.ps1              # 74 tests + compile-check des 3 configurations
.\Tools\run-tests.ps1 -TestsOnly   # tests seuls (~5 s)
```

`TacticalGridBuilder.cs` est exclu du harnais (seul pont vers la scène vivante, intestable hors
Éditeur) ; sa compilation reste couverte par les 3 compile-checks.

### 19.6 Revue adversariale des correctifs eux-mêmes — 9 défauts trouvés et corrigés

Les correctifs ci-dessus ont ensuite été relus de façon adversariale (« que casse ce diff ? »), avec
reproduction empirique. **Neuf défauts ont été trouvés dans le travail de cette session même**, tous
corrigés :

- **Le `try/finally` du rejeu ne rattrapait rien.** `try { yield return PlaySnapshotsBody(msg); }
  finally { … }` est un piège : Unity déroule lui-même l'itérateur imbriqué, donc une exception dans
  son `MoveNext()` ne repasse jamais par la machine à états de l'appelant — le `finally` n'est émis
  que dans son `Dispose()`, que Unity n'appelle pas sur une coroutine avortée. Remplacé par un
  pompage manuel de l'itérateur dans un `try/catch`, exactement comme
  `MatchSessionManager.RunMatchGuarded` le fait déjà côté serveur et pour la même raison.
- **Relâcher `isPlayingSnapshots` ne suffisait pas** : la fin de `PlaySnapshotsBody` remet aussi
  `phaseActuelle` à `Planification`, et `TacticalPathManager.Update` sort immédiatement tant qu'elle
  vaut `Execution`. Une exception laissait donc le joueur incapable de sélectionner une unité ou de
  poser un point, verrou relâché ou non. Nouvelle méthode `RecoverFromFailedReplay()` qui rétablit
  l'état JOUABLE complet.
- **Le garde `IsFlowActive` de `OnNavMeshReady` cassait le mode SOLO.** `IsFlowActive` est vrai pour
  TOUT écran multijoueur, menus de login/choix de mode compris, et plusieurs retours au menu de
  démarrage ne repassent jamais l'état à `Hidden`. Une partie solo lancée après un simple passage par
  le menu multijoueur trouvait toutes ses unités avec un agent désactivé, donc immobiles. Garde
  resserré à `IsActive || IsDeploymentPhaseActive`.
- **Le franchissement de seuil n'était pas atomique face à la troncature de budget** : si le budget
  expirait au milieu du pas, la coupe était interpolée DANS l'empreinte pendant que le checkpoint
  porteur du rattachement était supprimé — l'unité se retrouvait géométriquement dedans et
  logiquement dehors, l'incohérence même que ce correctif visait à supprimer. Nouvel ensemble
  `ExpandedOrder.atomicStep`, traité comme une transition verticale.
- **Le repli de `NearestCellInsideFootprint` pouvait renvoyer un point HORS de l'empreinte** pour un
  bâtiment plus petit qu'une cellule d'1 m. On n'entre plus du tout dans ce cas dégénéré.
- **GUETTER PAR LA PORTE faisait désormais ENTRER l'unité** : rendre `enterBuildingId` résoluble l'a
  aussi rendu visible à `ExpandOrder`, qui l'interprète comme « franchis le seuil ». Le rattachement
  (pour la couverture et la ligne de vue) a donc été séparé de l'entrée : nouveau champ
  `PathCheckpoint.attachBuildingId`.
- **Les deux appels d'overwatch de porte utilisaient encore la recherche stricte**, deux lignes
  au-dessus du site corrigé : le déclencheur de guet en LIGNE DE PORTE restait 100 % du code mort.
- **Les marqueurs s'auto-détruisaient au bout de 60 s** alors que la planification en dure désormais
  300 : un joueur qui réfléchit voyait ses hologrammes disparaître un à un. Auto-destruction retirée
  (cycle de vie désormais explicite), et un marqueur devenu orphelin (unité tuée) se ramasse seul.
- **La coloration de budget mesurait en 3D une contrainte serveur mesurée en 2D** : une escalade sur
  un toit de 9 m gonflait le total d'autant, donc la coupe rouge était dessinée PLUS LOIN que la
  vraie — l'aperçu promettait plus de chemin qu'il n'y en a. Mesure ramenée au sol (XZ).

La revue a aussi confirmé qu'aucun défaut n'a été trouvé dans le keepalive (hors le point ci-dessous),
dans `UnitTurnStart`, dans les primitives de `GeometryMath`, dans le mappage `checkpointAtStep`, ni
dans `expectingCloseAfterZoneResult`. Un point mineur reste **non corrigé et assumé** : une connexion
authentifiée qui n'envoie jamais `join_matchmaking` n'est plus jamais récoltée (le keepalive maintient
la socket vivante des deux côtés) — fuite lente d'un socket par client bloqué, sans impact en usage
normal.

### 19.7 Tests ajoutés (64 -> 74)

- `TacticalCoreSelfTest_DoorEntry.cs` (7 tests). La revue a montré que la première version ne
  pinglait presque rien : la recherche tolérante vivait dans `Assets/Scripts/Server`, que le harnais
  ne compile pas, donc revenir au test strict laissait tous les tests verts. Elle a donc été
  **déplacée dans `GeometryMath.FindBuildingAtOrNear`** — c'est de la géométrie pure sur des données
  pures, sa place est dans le moteur pur — et `MatchGeometry` s'y délègue. **Contrôle négatif
  refait** : remettre la recherche stricte fait bien échouer 3 tests (rattachement d'une vraie porte,
  du seuil extérieur, et déterminisme à égalité exacte), plus celui du franchissement.
- `TacticalCoreSelfTest_ResolveMutatesInPlace.cs` (3 tests). Le troisième test était tautologique
  (il copiait des `Vector2`/`int` locaux et vérifiait qu'ils n'avaient pas changé, ce que la
  sémantique de valeur de C# rend impossible à faire échouer). Remplacé par une démonstration de la
  DIVERGENCE réelle : le même tour amorcé depuis une copie par valeur montre le départ, amorcé
  depuis les références montre déjà l'arrivée, et les deux diffèrent bel et bien.

### 19.8 Signalé mais NON retenu après vérification

Consigné pour qu'une session future ne les re-« corrige » pas :

- **« Une action ATTENDRE 30 SECONDES fait exploser la durée du rejeu / atteint le plafond de
  3200 ticks »** : FAUX. Les snapshots ne sont émis que pour les ticks PORTANT UN ÉVÉNEMENT
  (`events.Select(e => e.tick).Distinct()`), donc une attente silencieuse ne coûte aucun snapshot.
  L'effet réel est inverse et mineur : le client rejoue chaque snapshot à 250 ms fixes en ignorant
  l'horodatage `t`, donc une attente est visuellement compressée, pas étirée.
- **« Le compte à rebours de planification est caché au joueur »** : c'est un CHOIX EXPLICITE du
  joueur (2026-09-06, « enlève le temps dans tous les états, ne stresse pas le joueur »). Ne pas
  réafficher de minuteur — c'est le plafond qui a été rendu généreux (§19.4).

### 19.9 Reste à faire — priorisé

Rien de ce qui suit n'a été touché. Par ordre de gravité pour le joueur :

1. **Conquête sans issue après la première capture** : `ZoneManager.ExpandNorth/South/East/West`
   n'ont aucun appelant et rien n'avance `CurrentTileX/Y` après une capture, donc les 4 boutons
   d'attaque se bloquent définitivement ; en prime, une partie PvP sur une vraie tuile déplace
   `CurrentTileX/Y` vers la tuile de l'adversaire.
2. **Aucun retour visuel de combat pendant le rejeu** : `SetNetworkHealth` court-circuite
   `TakeDamage` (donc impacts/sang/étincelles) et `ShootAt` est désactivé en réseau (donc traçantes,
   flash, son, ping radar). Les unités meurent sans que rien ne soit visible.
3. **La destruction de bâtiment n'est jamais transmise** : `WallDestroyed` n'a aucun consommateur —
   le bâtiment reste debout sur les deux écrans alors que le serveur le sait détruit.
4. **Grille tactique MUTABLE partagée entre parties concurrentes sur la même tuile** : détruire un
   bâtiment dans une partie ouvre le mur dans les autres.
5. **`mortarStrikes` n'est validé par rien** : ni type d'unité, ni portée. Un client modifié fait
   pleuvoir 150 dégâts n'importe où avec n'importe quelle unité.
6. **Postures à sens unique** : `isGuarding`/`isCamouflaged`/`isGarrisoned` ne sont jamais remis à
   false — un GUETTER au tour 1 vaut -50 % de dégâts subis pour toute la partie, même en courant à
   découvert (comportement « fidèle à l'original » mais très déséquilibré à deux joueurs).
7. **`ClampToDeploymentZone` est devenu la fonction identité** (désactivée le 2026-09-06 sur demande) :
   plus aucune borne de coordonnées, un client modifié peut déployer au contact ou hors carte.
8. **Économie entièrement en PlayerPrefs locaux** : réinstaller remet à zéro, et un compte a un
   portefeuille différent par téléphone.
9. **`practice_ai` est inatteignable depuis le client** (aucun bouton) alors que tout le mode existe
   côté serveur.

### 19.10 Non vérifié en conditions réelles

Comme les sessions précédentes : 74 tests verts, compile-check propre sur les 3 configurations
(client Android / serveur dédié / éditeur), **mais aucun build Unity réel produit ni déployé, et
aucune partie à 2 joueurs jouée** dans cette session. Les correctifs §19.1 (keepalive) et §19.2
(amorçage du rejeu) sont ceux qui changent le plus le comportement observable et méritent d'être
confirmés en premier par un vrai test à 2 clients.

### 19.11 Retour joueur (2026-09-08) — le déploiement remplaçait silencieusement les mortiers/positions choisis

**Rapporté par le joueur** : « il y a toujours de l'IA dans le multijoueur alors qu'on a dit pas
d'IA, aussi au début c'est le joueur qui doit acter le commencement du jeu pas automatiquement, et
pourquoi tu as enlevé le droit de déployer les mortiers, et toutes les unités se mettent toutes
seules dans des endroits bizarres après déploiement alors que le joueur avait choisi d'autres
endroits ». Quatre plaintes, une seule cause racine confirmée pour trois d'entre elles.

**Cause racine confirmée et corrigée** : `IsRosterValid` (introduit le 2026-09-06 avec le budget
en points, voir §19.4 de l'époque — `CombatPointBudget = 8`, coût 1/2/2/3 selon Fantassin/
VehiculeCanon/Mortier/CharLeopard) était TOUT OU RIEN — au moindre dépassement, `ResolveDeployment(
Pure)` jetait la soumission ENTIÈRE et la remplaçait par `AutoDeployTeamFallback(Pure)`, une
escouade FIXE (2 Fantassin + 1 CharLeopard + 1 Mortier) à des positions FIXES ancrées sur un coin
de la carte — sans le moindre rapport avec ce que le joueur avait réellement tapé, et sans le
moindre message d'erreur. Or le dock de déploiement (`OpenDeploymentDock`, `maxUnitsPerTeam = 4`)
n'a JAMAIS connu ni affiché ce budget en points — seulement un nombre d'unités. Une composition
tout à fait raisonnable et sous la limite affichée (ex. 2 CharLeopard + 1 Mortier + 1 Fantassin =
6+2+1 = 9 points, pour une limite de 4 unités mais 8 points) déclenchait donc le remplacement
intégral. Explique directement :
- **« pourquoi as-tu enlevé le droit de déployer les mortiers »** : pas littéralement enlevé — mais
  toute composition qui EN CONTENAIT dépassait facilement les 8 points, faisant remplacer tout le
  déploiement (mortier inclus) par l'escouade de repli (qui n'en a qu'UN, fixe) ;
- **« les unités se mettent toutes seules dans des endroits bizarres »** : les positions de repli
  sont fixes, ancrées sur (-25,-25)/(25,25), jamais celles tapées par le joueur ;
- probablement **« le joueur doit acter le commencement, pas automatiquement »** : le joueur clique
  bien CONFIRMER (vérifié — `SubmitLocalDeployment` n'a aucun déclenchement automatique/minuté),
  mais le résultat affiché ne correspond à rien de ce qu'il vient de faire, ce qui se vit comme
  « le jeu a décidé tout seul ».

**Correctif** : `FilterRosterToBudget` remplace `IsRosterValid` — garde EXACTEMENT les placements
du joueur (même type, même position) qui tiennent dans le budget, dans l'ordre de soumission, et
n'écarte QU'un placement individuel invalide ou qui ferait dépasser une limite — jamais la
soumission entière. Le repli fixe ne s'applique plus que si RIEN du tout n'a pu être conservé
(aucune soumission, ou entièrement malformée). Un nouveau champ `deployment_result.reason =
"roster_trimmed"` prévient le client quand un écart partiel a eu lieu (`MultiplayerMatchController.
OnDeploymentResult` affiche un message). Prévention en plus, côté client : le dock affiche
maintenant "Effectifs : X / 4 • Points : Y / 8" pendant le placement PvP (`UnitSpawnerUI.
GetTeamDeploymentPointCost`), pour que le joueur voie la limite AVANT de confirmer, pas après.

**« il y a toujours de l'IA dans le multijoueur »** — PAS résolu, cause non identifiée avec
certitude. Vérifié dans le code actuel (2026-09-08) :
- Deathmatch/Zone de Contrôle (chemin pur) n'invoquent JAMAIS `TacticalAIPlanner` — un adversaire
  absent "tient la position" sans aucune décision d'IA (`ApplyForPlayerPure`) ;
- l'offre "jouer contre l'IA en attendant" (bouton + minuteur dans la file d'attente) a déjà été
  retirée le 2026-09-06 (voir commentaire `MultiplayerMatchController.cs` ligne ~135) — plus aucun
  bouton, plus aucune mention "IA" dans `WaitingScreen.uxml` ;
- `opponent_username` dans `match_found` est toujours le VRAI pseudo de l'adversaire humain pour
  Deathmatch/Zone de Contrôle, jamais un nom générique "IA".
- La Conquête (et l'Entraînement) restent, PAR CONCEPTION, un joueur seul contre une garnison IA —
  ce n'est pas un bug si c'est le mode testé.

Deux explications restent possibles et n'ont pas pu être départagées sans plus d'information : (a)
le joueur testait la Conquête/l'Entraînement en pensant à du PvP réel, ou (b) le client testé est
un APK antérieur au 2026-09-06 (aucun nouveau build Android n'a été produit ni distribué depuis
cette session-ci ni, à confirmer, depuis le 2026-09-06). À reprendre avec confirmation du mode
testé ET d'un build fraîchement installé avant de chercher plus loin.

74 tests toujours verts, compile-check propre sur les 3 configurations. **Toujours aucun build ni
déploiement réel cette session.**

### 19.12 Trouvé : la bannière "IA de secours" mentait pour Deathmatch/Zone de Contrôle

**Confirmé par le joueur** : le mode testé était bien Deathmatch/Zone de Contrôle (pas la
Conquête). Root cause trouvée — pas une hypothèse cette fois, une chaîne de caractères
littéralement fausse affichée au joueur.

`MultiplayerMatchController.OnOpponentGhosted` affichait, INCONDITIONNELLEMENT et quel que soit le
mode : *"Vous étiez absent — une IA de secours a joué vos unités ce tour-ci."* (ou la version
adversaire). Ce texte décrit fidèlement le chemin "vivant" (`ApplyForPlayer`, Conquête/
Entraînement, qui appelle réellement `TacticalAIPlanner.PlanifierTourIA()`) — mais **pas du tout**
le chemin pur (`ApplyForPlayerPure`, Deathmatch/Zone de Contrôle), dont le commentaire dit
noir sur blanc depuis le 2026-08-30 : "plutôt que TacticalAIPlanner... ses unités TIENNENT LA
POSITION" — aucune IA n'y tourne jamais. Un vrai match PvP affichait donc, à CHAQUE tour manqué
par l'un ou l'autre camp (soi ou l'adversaire), une bannière affirmant explicitement qu'une IA
venait de jouer — alors qu'il ne s'était rien passé de plus qu'une unité immobile. Explique le
signalement mot pour mot : « il y a toujours de l'IA dans le multijoueur alors qu'on a dit pas
d'IA ».

**Corrigé** : le texte distingue maintenant les deux moteurs via `currentMode` (déjà connu du
client depuis `match_found.mode`, aucun nouveau champ réseau) — "deathmatch"/"zone_control" ->
"vos/ses unités ont tenu leur position" (vrai), "conquest"/"practice_ai" -> le texte IA d'origine
(toujours vrai pour ces deux-là).

**Diagnostic ajouté pour la prochaine fois** : `ApplyForPlayerPure` était totalement silencieuse —
aucun moyen de distinguer après coup un vrai AFK/déconnexion d'un joueur ghosté À TORT par un bug
(ex. désynchronisation du numéro de tour). Un `Debug.Log("[Ghost] ...")` explicite trace désormais
chaque ghosting avec `IsDisconnected`/`HasSubmittedThisTurn`, consultable dans les logs du
conteneur `game-server` sur le VPS.

**Ce qui reste possible et n'est PAS exclu par ce correctif** : si le joueur a été ghosté alors
qu'il pensait avoir soumis ses ordres à temps (plutôt que de simplement ne pas avoir eu le temps),
il pourrait y avoir un vrai bug de désynchronisation de `turn_number` (voir la liste "reste à faire"
du workflow initial — jamais confirmé ni corrigé cette session). Le nouveau log `[Ghost]` permettra
de vérifier ça la prochaine fois avec une vraie preuve plutôt qu'une hypothèse.

### 19.13 Déploiement réel du 2026-09-08 — serveur en production, APK en cours

Suite à la confirmation du joueur (mode testé = Deathmatch/Zone de Contrôle, build+déploiement
demandés), toutes les corrections de §19.1 à §19.12 ont été construites et déployées en RÉEL :

- **Build serveur Linux** (`ServerBuildScript.BuildLinuxServer`, batch mode) : succès, 119 Mo.
  Fraîcheur vérifiée PAR RÉFLEXION .NET (pas seulement l'horodatage du fichier, sujet au piège Bee
  documenté dans `project_novgov_vps_live` — chargement effectif de l'assembly et recherche des
  types/méthodes réels) : `PlayerConnection.PumpKeepalives`, `MatchSessionManager+UnitTurnStart`,
  `GeometryMath.FindBuildingAtOrNear` tous confirmés PRÉSENTS dans le DLL buildé.
- **Déploiement VPS** (`novgov.com`, `/opt/novgov/`) : sauvegarde de l'image en cours
  (`docker commit` -> `novgov-game-server:backup_20260908_204728`) AVANT toute modification,
  copie du nouveau build (`scp`, ~14s), MD5 du DLL identique entre local et VPS après copie,
  `docker compose build game-server-1` (succès), `docker compose up -d game-server-1`. Vérifié
  après coup : conteneur `Up` stable (40s+, pas de crash-loop), zéro `exception`/`error`/`crash`/
  `fatal` dans les logs (hors les avertissements shader habituels et sans conséquence), génération
  de ville propre ("87 éléments OSM -> 236 bâtiments"), port 7777 en écoute IPv4 ET IPv6, connexion
  TCP RÉELLEMENT établie depuis l'extérieur du VPS (`novgov.com:7777`, pas juste `localhost`).
- **Build Android** (`AndroidTestBuildScript.BuildDebugApk`, batch mode, même session Unity
  que le build serveur ci-dessus — donc EXACTEMENT le même arbre source, aucune modification entre
  les deux) : succès, `build/Android/Novgov-Test.apk` (135 Mo), zéro `error CS` dans le log complet
  (`android_build_20260908.log`). Pas installé sur un appareil par cette session (aucun accès
  matériel) — fichier local, à transférer sur le téléphone de test pour la suite.

**Toujours pas de vraie partie à 2 joueurs rejouée** — c'est la prochaine étape une fois l'APK
installé : réinstaller `build/Android/Novgov-Test.apk` sur le(s) téléphone(s) de test et refaire un
Deathmatch/Zone de Contrôle à 2 comptes pour confirmer que les 4 signalements initiaux
(§19.11/§19.12) sont bien résolus en conditions réelles, pas seulement en lecture de code et en
déploiement serveur. Le serveur, lui, est déjà en production et prêt à recevoir ce test.

### 19.14 Correctif 2026-09-09 — sélection tactile/souris difficile en multijoueur, root cause confirmée par logs réels

Retour joueur : "en multijoueur deathmatch c'est difficile de sélectionner les unités, des fois ça
fonctionne bien, des fois pas". Un diagnostic temporaire (`[SelectDiag]`) avait été ajouté en
session précédente dans `TacticalPathManager_Input.HandlePointerInput` sans conclusion. Cette
session a retrouvé une vraie partie jouée localement le soir même dans
`E:\NOVGOV\My project\Logs\Editor.log` (l'Éditeur venait d'être fermé) et y a lu les 28 lignes
`[SelectDiag]` qu'elle contenait — root cause confirmée SANS avoir besoin de rejouer.

**Root cause** : la boucle de sélection tolérante (rayon 60-75px à l'écran, ajoutée le 2026-09-05/06
pour rattraper un tap légèrement imprécis) mesurait la distance à l'écran depuis
`unit.transform.position + Vector3.up * 0.5f`. Pour un blindé (CharLeopard/VehiculeCanon/Mortier),
c'est le PIVOT D'IMPORT du modèle, pas son centre visuel réel — `UnitAI.Start()` le savait déjà et
recentre pour cette raison le `BoxCollider` sur `bounds.center` (vrai centre du maillage) et corrige
le `baseOffset` du `NavMeshAgent` en conséquence, mais la sélection, elle, continuait de lire le
pivot brut. Preuve dans les logs : à chaque tap manqué, l'unité écartée était systématiquement
`CharLeopard_2_2`/`VehiculeCanon_2_3`, à 150-500+ PIXELS d'écran du point tapé — jamais un
Fantassin (pivot déjà quasi confondu avec son centre visuel). Un tap DIRECT pile sur le modèle
continuait de fonctionner (son collider, lui, était déjà bien recentré) : d'où "des fois ça marche"
— uniquement quand le tap tombe pile sur le blindé, jamais quand la tolérance est censée rattraper
une petite imprécision, ce qui est précisément le cas d'usage qu'elle existe pour couvrir.

**Corrigé** : nouvelle propriété `UnitAI.SelectionAnchorWorldPos` (centre du même collider déjà
recentré par `Start()`, repli sur l'ancien calcul si pas encore de collider) utilisée partout où
`TacticalPathManager_Input` calculait une distance-écran à une unité (boucle tolérante + arbitrage
raycast-vs-tolérance). Diagnostic `[SelectDiag]` retiré (cause confirmée, voir consigne du
commentaire qui l'avait introduit).

**Bug latent trouvé au passage, corrigé aussi** : `UnitSpawnerUI.SpawnUnitAt` codait en dur
`isPlayerControlled = (team == 1)` — faux dès qu'un joueur multijoueur est l'ÉQUIPE 2 (le 2e joueur
à rejoindre, voir `MultiplayerMatchController.LocalTeamId`) : ses propres unités, posées sur SON
PROPRE dock de déploiement, se marquaient comme injouables dès leur pose. Sans conséquence sur le
combat réel (déjà recorrigé juste après par la boucle de `OnDeploymentResult`/`PlaySnapshotsBody`
qui réaffecte `isPlayerControlled` d'après `localTeamId`), mais faux pendant la PRÉVISUALISATION du
placement. Corrigé pour comparer à `MultiplayerMatchController.Instance.LocalTeamId` quand un match
multijoueur (déploiement ou combat) est actif ; repli inchangé (`team == 1`) en solo et côté serveur
(`LocalTeamId` n'existe que côté client, toute la classe sauf ses membres statiques étant sous
`#if !UNITY_SERVER`).

Vérifié : `Tools\run-tests.ps1` complet (76 tests TacticalCore + compile-check client/serveur/
éditeur) entièrement vert après le correctif. Pas de nouvelle partie à 2 joueurs rejouée cette
session (pas d'accès à un second appareil/émulateur depuis cet environnement) — root cause établie
par preuve de log réelle plutôt que par re-test, mais une confirmation en jeu réel reste la
prochaine étape recommandée.
