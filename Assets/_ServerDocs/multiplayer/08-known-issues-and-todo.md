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
   (`NovgovServer.x86_64`), le dossier `NovgovServer_Data/`, et **`UnityPlayer.so`** (facile
   à oublier — il est à côté de l'exécutable, pas dedans `_Data/`).
2. Transférer sur le VPS (`scp` + `tar`, voir historique de session pour la commande exacte —
   env. 40 Mo compressés) dans `/opt/streetact/game-server/`.
3. `docker compose build game-server && docker compose up -d game-server` sur le VPS.
4. **Vérifier les logs** (`docker compose logs -f game-server`) : le serveur doit charger la
   carte par défaut, générer les ~396 bâtiments (pas seulement 21 — sinon le fix shader n'a pas
   pris), et afficher `[GameServerBootstrap] Serveur de jeu Novgov à l'écoute sur le port 7777.`
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
