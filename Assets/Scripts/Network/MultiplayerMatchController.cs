using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Auth;
using UnityEngine;
using UnityEngine.AI;
#if !UNITY_SERVER
using UnityEngine.UIElements;
using UnityEngine.Networking;
#endif

namespace Novgov.Network
{
    /// <summary>
    /// Orchestration complète du mode multijoueur côté client : login/inscription, matchmaking,
    /// envoi des ordres, lecture des snapshots renvoyés par le serveur. Voir
    /// Assets/_ServerDocs/multiplayer/05-client-integration.md pour le contexte d'intégration.
    /// Le mode solo (TacticalAIPlanner local) n'est jamais impacté : voir le garde
    /// "MultiplayerMatchController.IsActive" ajouté dans TacticalPathManager.LancerExecutionTour().
    ///
    /// UI en UI Toolkit (voir Assets/Scripts/UI/UIScreenManager.cs) — chaque état a un écran UXML
    /// sous Resources/UI/, câblé une fois dans BindUI() plutôt que redessiné en OnGUI chaque frame.
    ///
    /// Tout ce fichier, hormis les membres statiques et SubmitLocalTurn() ci-dessous, est englobé
    /// dans #if !UNITY_SERVER : cette classe représente exclusivement le flux CLIENT réagissant aux
    /// messages d'un serveur distant (le serveur autoritaire, lui, a sa propre logique dans
    /// Assets/Scripts/Server/MatchSessionManager.cs) — rien ici n'a de raison de tourner sur un
    /// build Dedicated Server. Avant ce garde, un build où UNITY_SERVER se retrouvait défini (ex:
    /// sous-cible Server restée active par erreur dans les réglages de l'Éditeur, voir
    /// ServerBuildScript.cs) faisait échouer TOUTE la compilation : Update()/BeginLoginFlow/
    /// HandleSignIn/etc. appelaient SetUiState/RefreshHudDynamicFields (déclarées plus bas, dans un
    /// bloc #if !UNITY_SERVER déjà existant) sans être elles-mêmes gardées.
    /// </summary>
    public partial class MultiplayerMatchController : MonoBehaviour
    {
        public static MultiplayerMatchController Instance { get; private set; }
        public static bool IsActive { get; private set; }

        /// <summary>
        /// Vrai dès que l'écran multijoueur (login/mode/matchmaking/HUD/fin de partie) occupe l'écran
        /// — utilisé par UnitSpawnerUI pour ne pas ré-afficher son propre dock de déploiement solo
        /// par-dessus (les deux systèmes d'UI Toolkit sont indépendants et se raffraîchissent chacun
        /// dans leur propre Update(), donc sans ce garde ils se ré-affichent en boucle l'un sur l'autre).
        /// </summary>
        public static bool IsFlowActive { get; private set; }

        /// <summary>
        /// Vrai UNIQUEMENT pendant le tour-par-tour effectif (état InMatch) — distinct de
        /// IsFlowActive (vrai aussi pendant login/matchmaking/fin de partie) : la barre du bas
        /// (TacticalPathManager_UI, bouton FIN DE TOUR/squad-bar) doit rester masquée pendant les
        /// écrans de login/matchmaking, mais bien réapparaître une fois la partie commencée, alors
        /// que le dock de déploiement manuel (UnitSpawnerUI), lui, doit rester masqué pendant TOUTE
        /// la session multijoueur y compris en jeu (auto-déploiement serveur, jamais de placement
        /// manuel en PvP) — d'où deux indicateurs distincts plutôt qu'un seul.
        /// </summary>
        public static bool IsInMatch { get; private set; }

        /// <summary>
        /// Vrai UNIQUEMENT pendant la phase de placement manuel PvP (entre "match_found" et l'envoi
        /// de "submit_deployment") — utilisé par UnitSpawnerUI.RefreshDeploymentDockUI pour afficher
        /// exceptionnellement son dock de déploiement (normalement masqué pendant tout le reste du
        /// flux multijoueur, voir IsInMatch), verrouillé sur le seul camp du joueur local.
        /// </summary>
        public static bool IsDeploymentPhaseActive { get; private set; }

        public static MultiplayerMatchController EnsureInstance()
        {
            if (Instance == null)
            {
                var go = new GameObject("MultiplayerMatchController");
                DontDestroyOnLoad(go);
                go.AddComponent<MultiplayerMatchController>();
            }
            return Instance;
        }

#if !UNITY_SERVER
        private enum UiState { Hidden, Hub, Login, SignUp, Connecting, Matchmaking, Deployment, InMatch, MatchOver }
        private UiState uiState = UiState.Hidden;

        private string selectedMode = "deathmatch";

        // Zone de Conquête ciblée par AttackZone() — ignorés par le serveur pour deathmatch/zone_control
        // (voir NetMessage.zone_tile_x/zone_tile_y).
        private int attackTileX;
        private int attackTileY;

        private long pendingSiegeId;

        /// <summary>Résultat d'une demande de conquête INSTANTANÉE (zone_captured/zone_attack_result,
        /// pas de combat) — voir Novgov.UI.ZoneMapController, seul abonné actuel, qui affiche le
        /// message sur ZoneResultScreen. Le cas "combat de conquête" (match_found -> déploiement ->
        /// match_over) passe par le flux UiState normal (OnMatchOver), pas par cet event.</summary>
        public static event System.Action<string, bool> OnZoneResult; // (message, succès) — succès pilote le son joué

        private string emailField = "";
        private string passwordField = "";
        private string usernameField = "";

        private int localTeamId = 0;
        public int LocalTeamId => localTeamId;
        public static Dictionary<string, int> LostUnits = new Dictionary<string, int>();

        // Composition RÉELLE déployée pour mon camp (unit_type -> quantité), capturée à
        private Dictionary<string, int> deployedRosterCountByType = new Dictionary<string, int>();

        private string currentMode = "deathmatch";
        private float zoneProgressTeam1 = 0f;
        private float zoneProgressTeam2 = 0f;
        private int currentTurnNumber = 1;
        private int lastServerSecondsRemaining = -1;
        public static int PhaseSecondsRemaining => Instance != null ? Instance.lastServerSecondsRemaining : 0;
        private bool isPlayingSnapshots = false;
        // "turn_result" reçu mais pas encore rejoué — mis en cache le temps d'accuser réception au
        // serveur et d'attendre son signal "turn_playback_start" (voir OnTurnResultReceived/
        // OnTurnPlaybackStart), pour que la LECTURE démarre au même instant chez les deux clients au
        // lieu de démarrer dès que CE payload (taille variable selon le brouillard de guerre) est
        // arrivé.
        private NetMessage pendingTurnResult;
        private string statusMessage = "";
        private string ghostBannerText = "";
        private float ghostBannerTimer = 0f;

        // Références UI Toolkit mises en cache une fois dans BindUI().
        private VisualElement authRoot, waitingRoot, hudRoot, matchOverRoot;
        private Label authTitleLabel, authStatusLabel, waitingStatusLabel;

        // Écran "Waiting" (matchmaking initial ET attente de déploiement adverse, voir SetUiState) —
        private float waitingScreenEnteredRealtime = 0f;
        private TextField emailFieldEl, passwordFieldEl, usernameFieldEl;
        private VisualElement usernameContainer;
        private Button submitButton, toggleModeButton;
        private Label teamBanner, phaseLabel, ghostBannerLabel, resultLabel, ratingLabel;
        private VisualElement zoneBarContainer, zoneFillTeam1, zoneFillTeam2;
        private bool uiBound = false;


        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BindUI();
        }

        private void Update()
        {
            if (ghostBannerTimer > 0f)
            {
                ghostBannerTimer -= Time.deltaTime;
                if (ghostBannerTimer <= 0f && ghostBannerLabel != null) ghostBannerLabel.style.display = DisplayStyle.None;
            }

            if (uiState == UiState.Connecting || uiState == UiState.Matchmaking)
            {
                if (waitingStatusLabel != null) waitingStatusLabel.text = RenderWaitingScreenText();
            }
            else if (uiState == UiState.InMatch)
            {
                RefreshHudDynamicFields();
            }
        }

                /// voir waitingScreenEnteredRealtime pour le contexte complet). <see cref="statusMessage"/>
        /// seul ne changeait jamais tant que le message attendu (match_found/deployment_result)
        /// n'arrivait pas — potentiellement plusieurs MINUTES de silence total à l'écran, ce qui se
        /// lit exactement comme un gel du jeu même quand tout fonctionne normalement.</summary>
        private string RenderWaitingScreenText()
        {
            float elapsed = Time.realtimeSinceStartup - waitingScreenEnteredRealtime;

            // Points de suspension animés (0 à 3, un cran toutes les ~0,5s) : la plus petite preuve
            // possible que l'application tourne toujours et n'a pas gelé — un texte parfaitement
            // statique pendant plusieurs minutes est indiscernable d'un plantage pour le joueur.
            int dotCount = ((int)(elapsed * 2f)) % 4;
            string dots = new string('.', dotCount);

            string text = statusMessage + dots;

            // Au-delà de 15s, cette attente n'est plus le cas courant (l'appariement/déploiement
            // normal est quasi instantané entre deux joueurs déjà prêts) — le joueur mérite de savoir
            // explicitement que c'est ATTENDU et BORNÉ dans le temps, pas planté. Les bornes réelles
            // sont MatchSessionManager.MapReadyMaxWaitSeconds/DeploymentSeconds (300s chacune,
            // volontairement généreuses, voir leur commentaire) — jusqu'à 10 minutes dans le pire cas
            // si l'adversaire charge encore sa carte ou n'a pas fini de se déployer.
            if (elapsed > 15f)
            {
                text += "\n\nCela peut prendre plusieurs minutes si l'adversaire charge encore sa carte — la partie n'est pas bloquée.";
            }

            return text;
        }

        public async void BeginLoginFlow()
        {
            if (SupabaseAuthClient.HasSavedSession())
            {
                statusMessage = "Reconnexion...";
                SetUiState(UiState.Connecting);
                var (ok, _) = await SupabaseAuthClient.TryRestoreSession();
                if (ok)
                {
                    EnterHub();
                    return;
                }
                // Session sauvegardée invalide/expirée (ex: > 30 jours) — retour au formulaire normal.
            }

            statusMessage = "";
            SetUiState(UiState.Login);
        }

        // =====================================================================
        // Auth
        // =====================================================================

        private async void HandleSignIn()
        {
            if (emailFieldEl != null && !string.IsNullOrEmpty(emailFieldEl.value)) emailField = emailFieldEl.value;
            if (passwordFieldEl != null && !string.IsNullOrEmpty(passwordFieldEl.value)) passwordField = passwordFieldEl.value;

            if (string.IsNullOrWhiteSpace(emailField) || string.IsNullOrWhiteSpace(passwordField))
            {
                statusMessage = "Veuillez saisir votre email et mot de passe.";
                SetUiState(UiState.Login);
                return;
            }

            statusMessage = "Connexion en cours...";
            SetUiState(UiState.Connecting);
            var (ok, error) = await SupabaseAuthClient.SignIn(emailField.Trim(), passwordField);
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                SetUiState(UiState.Login);
                return;
            }
            statusMessage = "";
            EnterHub();
        }

        private async void HandleSignUp()
        {
            if (emailFieldEl != null && !string.IsNullOrEmpty(emailFieldEl.value)) emailField = emailFieldEl.value;
            if (passwordFieldEl != null && !string.IsNullOrEmpty(passwordFieldEl.value)) passwordField = passwordFieldEl.value;
            if (usernameFieldEl != null && !string.IsNullOrEmpty(usernameFieldEl.value)) usernameField = usernameFieldEl.value;

            if (string.IsNullOrWhiteSpace(emailField) || string.IsNullOrWhiteSpace(passwordField))
            {
                statusMessage = "Veuillez renseigner un email et un mot de passe.";
                SetUiState(UiState.SignUp);
                return;
            }

            if (passwordField.Length < 6)
            {
                statusMessage = "Le mot de passe doit comporter au moins 6 caractères.";
                SetUiState(UiState.SignUp);
                return;
            }

            if (string.IsNullOrWhiteSpace(usernameField))
            {
                usernameField = emailField.Split('@')[0];
            }

            statusMessage = "Création du compte...";
            SetUiState(UiState.Connecting);
            var (ok, error) = await SupabaseAuthClient.SignUp(emailField.Trim(), passwordField, usernameField.Trim());
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                SetUiState(UiState.SignUp);
                return;
            }
            statusMessage = "";
            EnterHub();
        }

        // =====================================================================
        // Navigation de l'écran CONQUÊTE (refonte du parcours des menus, 2026-09-30)
        // =====================================================================
        // État Hub = écran "Conquest" (ConquestScreen.uxml) : onglets CARTE (Novgov.UI.
        // ZoneMapController) et GESTION (câblé ici, BindUI). Depuis le 2026-09-30 (demande joueur :
        // "après multijoueur, je veux juste Conquête"), Match à mort / Contrôle de zone /
        // Entraînement ne sont plus proposés dans l'interface — StartDeathmatch/StartZoneControl/
        // StartPracticeVsAI restent, sans appelant UI, le protocole et le serveur les gérant toujours.

        // Vrai dès qu'un "match_found" a modifié la scène (autre carte chargée, unités posées,
        // dock ouvert...) : revenir à la Conquête passe alors par un rechargement de scène propre
        // (voir ReturnToHub) au lieu d'afficher l'écran par-dessus les restes de la partie.
        private bool sceneDirtyFromMatch = false;

        // Sièges où je suis DÉFENSEUR, le plus urgent en tête — lu par RefreshHubScreen pour l'alerte
        // "quartier assiégé" et son bouton DÉFENDRE.
        private Novgov.Auth.SupabaseDatabaseClient.SiegeInfo mostUrgentDefense;

        /// <summary>Point d'entrée UNIQUE de la Conquête une fois le compte connecté : garantit
        /// d'abord que le QG du joueur est placé et son quartier chargé (localisation expliquée la
        /// toute première fois, voir GameManagerUI.EnsureOnlineZoneReady), puis ouvre l'écran. Avant
        /// le 2026-09-30, le GPS et le chargement de la ville passaient AVANT même la connexion.</summary>
        private void EnterHub()
        {
            GameManagerUI gm = GameManagerUI.Instance;
            if (gm == null)
            {
                SetUiState(UiState.Hub);
                return;
            }

            statusMessage = "Préparation de la carte de conquête";
            SetUiState(UiState.Connecting);
            gm.EnsureOnlineZoneReady(
                onReady: () => { statusMessage = ""; SetUiState(UiState.Hub); },
                onCancelled: () =>
                {
                    statusMessage = "";
                    SetUiState(UiState.Hidden);
                    GameManagerUI.Instance?.ReturnToStartupMenu();
                });
        }

        /// <summary>Ouvre un écran de gestion (Caserne, Mes quartiers, Sièges, Rapports) avec son
        /// contenu à jour — utilisé par l'onglet GESTION et par les raccourcis de la fiche d'un
        /// quartier sur la carte (ex: "AMÉLIORER MES QUARTIERS"). Son bouton RETOUR revient à la
        /// Conquête, sur l'onglet où l'on était.</summary>
        public void OpenManagementScreen(string screen)
        {
            UIScreenManager.Instance.Show(screen);
            switch (screen)
            {
                case "Roster": RefreshRosterScreen(); break;
                case "Buildings": RefreshBuildingsScreen(); break;
                case "Sieges": RefreshSiegesScreen(); break;
                case "Notifications": RefreshNotificationsScreen(); break;
            }
        }

        /// <summary>Retour au Quartier Général depuis n'importe quel sous-écran ou résultat (Carte de
        /// Conquête, résultat de siège, annulation d'une attente...) — remplace les anciens appels à
        /// GameManagerUI.ReturnToStartupMenu() de ces écrans, qui renvoyaient au menu Solo/En ligne
        /// alors que leur bouton disait "RETOUR AU HUB". Recharge la scène si une partie l'a modifiée
        /// (voir sceneDirtyFromMatch).</summary>
        public void ReturnToHub()
        {
            statusMessage = "";
            if (!sceneDirtyFromMatch)
            {
                SetUiState(UiState.Hub);
                return;
            }

            // La Zone chargée pour la partie (Zone assiégée, carte de match...) ne doit pas devenir
            // la position du joueur sur la Carte de Conquête — même restauration que OnMatchOver.
            if (preMatchExplorationTileX.HasValue && preMatchExplorationTileY.HasValue && Novgov.Generation.ZoneManager.Instance != null)
            {
                Novgov.Generation.ZoneManager.Instance.SetCurrentTileWithoutLoading(preMatchExplorationTileX.Value, preMatchExplorationTileY.Value);
            }
            preMatchExplorationTileX = null;
            preMatchExplorationTileY = null;
            ReloadSceneBackToHub();
        }

        /// <summary>Vrai si le prochain ReturnToHub rechargera la scène — voir ZoneMapController (le
        /// bouton OK d'un résultat ne peut revenir directement sur la carte que si ce n'est pas le cas).</summary>
        public bool ReturnToHubNeedsReload => sceneDirtyFromMatch;

        private void ReloadSceneBackToHub()
        {
            // Ce contrôleur survit au rechargement (DontDestroyOnLoad) : une coroutine encore en cours
            // (rejeu de tour, chargement de carte de match...) continuerait sinon à manipuler les
            // unités/la ville de la scène détruite. Jamais appelé depuis une de ses coroutines.
            StopAllCoroutines();
            isPlayingSnapshots = false;
            pendingTurnResult = null;
            awaitingCityVerifyResult = false;
            sceneDirtyFromMatch = false;
            IsActive = false;
            IsDeploymentPhaseActive = false;
            SetUiState(UiState.Hidden);
            GameManagerUI.ReloadSceneThen(GameManagerUI.AfterReloadAction.OpenOnlineHub);
        }

        // Message à afficher UNE fois en tête de la Conquête (ex: raison d'une déconnexion en cours
        // de partie) — voir lbl-hub-notice dans ConquestScreen.uxml. Effacé dès qu'il a été montré
        // puis que le joueur quitte l'écran.
        private string pendingHubNotice = null;

        private bool quitConfirmArmed = false;
        private Button cancelWaitButton;

        /// <summary>Bouton de l'écran d'attente (2026-09-30) — il n'existait AUCUN moyen de sortir de
        /// "Recherche d'adversaire..." : sans second joueur en file, le serveur garde la connexion
        /// indéfiniment (keepalives), le joueur restait bloqué jusqu'à tuer l'application. Avant
        /// l'appariement : annulation immédiate. Après (carte en chargement, attente du déploiement
        /// adverse) : c'est un abandon de partie, confirmé par un second appui.</summary>
        private void OnCancelWaitClicked()
        {
            if (uiState != UiState.Matchmaking) return;

            if (sceneDirtyFromMatch && !quitConfirmArmed)
            {
                quitConfirmArmed = true;
                RefreshCancelWaitButton();
                return;
            }

            // Coupe toute étape encore en cours de CE contrôleur (requête du serveur libre,
            // chargement de la carte de match, vérification de géométrie) : aucune ne doit rouvrir
            // un écran de partie après l'annulation. En état Matchmaking, aucune autre coroutine de
            // ce contrôleur (rejeu de tour, fin de match) ne tourne.
            StopAllCoroutines();
            Novgov.UI.ZoneMapController.Instance?.ForgetPendingAttack();
            expectingCloseAfterZoneResult = false;
            awaitingCityVerifyResult = false;
            IsActive = false;
            IsDeploymentPhaseActive = false;

            // L'état est quitté AVANT la déconnexion : HandleServerDisconnected (dispatché à la frame
            // suivante) ignore ainsi cette fermeture volontaire au lieu d'afficher "Connexion perdue".
            if (sceneDirtyFromMatch)
            {
                ReturnToHub(); // abandon d'une partie déjà trouvée : scène rechargée
                GameServerClient.Instance?.Disconnect("user_quit_match");
                return;
            }

            SetUiState(UiState.Hub);
            GameServerClient.Instance?.Disconnect("user_cancelled_matchmaking");
        }

        private void RefreshCancelWaitButton()
        {
            if (cancelWaitButton == null) return;
            cancelWaitButton.style.display = uiState == UiState.Matchmaking ? DisplayStyle.Flex : DisplayStyle.None;
            cancelWaitButton.text = !sceneDirtyFromMatch
                ? "ANNULER"
                : (quitConfirmArmed ? "TOUCHEZ À NOUVEAU POUR QUITTER" : "QUITTER LA PARTIE");
        }

        /// <summary>Quitter une partie EN COURS depuis le menu pause (voir Novgov.UI.
        /// InGameMenuController) : l'état est quitté avant la déconnexion (même raison que
        /// OnCancelWaitClicked), puis la scène est rechargée vers le QG. Côté serveur, une connexion
        /// perdue en cours de partie passe les unités du joueur en garde automatique (Ghost).</summary>
        public void QuitCurrentMatch()
        {
            sceneDirtyFromMatch = true; // ReturnToHub -> ReloadSceneBackToHub coupe aussi les coroutines de rejeu
            ReturnToHub();
            GameServerClient.Instance?.Disconnect("user_quit_match");
        }

        [System.Serializable] private class ServerInstanceEntry { public string id; public int public_port; }
        [System.Serializable] private class ServerInstanceList { public ServerInstanceEntry[] items; }

        private const int InstanceStaleSeconds = 60;

        private IEnumerator ConnectToGameServerCoroutine()
        {
            statusMessage = "Recherche d'un serveur de jeu libre...";
            SetUiState(UiState.Matchmaking);

            int chosenPort = 7777; // Port robuste par défaut
            string queueColumn = selectedMode == "zone_control" ? "waiting_zone_control" : "waiting_deathmatch";
            string url = $"{SupabaseAuthClient.RestBaseUrl}/server_instances?status=neq.busy&order={queueColumn}.desc,updated_at.desc&limit=1&select=id,public_port";

            if (SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(SupabaseAuthClient.CurrentSession.access_token))
            {
                using (UnityWebRequest req = UnityWebRequest.Get(url))
                {
                    req.timeout = 4;
                    req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                    req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.access_token);
                    yield return req.SendWebRequest();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                            ServerInstanceEntry[] instances = JsonUtility.FromJson<ServerInstanceList>(wrapped).items;
                            if (instances != null && instances.Length > 0 && instances[0].public_port > 0)
                            {
                                chosenPort = instances[0].public_port;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[MultiplayerMatchController] Parsing server_instances : {ex.Message}");
                        }
                    }
                }
            }

            GameServerClient.ServerPort = chosenPort;

            GameServerClient client = GameServerClient.Instance;
            if (client == null)
            {
                var go = new GameObject("GameServerClient");
                DontDestroyOnLoad(go);
                client = go.AddComponent<GameServerClient>();
            }

            // Défensif : ce contrôleur ET GameServerClient sont tous deux DontDestroyOnLoad, donc
            // survivent à un rechargement de scène (ex. bouton "Rejouer" après match_over). Sans ce
            // retrait préalable, rejouer une deuxième partie dans la même session ajoutait un
            // abonnement SUPPLÉMENTAIRE à chaque connexion, faisant traiter chaque message serveur
            // (donc chaque relecture de snapshot de combat) en double, triple, etc. au fil des parties.
            client.OnMessage -= HandleServerMessage;
            client.OnDisconnected -= HandleServerDisconnected;
            client.OnMessage += HandleServerMessage;
            client.OnDisconnected += HandleServerDisconnected;
            client.Connect(SupabaseAuthClient.CurrentSession.access_token);

            // Deathmatch/Zone de Contrôle sur la vraie position GPS du joueur (2026-08-30, "des
            // milliers de cartes") : envoie la tuile domicile déjà connue (voir ZoneManager,
            // GameManagerUI.StartDeviceGPS — GPS consulté UNE SEULE fois au tout premier lancement,
            // jamais réinterrogé ici) ; sans effet pour la Conquête, qui a déjà son propre usage de
            // zone_tile_x/y (attackTileX/Y ci-dessus, une Zone précisément visée, pas un domicile).
            bool hasHomeTile = false;
            int homeTileX = 0, homeTileY = 0;
            bool targetsSpecificZone = selectedMode == "conquest" || selectedMode == "siege_attack_deploy" || selectedMode == "siege_defend_deploy";
            if (!targetsSpecificZone)
            {
                var zoneManager = Novgov.Generation.ZoneManager.Instance;
                if (zoneManager != null && zoneManager.HasHomeZone)
                {
                    hasHomeTile = true;
                    homeTileX = zoneManager.HomeTileX;
                    homeTileY = zoneManager.HomeTileY;
                }
            }

            client.Send(new NetMessage
            {
                type = "join_matchmaking",
                mode = selectedMode,
                zone_tile_x = targetsSpecificZone ? attackTileX : homeTileX,
                zone_tile_y = targetsSpecificZone ? attackTileY : homeTileY,
                has_home_tile = hasHomeTile,
                siege_id = pendingSiegeId
            });

            // Textes du point de vue du joueur (2026-09-30) — plus de "Attaque de la Zone (66648,44111)".
            statusMessage = selectedMode switch
            {
                "conquest" => "Prise du quartier en cours",
                "siege_attack_deploy" => "Préparation du siège : chargement du quartier visé",
                "siege_defend_deploy" => "Préparation de la défense : chargement de votre quartier",
                _ => "Recherche d'adversaire",
            };
        }

        /// <summary>Demande au serveur d'attaquer/capturer la Zone de Conquête (tileX,tileY) — voir
        /// MatchSessionManager.HandleConquestMessage : capture instantanée si elle est neutre, combat
        /// contre sa garnison IA si elle appartient à un autre joueur. Résultat via "zone_captured"
        /// (capture immédiate), "zone_attack_result" (refus) ou "match_found"/"match_over" (combat).
        /// Appelé par ZoneManager (voir Assets/Scripts/Generation/ZoneManager.cs).</summary>
        public void AttackZone(int tileX, int tileY)
        {
            selectedMode = "conquest";
            attackTileX = tileX;
            attackTileY = tileY;
            StartCoroutine(ConnectToGameServerCoroutine());
        }

        /// <summary>Déploiement de l'ATTAQUANT pour un siège déjà déclaré (le siège lui-même doit
        /// avoir été créé AVANT cet appel via SupabaseDatabaseClient.StartSiege — voir
        /// Novgov.UI.ZoneMapController). Le serveur ouvre le même dock de déploiement qu'une Conquête
        /// classique, mais ne fait tourner AUCUN combat immédiatement : voir
        /// MatchSessionManager_Siege.RunSiegeAttackDeploy, résultat via "siege_deploy_ack"
        /// (OnSiegeDeployAck), jamais "match_found"->combat->"match_over".</summary>
        public void SiegeZone(int tileX, int tileY, long siegeId)
        {
            selectedMode = "siege_attack_deploy";
            attackTileX = tileX;
            attackTileY = tileY;
            pendingSiegeId = siegeId;
            StartCoroutine(ConnectToGameServerCoroutine());
        }

        /// <summary>Déploiement du DÉFENSEUR pour un siège en cours contre lui (voir la table
        /// public.zone_sieges / la notification "under_attack" qui l'a informé) — même mécanique que
        /// SiegeZone, côté défenseur. Voir MatchSessionManager_Siege.RunSiegeDefendDeploy.</summary>
        public void DefendSiege(int tileX, int tileY, long siegeId)
        {
            selectedMode = "siege_defend_deploy";
            attackTileX = tileX;
            attackTileY = tileY;
            pendingSiegeId = siegeId;
            StartCoroutine(ConnectToGameServerCoroutine());
        }

                /// depuis l'UI — seuls AttackZone (Conquête, un joueur contre une garnison IA, jamais un
        /// adversaire vivant) et StartPracticeVsAI (accessible uniquement DEPUIS une file d'attente
        /// déjà ouverte) appelaient ConnectToGameServerCoroutine. Le vrai appariement à deux joueurs
        /// vivants (DetermineMatchCacheKey côté serveur) existait donc dans le protocole sans aucun
        /// bouton pour l'atteindre. Ajouté ici — puis retiré de l'interface le 2026-09-30 (demande
        /// joueur : seule la Conquête est proposée en ligne), voir "Navigation de l'écran CONQUÊTE".</summary>
        public void StartDeathmatch()
        {
            selectedMode = "deathmatch";
            StartCoroutine(ConnectToGameServerCoroutine());
        }

        public void StartZoneControl()
        {
            selectedMode = "zone_control";
            StartCoroutine(ConnectToGameServerCoroutine());
        }

        public void StartPracticeVsAI()
        {
            selectedMode = "practice_ai";
            StartCoroutine(ConnectToGameServerCoroutine());
        }

        /// <summary>Traduit les codes internes de GameServerClient.Disconnect en message lisible par
        /// le joueur — auparavant affiché tel quel (ex. "Connexion au serveur perdue (app_paused).")
        /// (voir rapport d'audit interface, défaut bloquant #1).</summary>
        private static string DescribeDisconnectReason(string reason) => reason switch
        {
            // Après le délai de grâce de GameServerClient.BackgroundGraceSeconds : l'appli est
            // restée en arrière-plan trop longtemps, le serveur a basculé vos unités en garde
            // automatique (Ghost) pour ne pas bloquer votre adversaire.
            "app_paused" => "Vous êtes resté trop longtemps hors de l'application : vos unités ont été laissées en garde automatique et la partie a continué sans vous.",
            "app_quit" => "Partie interrompue : l'application a été fermée.",
            "connection_lost" => "Connexion au serveur perdue. Vérifiez votre réseau et réessayez.",
            "send_failed" => "Impossible de communiquer avec le serveur. Vérifiez votre réseau et réessayez.",
            "map_load_failed" => "Le chargement de la carte a échoué. Réessayez.",
            _ => "Connexion au serveur perdue. Réessayez.",
        };

        private void HandleServerDisconnected(string reason)
        {
            // Fermeture ATTENDUE après un résultat de Conquête instantanée : ne rien signaler, et
            // surtout ne pas écraser le panneau de résultat que le joueur est en train de lire.
            if (expectingCloseAfterZoneResult)
            {
                expectingCloseAfterZoneResult = false;
                IsActive = false;
                IsDeploymentPhaseActive = false;
                return;
            }

            if (uiState == UiState.InMatch || uiState == UiState.Matchmaking || uiState == UiState.Deployment)
            {
                IsActive = false;
                IsDeploymentPhaseActive = false;
                if (Novgov.Auth.SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(Novgov.Auth.SupabaseAuthClient.CurrentSession.access_token))
                {
                    // 2026-09-30 : la raison était écrite dans statusMessage, qu'AUCUN élément du hub
                    // n'affiche — le joueur se retrouvait au menu sans savoir pourquoi. Elle passe
                    // maintenant par le bandeau d'avis du QG (survit au rechargement de scène que
                    // ReturnToHub déclenche si la partie avait déjà modifié la carte).
                    pendingHubNotice = DescribeDisconnectReason(reason);
                    ReturnToHub();
                }
                else
                {
                    statusMessage = DescribeDisconnectReason(reason);
                    SetUiState(UiState.Login);
                }
            }
        }

        // =====================================================================
        // Messages serveur
        // =====================================================================

        private void HandleServerMessage(NetMessage msg)
        {
            // Un message déjà reçu mais pas encore dispatché au moment où le joueur annule/quitte
            // (ex: "match_found" arrivé dans la même frame que ANNULER) ne doit pas rouvrir un écran
            // de partie par-dessus le QG : hors attente serveur/partie, tout message est périmé.
            if (uiState == UiState.Hidden || uiState == UiState.Hub || uiState == UiState.Connecting
                || uiState == UiState.Login || uiState == UiState.SignUp)
            {
                Debug.Log($"[MultiplayerMatchController] Message serveur '{msg.type}' ignoré (hors partie, état {uiState}).");
                return;
            }

            switch (msg.type)
            {
                case "match_found": OnMatchFound(msg); break;
                case "deployment_result": OnDeploymentResult(msg); break;
                case "turn_timer": lastServerSecondsRemaining = msg.seconds_remaining; break;
                case "opponent_ghosted": OnOpponentGhosted(msg); break;
                case "turn_result": if (!isPlayingSnapshots) OnTurnResultReceived(msg); break;
                case "turn_playback_start": OnTurnPlaybackStart(msg); break;
                case "match_over": StartCoroutine(DeferredMatchOver(msg)); break;
                case "city_verify_result": OnCityVerifyResult(msg); break;
                case "zone_captured": OnZoneCaptured(msg); break;
                case "zone_attack_result": OnZoneAttackResult(msg); break;
                case "siege_deploy_ack": OnSiegeDeployAck(msg); break;
            }
        }

        /// <summary>Vrai quand le serveur vient d'envoyer un résultat de Conquête INSTANTANÉE
        /// (zone_captured / zone_attack_result) : il referme systématiquement la socket juste après
        /// (voir MatchSessionManager_Conquest, `attacker.Close()` sur chacun de ces chemins). Cette
        /// fermeture est donc NORMALE et attendue — sans ce drapeau, HandleServerDisconnected la
        /// traitait comme une panne réseau, affichait "Connexion au serveur perdue" et renvoyait au
        /// menu une frame après l'ouverture du panneau de résultat, que le joueur n'avait donc jamais
                private bool expectingCloseAfterZoneResult = false;

        private void OnZoneCaptured(NetMessage msg)
        {
            expectingCloseAfterZoneResult = true;
            OnZoneResult?.Invoke($"Quartier pris ! Il est à vous et vous rapportera des Points d'Action toutes les 5 minutes, même quand vous ne jouez pas.\n\n(+{msg.rating_delta} points au classement)", true);
            // Avancer la vue de la carte locale vers la zone nouvellement capturée
            if (Novgov.Generation.ZoneManager.Instance != null)
            {
                Novgov.Generation.ZoneManager.Instance.LoadZone(msg.zone_tile_x, msg.zone_tile_y);
            }
        }

        private void OnZoneAttackResult(NetMessage msg)
        {
            expectingCloseAfterZoneResult = true; // voir expectingCloseAfterZoneResult
            string message = msg.reason switch
            {
                "already_owned" => "Ce quartier est déjà à vous.",
                "server_busy" => "Le serveur est très occupé — réessayez dans un instant.",
                // "not_adjacent" (voir MatchSessionManager.RunConquestRequest) : le serveur vérifie
                // lui-même la contiguïté du territoire, un quartier lointain n'est pas attaquable
                // même via un client modifié.
                "not_adjacent" => "Ce quartier ne touche aucun des vôtres : prenez d'abord un quartier situé entre les deux.",
                // "zone_taken" : quartier libre pris par quelqu'un d'autre entre votre demande et la
                // réponse du serveur (deux joueurs visant le même quartier libre au même instant).
                "zone_taken" => "Trop tard : un autre joueur vient de prendre ce quartier juste avant vous.",
                _ => "Impossible de prendre ce quartier pour le moment. Réessayez dans un instant.",
            };
            OnZoneResult?.Invoke(message, false);
        }

                /// (mode="siege_attack_deploy") ou défenseur (mode="siege_defend_deploy") vient de valider
        /// son placement, voir MatchSessionManager_Siege.cs. Jamais de match live derrière : le
        /// serveur ferme la connexion juste après (comme zone_captured/zone_attack_result), d'où la
        /// réutilisation du même drapeau/événement OnZoneResult (déjà écouté par ZoneMapController
        /// pour afficher un écran de résultat) plutôt que d'inventer un nouvel écran pour ça.</summary>
        private void OnSiegeDeployAck(NetMessage msg)
        {
            expectingCloseAfterZoneResult = true;
            string message;
            if (msg.success)
            {
                message = currentMode == "siege_defend_deploy"
                    ? "Défense en place ! À la fin du siège, la bataille se jouera toute seule avec les troupes que vous venez de placer. Le résultat arrivera dans vos RAPPORTS."
                    : "Siège lancé ! Le propriétaire a maintenant 6 heures pour organiser sa défense. La bataille se jouera ensuite toute seule : le résultat arrivera dans vos RAPPORTS.";
            }
            else
            {
                message = msg.reason switch
                {
                    "siege_invalid" => "Ce siège n'existe plus (déjà terminé ou expiré).",
                    "server_busy" => "Le serveur est très occupé — réessayez dans un instant.",
                    _ => "Impossible d'envoyer vos troupes pour ce siège pour le moment. Réessayez dans un instant.",
                };
            }
            OnZoneResult?.Invoke(message, msg.success);
        }

        private int? preMatchExplorationTileX = null;
        private int? preMatchExplorationTileY = null;

        private void OnMatchFound(NetMessage msg)
        {
            // À partir d'ici la scène va être modifiée (carte de match, dock, unités) : tout retour au
            // QG passe par un rechargement propre, et "ANNULER" devient "QUITTER LA PARTIE".
            sceneDirtyFromMatch = true;
            quitConfirmArmed = false;
            RefreshCancelWaitButton();
            LostUnits.Clear();
            deployedRosterCountByType.Clear();
            localTeamId = msg.team_id;
            currentMode = string.IsNullOrEmpty(msg.mode) ? "deathmatch" : msg.mode;
            zoneProgressTeam1 = 0f;
            zoneProgressTeam2 = 0f;
            currentTurnNumber = 1;
            statusMessage = $"Adversaire trouvé : {msg.opponent_username}";

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MultiplayerMatchController] UnitSpawnerUI introuvable dans la scène.");
                return;
            }

            if (currentMode != "conquest" && Novgov.Generation.ZoneManager.Instance != null)
            {
                preMatchExplorationTileX = Novgov.Generation.ZoneManager.Instance.CurrentTileX;
                preMatchExplorationTileY = Novgov.Generation.ZoneManager.Instance.CurrentTileY;
            }

            if (currentMode == "conquest" || currentMode == "siege_attack_deploy" || currentMode == "siege_defend_deploy")
            {
                // La géométrie RÉELLE de la Zone attaquée (bâtiments + sol + NavMesh) doit être
                // chargée sur CE client avant d'ouvrir le déploiement — sans ça, le joueur placerait
                // ses unités sur l'ancienne carte encore affichée à l'écran. Même chargement pour un
                // déploiement de siège (attaquant ou défenseur) : c'est la même vraie Zone GPS.
                statusMessage = "Chargement du quartier visé";
                StartCoroutine(LoadMatchMapThenOpenDeployment(true, msg.zone_tile_x, msg.zone_tile_y, msg.city_data_json));
                return;
            }

            // Deathmatch/Zone de Contrôle (2026-08-30, "des milliers de cartes") : le serveur peut
            // désormais assigner la vraie tuile GPS d'un des deux joueurs (msg.has_home_tile) au lieu
            // de toujours la carte par défaut fixe — voir MatchSessionManager.TryStartMatch/
            statusMessage = "Chargement du champ de bataille...";
            StartCoroutine(LoadMatchMapThenOpenDeployment(msg.has_home_tile, msg.zone_tile_x, msg.zone_tile_y, msg.city_data_json));
        }

        /// <summary>Charge la carte réellement choisie par le serveur pour cette partie (voir
        /// OnMatchFound) puis ouvre le déploiement — généralisation du chargement de Zone déjà
        /// utilisé par la Conquête (2026-08-30, "des milliers de cartes") : <paramref name="hasRealTile"/>
        /// distingue une vraie tuile GPS (Conquête toujours, Deathmatch/Zone de Contrôle si le serveur
        /// en a assigné une) de la carte par défaut fixe. Délai d'attente ADAPTATIF : court pour la
        /// carte par défaut (déjà en mémoire locale), long pour une vraie tuile (peut nécessiter un
        /// fetch OSM + bake NavMesh côté serveur avant que le résultat n'arrive, jusqu'à ~60s — voir
        /// MatchSessionManager.GenerateAndCacheTile). Échec EXPLICITE si la carte n'est jamais prête,
        /// plutôt que d'enchaîner quand même sur le déploiement avec l'ancienne ville encore affichée
        /// (lacune de l'ancien code, plus probable désormais avec une vraie dépendance réseau).</summary>
        /// <summary>Lit le bâtiment HQ RÉEL de cette Zone depuis public.zones.hq_building_index
        /// (désigné une fois par le serveur, voir MatchSessionManager_Conquest.EnsureHqBuildingIndex)
        /// — remplace l'ancien SupabaseDatabaseClient.GetBuilding (PlayerPrefs LOCAL à cet appareil,
                /// l'autre" : les deux clients d'un même match lisent maintenant la MÊME ligne en base.</summary>
        private async System.Threading.Tasks.Task LoadZoneWithHqAsync(int tileX, int tileY, string json)
        {
            var (ok, zone) = await Novgov.Auth.SupabaseDatabaseClient.GetZoneInfo(tileX, tileY, CityGenerator.ZONE_ZOOM);
            var cityGen = FindAnyObjectByType<CityGenerator>();
            if (cityGen != null) cityGen.HQBuildingIndex = (ok && zone != null) ? zone.hq_building_index : -1;
            Novgov.Generation.ZoneManager.EnsureInstance().LoadZoneFromServerData(tileX, tileY, json);
        }

        private IEnumerator LoadMatchMapThenOpenDeployment(bool hasRealTile, int tileX, int tileY, string serverCityDataJson)
        {
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();

            if (hasRealTile && !string.IsNullOrEmpty(serverCityDataJson))
            {
                _ = LoadZoneWithHqAsync(tileX, tileY, serverCityDataJson);
            }
            else
            {
                MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
                if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
                if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            }

            const float MapLoadMaxWaitSeconds = 300f;
            float maxWait = MapLoadMaxWaitSeconds;
            while (cityGen != null && !cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            if (cityGen != null && !cityGen.IsCityReady)
            {
                Debug.LogError($"[MultiplayerMatchController] Carte non prête après {MapLoadMaxWaitSeconds:F0}s (tuile réelle={hasRealTile}) — abandon, jamais d'ouverture du déploiement sur une carte non confirmée.");
                statusMessage = "Échec du chargement de la carte — nouvelle tentative nécessaire.";
                GameServerClient.Instance?.Disconnect("map_load_failed");
                yield break;
            }

            yield return VerifyCityGeometryWithServer(cityGen);

            OpenDeploymentDock();
        }

        private bool awaitingCityVerifyResult = false;

        /// <summary>Calcule le hash de la ville que CE client vient de générer localement et le
        /// compare à la référence du serveur AVANT que le dock de déploiement ne s'ouvre — voir
        /// TacticalGridBuilder.ComputeBuildingListHash. Ne bloque JAMAIS indéfiniment : un serveur qui
        /// ne répond pas dans les 15s (ancienne version sans ce message, coupure réseau ponctuelle)
        /// laisse la partie continuer sur la géométrie locale plutôt que de bloquer le déploiement —
        /// ce garde-fou est un filet de sécurité, pas une exigence bloquante.</summary>
        private IEnumerator VerifyCityGeometryWithServer(CityGenerator cityGen)
        {
            if (cityGen == null) yield break;

            var localState = Novgov.TacticalCore.TacticalGridBuilder.BuildFromScene();
            int localHash = Novgov.TacticalCore.TacticalGridBuilder.ComputeBuildingListHash(localState.buildings);

            awaitingCityVerifyResult = true;
            GameServerClient.Instance.Send(new NetMessage
            {
                type = "city_verify",
                city_building_hash = localHash,
                city_building_count = localState.buildings.Count
            });

            const float CityVerifyMaxWaitSeconds = 15f;
            float wait = CityVerifyMaxWaitSeconds;
            while (awaitingCityVerifyResult && wait > 0f && (GameServerClient.Instance?.IsConnected ?? false))
            {
                wait -= Time.deltaTime;
                yield return null;
            }

            if (awaitingCityVerifyResult)
            {
                Debug.LogWarning("[MultiplayerMatchController] Pas de city_verify_result reçu à temps — on continue sur la géométrie locale (filet de sécurité, jamais bloquant).");
                awaitingCityVerifyResult = false;
            }

            // Si city_verify_result a déclenché une resynchronisation (voir OnCityVerifyResult), le
            // dock de déploiement ne doit s'ouvrir qu'une fois la ville reconstruite — jamais pendant.
            while (resyncInProgress) yield return null;
        }

        private void OnCityVerifyResult(NetMessage msg)
        {
            awaitingCityVerifyResult = false;

            if (msg.success)
            {
                Debug.Log("[MultiplayerMatchController] Géométrie de carte confirmée identique au serveur.");
                return;
            }

            int count = msg.city_buildings?.Length ?? 0;
            Debug.LogWarning($"[MultiplayerMatchController] Géométrie de carte DIVERGENTE détectée par le serveur — resynchronisation depuis sa structure autoritaire ({count} bâtiments).");
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (cityGen == null || msg.city_buildings == null)
            {
                Debug.LogError("[MultiplayerMatchController] Resynchronisation impossible (CityGenerator ou city_buildings absent) — la partie continue sur une géométrie potentiellement divergente.");
                return;
            }

            resyncInProgress = true;
            cityGen.ApplyAuthoritativeBuildings(ConvertToTacticalBuildings(msg.city_buildings), () => resyncInProgress = false);
        }

        /// <summary>NetMessage.BuildingGeometryDto (format réseau plat) -> Novgov.TacticalCore.
        /// TacticalBuilding (type déjà partagé serveur/TacticalGridBuilder) — la hauteur de fenêtre
        /// (Y) n'est pas transmise, voir NetMessage.WindowGeometryDto, CityGenerator.
        /// ApplyAuthoritativeBuildings la recalcule avec la même formule déterministe que la
        /// génération normale.</summary>
        private static List<Novgov.TacticalCore.TacticalBuilding> ConvertToTacticalBuildings(BuildingGeometryDto[] dtos)
        {
            var result = new List<Novgov.TacticalCore.TacticalBuilding>(dtos.Length);
            foreach (var dto in dtos)
            {
                result.Add(new Novgov.TacticalCore.TacticalBuilding
                {
                    id = dto.id,
                    height = dto.height,
                    footprint = (dto.footprint ?? System.Array.Empty<Vector2Data>())
                        .Select(p => new Vector2(p.x, p.y)).ToList(),
                    doors = (dto.doors ?? System.Array.Empty<DoorGeometryDto>())
                        .Select(d => new Novgov.TacticalCore.TacticalDoor
                        {
                            position = new Vector2(d.position.x, d.position.y),
                            entryDirection = new Vector2(d.entry_direction.x, d.entry_direction.y),
                            width = d.width
                        }).ToList(),
                    windows = (dto.windows ?? System.Array.Empty<WindowGeometryDto>())
                        .Select(w => new Novgov.TacticalCore.TacticalWindow
                        {
                            id = w.id,
                            position = new Vector2(w.position.x, w.position.y),
                            outwardNormal = new Vector2(w.outward_normal.x, w.outward_normal.y),
                            floorLevel = w.floor_level
                        }).ToList()
                });
            }
            return result;
        }

        /// <summary>Vrai pendant la reconstruction de ville déclenchée par OnCityVerifyResult —
        /// VerifyCityGeometryWithServer attend aussi la fin de CETTE étape (pas seulement la
        /// réception du message) avant de laisser le dock de déploiement s'ouvrir : ouvrir le dock
        /// pendant que la ville est en cours de démolition/reconstruction laisserait le joueur
        /// déployer sur un champ de bataille à moitié détruit.</summary>
        private bool resyncInProgress = false;

        /// <summary>Placement manuel (voir 03-network-protocol.md, "submit_deployment"/
        /// "deployment_result") : chaque joueur choisit où poser sa PROPRE escouade, dans son propre
        /// dock, verrouillé sur son camp — voir UnitSpawnerUI.OpenDockForMultiplayerDeployment. Le
        /// serveur valide et diffuse le résultat final via "deployment_result" (OnDeploymentResult),
        /// qui est ce qui spawn réellement les unités sur CE client — y compris les siennes, au cas
        /// où le serveur ait dû recadrer une position hors de la zone légale.</summary>
        private void OpenDeploymentDock()
        {
            _ = Novgov.Auth.SupabaseDatabaseClient.GetRoster();

            UnitSpawnerUI.Instance.ClearAllUnits();
            UnitSpawnerUI.Instance.maxUnitsPerTeam = Novgov.Server.MatchSessionManager.MaxDeployedCombatUnits;
            UnitSpawnerUI.Instance.OpenDockForMultiplayerDeployment(localTeamId);

            // Centrage de la caméra sur la zone de déploiement du joueur local — chaque camp regarde
            // son propre coin, et l'orientation doit pointer vers le centre de la carte pour ce coin.
            if (TacticalCamera.Instance != null)
            {
                Vector3 center = localTeamId == 1 ? new Vector3(-25f, 0f, -25f) : new Vector3(25f, 0f, 25f);
                TacticalCamera.Instance.focusPosition = center;
                // L'équipe 2 déploie depuis le coin Nord-Est, on tourne la caméra à 180° pour faire face au champ de bataille.
                TacticalCamera.Instance.currentYaw = localTeamId == 2 ? 225f : 45f;
            }
            IsDeploymentPhaseActive = true;

            MusicManager.SetGameplayVolume();
            SetUiState(UiState.Deployment);

            // Signale au serveur que CE client a fini de charger sa carte et voit maintenant
            // réellement son dock de déploiement — voir MatchSessionManager.RunDeploymentPhase, qui
            // attend ce signal des DEUX joueurs avant de démarrer le vrai compte à rebours de 45s
            // (sinon le chargement de ville pouvait à lui seul consommer tout le timer, voir rapport
            GameServerClient.Instance.Send(new NetMessage { type = "deployment_ready" });
        }

        /// <summary>Rassemble le placement local (unités + barricades de mon seul camp) et l'envoie
        /// au serveur — appelé par UnitSpawnerUI quand le joueur tape "CONFIRMER LE DÉPLOIEMENT".
        /// Le dock se cache aussitôt (IsDeploymentPhaseActive=false) : toute modification locale
        /// après ce point ne serait de toute façon jamais transmise au serveur.</summary>
        public void SubmitLocalDeployment()
        {
            var placements = new List<UnitPlacement>();
            foreach (var unit in UnitAI.AllLivingUnits)
            {
                if (unit.teamID != localTeamId) continue;
                placements.Add(new UnitPlacement
                {
                    unit_type = InferUnitType(unit),
                    x = unit.transform.position.x,
                    y = unit.transform.position.y,
                    z = unit.transform.position.z
                });
            }
            foreach (var barrier in RoadBarrier.AllBarriers)
            {
                if (barrier == null || barrier.teamID != localTeamId) continue;
                placements.Add(new UnitPlacement
                {
                    unit_type = (int)UnitSpawnerUI.UnitType.BarricadeRoutiere,
                    x = barrier.transform.position.x,
                    y = barrier.transform.position.y,
                    z = barrier.transform.position.z
                });
            }

            GameServerClient.Instance.Send(new NetMessage { type = "submit_deployment", placements = placements.ToArray() });

            IsDeploymentPhaseActive = false;
            statusMessage = "Déploiement envoyé — en attente de l'adversaire...";
            SetUiState(UiState.Matchmaking);
        }

        private static int InferUnitType(UnitAI u)
        {
            if (u.isMortar) return (int)UnitSpawnerUI.UnitType.Mortier;
            if (u.isCanonVehicle) return (int)UnitSpawnerUI.UnitType.VehiculeCanon;
            if (u.isTank) return (int)UnitSpawnerUI.UnitType.CharLeopard;
            return (int)UnitSpawnerUI.UnitType.Fantassin;
        }

        /// <summary>Positions FINALES validées par le serveur pour les DEUX camps — spawn réellement
        /// les unités sur ce client (y compris les miennes, jamais mes propres positions candidates
        /// locales, au cas où le serveur ait dû les recadrer) puis démarre la partie.</summary>
        private void OnDeploymentResult(NetMessage msg)
        {
            // Le déploiement est terminé qu'il vienne d'une soumission manuelle (SubmitLocalDeployment,
            // qui met déjà ce flag à false) OU d'un repli automatique côté serveur après expiration du
            // timer (le joueur n'a alors JAMAIS cliqué "CONFIRMER", donc ce flag restait bloqué à true) —
            // sans cette ligne, le dock de déploiement (UnitSpawnerUI) restait affiché EN PERMANENCE
            // par-dessus le HUD de combat pour le reste du match (superposition de boutons/texte
            IsDeploymentPhaseActive = false;

            UnitSpawnerUI.Instance.ClearAllUnits();

            // Le dock de déploiement plafonnait volontairement à 4 (voir OpenDeploymentDock) pour
            // empêcher LE JOUEUR de placer plus que son budget — mais ce plafond partagé bloquerait
            // aussi l'apparition dynamique d'une garnison de conquête renforcée (jusqu'à 4+3=7 unités,
            // voir MatchSessionManager.GarrisonExtraInfantryForZoneCount) au moment où ses unités sont
            // repérées en jeu (voir PlaySnapshotsCoroutine). Relevé une fois le déploiement soumis :
            // le joueur ne peut de toute façon plus placer de nouvelles unités passé ce point.
            UnitSpawnerUI.Instance.maxUnitsPerTeam = 8;

            deployedRosterCountByType.Clear();
            if (msg.deployed_units != null)
            {
                foreach (var u in msg.deployed_units)
                {
                    var type = (UnitSpawnerUI.UnitType)u.unit_type;
                    Vector3 pos = new Vector3(u.x, u.y, u.z);
                    UnitSpawnerUI.Instance.SpawnUnitAt(type, pos, u.team_id, forcedName: u.unit_id, skipSafeSpawnAdjustment: true);

                    // Composition RÉELLE et AUTORITAIRE (validée/recadrée par le serveur) de MON
                    // camp, capturée ici — voir OnMatchOver, qui la compare à l'effectif encore
                    // vivant en fin de partie pour déduire les pertes (voir deployedRosterCountByType).
                    // Barricades exclues : ni suivies par la caserne (SupabaseDatabaseClient.
                    // KnownUnitTypes), ni des UnitAI (jamais dans AllLivingUnits, donc toujours
                    // "0 survivante" — fausserait le calcul sans que rien ne lise ce résultat).
                    if (u.team_id == localTeamId && type != UnitSpawnerUI.UnitType.BarricadeRoutiere)
                    {
                        string key = type.ToString();
                        deployedRosterCountByType.TryGetValue(key, out int cur);
                        deployedRosterCountByType[key] = cur + 1;
                    }
                }
            }

            // Le client ne simule jamais de mouvement localement en multijoueur : les NavMeshAgent
            // sont désactivés pour ne jamais entrer en conflit avec les positions reçues du serveur.
            foreach (var unit in UnitAI.AllLivingUnits)
            {
                unit.isPlayerControlled = (unit.teamID == localTeamId);
                var agent = unit.GetComponent<NavMeshAgent>();
                if (agent != null) agent.enabled = false;
            }

            IsActive = true;
            if (TacticalPathManager.Instance != null)
                TacticalPathManager.Instance.phaseActuelle = TacticalPathManager.GamePhase.Planification;
            MusicManager.SetGameplayVolume();

            // Préchauffe le cache de géométrie de TacticalGridBuilder (voir
            // TacticalPathManager_PathDrawing.AppendGridPathSegment) MAINTENANT plutôt que d'attendre
            // le premier tracé de trajectoire du joueur : le tout premier appel construit ~1500
            // segments de mur + la grille de marche depuis zéro (~2s mesurées côté serveur pour cette
            // même carte) — sans ce préchauffage, la toute première ligne bleue dessinée par le
            // joueur aurait figé l'interface pendant ce laps de temps.
            Novgov.TacticalCore.TacticalGridBuilder.BuildFromScene();

            if (msg.reason == "roster_trimmed")
            {
                statusMessage = "Une partie de votre déploiement dépassait le budget autorisé (unités trop lourdes) — le reste a été posé tel quel.";
            }

            SetUiState(UiState.InMatch);
        }

        private void OnOpponentGhosted(NetMessage msg)
        {
            bool realAiRan = currentMode == "conquest" || currentMode == "practice_ai";
            string who = msg.team_id == localTeamId ? "Vous étiez" : "Adversaire";
            string pronoun = msg.team_id == localTeamId ? "vos" : "ses";
            ghostBannerText = realAiRan
                ? $"{who} absent — une IA de secours a joué {pronoun} unités ce tour-ci."
                : $"{who} absent — {pronoun} unités ont tenu leur position ce tour-ci (aucun ordre, mais ripostent si attaquées).";
            ghostBannerTimer = 4f;
            if (ghostBannerLabel != null)
            {
                ghostBannerLabel.text = ghostBannerText;
                ghostBannerLabel.style.display = DisplayStyle.Flex;
            }
        }

                /// réellement déployée pour mon camp (deployedRosterCountByType, capturée à
        /// OnDeploymentResult depuis la liste AUTORITAIRE du serveur) à l'effectif ENCORE VIVANT de
        /// ce même camp à l'instant précis de la fin de partie. Avant ce correctif, LostUnits n'était
        /// JAMAIS écrit nulle part dans tout le projet : ProcessLostUnitsAsync ne faisait donc
        /// jamais rien (son unique garde, `if (LostUnits.Count == 0) return;`, était toujours vraie),
        /// et aucune perte au combat n'était jamais déduite de la caserne — un joueur pouvait
        /// redéployer indéfiniment des unités pourtant mortes en match précédent.
        ///
        /// Approche par DIFFÉRENCE d'effectif plutôt que par un nouveau message serveur listant les
        /// morts une à une : ne nécessite aucun changement de protocole réseau, et reste correct même
        /// si une unité change de représentation entre temps (elle est simplement soit vivante, soit
        /// non, à cet instant précis).</summary>
        private void ComputeLostUnitsFromDeployedVsAlive()
        {
            var aliveCountByType = new Dictionary<string, int>();
            foreach (var u in UnitAI.AllLivingUnits)
            {
                if (u == null || u.isDead || u.teamID != localTeamId) continue;
                aliveCountByType.TryGetValue(u.sourceUnitType, out int cur);
                aliveCountByType[u.sourceUnitType] = cur + 1;
            }

            foreach (var kv in deployedRosterCountByType)
            {
                aliveCountByType.TryGetValue(kv.Key, out int stillAlive);
                int lost = kv.Value - stillAlive;
                if (lost > 0) LostUnits[kv.Key] = lost;
            }
        }

                /// (PlayerPrefs, supprimé — voir SupabaseDatabaseClient.cs, la caserne est maintenant une
        /// vraie table serveur, public.player_roster, verrouillée en écriture directe). Ne fait plus
        /// rien : le déploiement ne dépend plus d'un stock d'unités possédées (voir
        /// UnitSpawnerUI.StartPlacingUnit, demande explicite "laisse-moi déployer tout"), donc la
        /// perte définitive d'une unité au combat n'a plus de conséquence à répercuter ici. LostUnits/
        /// ComputeLostUnitsFromDeployedVsAlive restent calculés (inoffensif) au cas où une vraie
        /// conséquence de perte serait réintroduite plus tard.</summary>
        private System.Threading.Tasks.Task ProcessLostUnitsAsync() => System.Threading.Tasks.Task.CompletedTask;

        private IEnumerator DeferredMatchOver(NetMessage msg)
        {
            // Attendre la fin du rejeu (tour final) s'il y en a un en cours, pour ne pas couper
            // brutalement l'animation de mort de la dernière unité et cacher le champ de bataille
            // derrière l'écran de fin.
            while (isPlayingSnapshots)
            {
                yield return null;
            }

            if (TacticalPathManager.Instance != null)
                TacticalPathManager.Instance.ForceCloseTacticalUIForMatchEnd();

            OnMatchOver(msg);
        }

        private void OnMatchOver(NetMessage msg)
        {
            // Restauration de la vue d'exploration si on l'avait sauvegardée (Deathmatch)
            if (preMatchExplorationTileX.HasValue && preMatchExplorationTileY.HasValue && currentMode != "conquest")
            {
                if (Novgov.Generation.ZoneManager.Instance != null)
                {
                    Novgov.Generation.ZoneManager.Instance.LoadZone(preMatchExplorationTileX.Value, preMatchExplorationTileY.Value);
                }
                preMatchExplorationTileX = null;
                preMatchExplorationTileY = null;
            }

            bool isVictory = msg.winner_team == localTeamId;


            IsActive = false;
            string resultText;
            if (currentMode == "conquest")
            {
                // Retour spécifique conquête (voir MatchSessionManager.RunConquestSkirmish) : jusqu'ici
                // le client ignorait totalement zone_tile_x/y et success, affichant le même "VICTOIRE
                // !"/"DÉFAITE." générique qu'un deathmatch — sans jamais dire au joueur SI la Zone a
                // réellement été conquise, ni pourquoi pas en cas de victoire militaire "perdue"
                // (course avec un autre attaquant, voir reason="zone_lost_race").
                if (msg.winner_team == 0)
                    resultText = "Partie interrompue.";
                else if (msg.success)
                    resultText = $"VICTOIRE ! Le {Novgov.UI.QuartierText.Name(msg.zone_tile_x, msg.zone_tile_y)} est à vous.";
                else if (msg.winner_team == localTeamId && msg.reason == "zone_lost_race")
                    resultText = "Garnison vaincue, mais un autre joueur a pris ce quartier juste avant vous.";
                else if (msg.winner_team == localTeamId)
                    resultText = "Garnison vaincue.";
                else
                    resultText = "DÉFAITE — ce quartier reste aux mains de son propriétaire.";
            }
            else if (currentMode == "practice_ai")
            {
                // Entraînement hors-score (voir MatchSessionManager.RunPracticeVsAI) : jamais de
                // texte "Classement" en dessous (your_new_rating reste à 0, voir ratingText), pour
                // ne pas laisser croire que cette partie compte.
                resultText = msg.winner_team == 0 ? "Entraînement interrompu."
                    : msg.winner_team == localTeamId ? "VICTOIRE contre l'IA ! (Entraînement)"
                    : "DÉFAITE contre l'IA. (Entraînement)";
            }
            else
            {
                resultText = msg.winner_team == 0 ? "Partie interrompue."
                    : msg.winner_team == localTeamId ? "VICTOIRE !"
                    : "DÉFAITE.";
            }
            string ratingText = msg.your_new_rating > 0
                ? $"Classement : {msg.your_new_rating} ({(msg.rating_delta >= 0 ? "+" : "")}{msg.rating_delta})"
                : "";

            if (resultLabel != null) resultLabel.text = resultText;
            if (ratingLabel != null) ratingLabel.text = ratingText;
            SetUiState(UiState.MatchOver);
        }

        /// <summary>Met le tour reçu en cache et accuse immédiatement réception (voir
        /// "turn_result_ack" côté serveur, MatchSessionManager_CombatRealEngine.
        /// RunExecutionPhaseRealEngine) — ne lance PAS encore PlaySnapshotsCoroutine : la lecture
        /// n'est déclenchée que par le signal "turn_playback_start" du serveur (voir
        /// OnTurnPlaybackStart ci-dessous), une fois que les DEUX joueurs ont accusé réception. Sans
        /// cette étape, chaque client démarrait sa lecture dès que SON PROPRE payload (taille
        /// variable selon le brouillard de guerre) lui arrivait, désynchronisant les deux écrans d'un
                private void OnTurnResultReceived(NetMessage msg)
        {
            pendingTurnResult = msg;
            GameServerClient.Instance?.Send(new NetMessage { type = "turn_result_ack", turn_number = msg.turn_number });
            StartCoroutine(FallbackStartPlaybackIfServerNeverSignals(msg.turn_number));
        }

                /// un serveur pas encore reconstruit/redéployé avec ce correctif ne l'enverra JAMAIS, ce qui
        /// laissait le client bloqué sur "ACTION EN COURS..." pour toujours (rapport utilisateur :
        /// "j'ai action en cours mais rien ne se passe", juste après le déploiement de ce correctif
        /// alors que le serveur du VPS n'avait pas encore été reconstruit). Volontairement plus long
        /// que la fenêtre d'attente serveur (3s, voir MatchSessionManager_CombatRealEngine.
        /// RunExecutionPhaseRealEngine) : si le serveur EST à jour, OnTurnPlaybackStart aura déjà vidé
        /// pendingTurnResult avant que ce délai n'expire, et ce filet ne fait alors rien.</summary>
        private IEnumerator FallbackStartPlaybackIfServerNeverSignals(int turnNumber)
        {
            yield return new WaitForSeconds(5f);
            if (isPlayingSnapshots) yield break;
            if (pendingTurnResult == null || pendingTurnResult.turn_number != turnNumber) yield break;
            NetMessage toPlay = pendingTurnResult;
            pendingTurnResult = null;
            StartCoroutine(PlaySnapshotsCoroutine(toPlay));
        }

        /// <summary>Démarre RÉELLEMENT la lecture du tour mis en cache — voir OnTurnResultReceived.
        /// Le TCP garantit l'ordre de réception sur une même connexion, et le serveur n'envoie ce
        /// signal qu'APRÈS avoir reçu notre "turn_result_ack" (lui-même envoyé seulement après avoir
        /// entièrement reçu et désérialisé "turn_result") : pendingTurnResult est donc toujours déjà
        /// posé quand ce message arrive, sauf timeout serveur (adversaire disparu) où ce client peut
        /// recevoir ce signal sans jamais avoir eu de tour à lui-même rejouer (rien à faire alors).</summary>
        private void OnTurnPlaybackStart(NetMessage msg)
        {
            if (isPlayingSnapshots) return;
            if (pendingTurnResult == null || pendingTurnResult.turn_number != msg.turn_number) return;
            NetMessage toPlay = pendingTurnResult;
            pendingTurnResult = null;
            StartCoroutine(PlaySnapshotsCoroutine(toPlay));
        }

        /// <summary>Joue le tour reçu du serveur, en garantissant que le verrou isPlayingSnapshots
        /// est TOUJOURS relâché — voir le finally.</summary>
        private IEnumerator PlaySnapshotsCoroutine(NetMessage msg)
        {
            isPlayingSnapshots = true;
            currentTurnNumber = msg.turn_number + 1;

            return SafeCoroutineRunner.Run(
                PlaySnapshotsBody(msg),
                onComplete: () =>
                {
                    isPlayingSnapshots = false;
                },
                onException: (Exception e) =>
                {
                    Debug.LogError($"[MultiplayerMatchController] Exception pendant le rejeu du tour — récupération pour ne pas figer la partie : {e}");
                    RecoverFromFailedReplay();
                }
            );
        }

        /// <summary>Remet le client dans un état JOUABLE après un rejeu interrompu par une exception —
        /// même effet que la fin normale de PlaySnapshotsBody. Sans ça, relâcher le seul verrou
        /// isPlayingSnapshots ne suffisait pas : phaseActuelle restait à Execution et toute la saisie
        /// tactique demeurait morte (voir TacticalPathManager.Update).</summary>
        private void RecoverFromFailedReplay()
        {
            isPlayingSnapshots = false;
            foreach (var unit in UnitAI.AllLivingUnits)
            {
                if (unit == null) continue;
                unit.ClearTacticalPath();
                unit.SetNetworkAnimState(false, 0f);
            }
            if (TacticalPathManager.Instance != null)
                TacticalPathManager.Instance.phaseActuelle = TacticalPathManager.GamePhase.Planification;
            statusMessage = "";
        }

        private IEnumerator PlaySnapshotsBody(NetMessage msg)
        {
            // Construction défensive (2026-09-20) : un ToDictionary() direct plantait tout le rejeu
            // du tour ("An item with the same key has already been added") si jamais deux UnitAI de
            // la scène partageaient exactement le même nom — normalement empêché en amont désormais
            // (voir UnitSpawnerUI.SpawnUnitAt, garde anti-doublon sur forcedName), mais une boucle
            // manuelle qui ignore un doublon résiduel au lieu de lever une exception non rattrapable
            // garantit que la partie ne se bloque plus jamais pour cette seule raison.
            var unitLookup = new Dictionary<string, UnitAI>();
            foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude))
            {
                string key = u.gameObject.name;
                if (unitLookup.ContainsKey(key))
                {
                    Debug.LogWarning($"[MultiplayerMatchController] Unité en double détectée dans la scène pour le nom '{key}' — ignorée pour ne pas bloquer le rejeu du tour.");
                    continue;
                }
                unitLookup[key] = u;
            }
            var previousPositions = new Dictionary<string, Vector3>();

            float intervalSec = Mathf.Max(0.02f, msg.snapshot_interval_ms / 1000f);

            foreach (Snapshot snap in msg.snapshots)
            {
                // Brouillard de guerre (voir MatchSessionManager.ComputeVisibleUnitIds) : ce snapshot
                // ne contient déjà plus que les unités que MON camp peut voir à cet instant — une
                // unité adverse absente de snap.units est soit pas encore repérée, soit plus repérée
                // (elle a pu l'être à un tick précédent). "visibleThisTick" sert à masquer, en fin de
                // tick, toute unité adverse déjà apparue mais qui n'y figure plus.
                var visibleThisTick = new HashSet<string>();

                var lerpFromPos = new Dictionary<UnitAI, Vector3>();
                var lerpFromRot = new Dictionary<UnitAI, Quaternion>();
                var lerpToPos = new Dictionary<UnitAI, Vector3>();
                var lerpToRot = new Dictionary<UnitAI, Quaternion>();

                var shotsThisTick = new List<(UnitAI shooter, string targetId)>();

                foreach (UnitState state in snap.units)
                {
                    visibleThisTick.Add(state.unit_id);
                    bool justSpawned = false;

                    if (!unitLookup.TryGetValue(state.unit_id, out UnitAI unit) || unit == null)
                    {
                        // Première apparition de cette unité côté client : elle vient d'être repérée
                        // et n'existe pas encore en scène (deployment_result ne contenait que ma
                        // propre équipe, voir MatchSessionManager.RunDeploymentPhase) — on la fait
                        // apparaître directement à sa position révélée.
                        var newType = (UnitSpawnerUI.UnitType)state.unit_type;
                        Vector3 spawnPos = new Vector3(state.x, state.y, state.z);
                        unit = UnitSpawnerUI.Instance.SpawnUnitAt(newType, spawnPos, state.team_id, forcedName: state.unit_id, skipSafeSpawnAdjustment: true);
                        if (unit == null) continue;
                        unitLookup[state.unit_id] = unit;
                        unit.isPlayerControlled = (unit.teamID == localTeamId);
                        var newAgent = unit.GetComponent<NavMeshAgent>();
                        if (newAgent != null) newAgent.enabled = false;
                        justSpawned = true;
                    }

                    unit.SetVisualsVisibility(true);

                    unit.teamID = state.team_id;
                    unit.isPlayerControlled = (state.team_id == localTeamId);

                    Vector3 newPos = new Vector3(state.x, state.y, state.z);
                    Quaternion newRot = Quaternion.Euler(0f, state.ry, 0f);
                    float moveSpeed = previousPositions.TryGetValue(state.unit_id, out Vector3 prevPos)
                        ? Vector3.Distance(prevPos, newPos) / intervalSec
                        : 0f;
                    previousPositions[state.unit_id] = newPos;

                    // Une unité qui vient d'apparaître (SpawnUnitAt) est déjà à newPos : rien à interpoler.
                    lerpFromPos[unit] = justSpawned ? newPos : unit.transform.position;
                    lerpFromRot[unit] = justSpawned ? newRot : unit.transform.rotation;
                    lerpToPos[unit] = newPos;
                    lerpToRot[unit] = newRot;

                    unit.SetNetworkHealth(state.health);
                    unit.SetNetworkAnimState(state.shooting, moveSpeed);
                    if (state.dead) unit.ApplyNetworkDeath();

                    if (state.shooting && !string.IsNullOrEmpty(state.shoot_target_id))
                        shotsThisTick.Add((unit, state.shoot_target_id));
                }

                // Effets cosmétiques du tir (voir UnitAI.PlayNetworkShotEffects/PlayNetworkHitReaction) :
                // exécuté ICI, transforms encore à leur position d'AVANT ce tick — c'est précisément
                // l'instant où le coup part côté serveur (voir TacticalResolver, Kind.Shot).
                foreach (var (shooter, targetId) in shotsThisTick)
                {
                    if (shooter == null) continue;
                    if (!unitLookup.TryGetValue(targetId, out UnitAI target) || target == null) continue;
                    shooter.PlayNetworkShotEffects(target.transform.position);
                    Vector3 hitDir = (target.transform.position - shooter.transform.position).normalized;
                    target.PlayNetworkHitReaction(hitDir);
                }

                if (snap.destroyed_building_ids != null)
                {
                    foreach (int buildingId in snap.destroyed_building_ids)
                    {
                        if (buildingId < 0 || buildingId >= BuildingStructure.AllBuildings.Count) continue;
                        BuildingStructure bs = BuildingStructure.AllBuildings[buildingId];
                        DestructibleEnvironment env = bs != null ? bs.GetComponent<DestructibleEnvironment>() : null;
                        env?.ApplyNetworkDestruction();
                    }
                }

                if (snap.destroyed_barrier_ids != null)
                {
                    foreach (string barrierName in snap.destroyed_barrier_ids)
                    {
                        RoadBarrier barrier = RoadBarrier.AllBarriers.Find(b => b.gameObject.name == barrierName);
                        if (barrier != null) barrier.RemoveByPlayer();
                    }
                }

                // Toute unité ENNEMIE déjà apparue mais absente de CE tick n'est plus repérée à cet
                // instant précis — masquée, jamais détruite (elle peut réapparaître dès qu'elle
                // redevient visible). Mes propres unités sont toujours incluses dans snap.units (voir
                // ComputeVisibleUnitIds, "sa propre équipe est toujours visible pour elle-même"),
                // donc jamais concernées par cette boucle. Exception : un ennemi déjà MORT n'est
                // jamais masqué même s'il disparaît des ticks suivants — voir RunExecutionPhase,
                // "allUnits" y exclut les unités mortes dès le tour SUIVANT leur mort (elles ne
                // participent plus au calcul), donc un cadavre ennemi repéré au tour où il meurt
                // n'apparaît plus jamais dans aucun snapshot après coup ; sans ce garde, il
                // redisparaissait silencieusement du champ de bataille un tour après sa mort, comme
                // si le corps avait été retiré (trouvé en simulant une partie grandeur nature,
                // 2026-08-30) — un cadavre est statique et inerte, il n'y a aucune raison de le
                // "re-cacher" une fois déjà vu mort.
                foreach (var kv in unitLookup)
                {
                    if (kv.Value != null && kv.Value.teamID != localTeamId && !visibleThisTick.Contains(kv.Key) && !kv.Value.isDead)
                        kv.Value.SetVisualsVisibility(false);
                }

                zoneProgressTeam1 = snap.zone_progress_team1;
                zoneProgressTeam2 = snap.zone_progress_team2;

                float elapsed = 0f;
                while (elapsed < intervalSec)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / intervalSec);
                    foreach (var kv in lerpToPos)
                    {
                        UnitAI u = kv.Key;
                        if (u == null) continue;
                        u.transform.position = Vector3.Lerp(lerpFromPos[u], kv.Value, t);
                        u.transform.rotation = Quaternion.Slerp(lerpFromRot[u], lerpToRot[u], t);
                    }
                    yield return null;
                }
                // Rattrape tout retard d'arrondi de Time.deltaTime : la position finale du tick doit être
                // EXACTEMENT celle du serveur avant que le tick suivant ne reprenne depuis ce point.
                foreach (var kv in lerpToPos)
                {
                    if (kv.Key != null) kv.Key.transform.position = kv.Value;
                }
            }

            foreach (var unit in UnitAI.AllLivingUnits)
            {
                unit.ClearTacticalPath();
                unit.SetNetworkAnimState(false, 0f);
            }

            if (TacticalPathManager.Instance != null)
                TacticalPathManager.Instance.phaseActuelle = TacticalPathManager.GamePhase.Planification;

            statusMessage = "";
        }

        // =====================================================================
        // UI Toolkit — câblage une fois, puis mise à jour ciblée des champs qui changent.
        // =====================================================================

                /// +50 AP une fois par jour UTC, suivi par profiles.last_daily_bonus_at — un vrai horodatage
        /// serveur, pas le PlayerPrefs local d'avant, remis à zéro par une simple réinstallation).
        /// Le bonus "+10 par bâtiment possédé" a été retiré : la possession d'une Zone rapporte
        /// maintenant un vrai revenu passif régulier côté serveur (MatchSessionManager.
        /// ZoneIncomeLoop), cumuler les deux aurait été redondant.</summary>
        private async System.Threading.Tasks.Task GrantDailyActionPoints()
        {
            if (Novgov.Auth.SupabaseAuthClient.CurrentSession == null) return;
            await Novgov.Auth.SupabaseDatabaseClient.ClaimDailyBonus();
            RefreshHubScreen();
        }

        private async void RefreshHubScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("Conquest");
            if (root == null) return;
            var lblUser = root.Q<Label>("lbl-username");
            var lblAP = root.Q<Label>("lbl-action-points");

            // Avis ponctuel (ex: raison d'une déconnexion en pleine partie) — voir pendingHubNotice.
            var lblNotice = root.Q<Label>("lbl-hub-notice");
            if (lblNotice != null)
            {
                lblNotice.text = pendingHubNotice ?? "";
                lblNotice.style.display = string.IsNullOrEmpty(pendingHubNotice) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            var (okProf, prof) = await Novgov.Auth.SupabaseDatabaseClient.GetProfile();
            if (okProf && prof != null)
            {
                if (lblUser != null) lblUser.text = $"Commandant {prof.username}";
                if (lblAP != null) lblAP.text = $"{prof.action_points} PA";
            }

            var lblNotifBadge = root.Q<Label>("lbl-notifications-badge");
            if (lblNotifBadge != null)
            {
                var (okNotif, notifs) = await Novgov.Auth.SupabaseDatabaseClient.GetUnreadNotifications();
                int unread = okNotif && notifs != null ? notifs.Length : 0;
                // Même pastille ".notif-badge" que la cloche du HUD tactique (TacticalBottomBarScreen) —
                // remplace le texte "Notifications (N)" par un compteur visuel cohérent avec le reste
                // du jeu plutôt qu'un traitement ad hoc propre à cet écran.
                lblNotifBadge.text = unread > 9 ? "9+" : unread.ToString();
                lblNotifBadge.style.display = unread > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Alerte "quartier assiégé" + pastilles SIÈGES/GESTION (2026-09-30) : un siège contre le
            // joueur est la seule chose du jeu qui a une échéance — elle reste visible en tête de la
            // Conquête (les deux onglets) avec son bouton DÉFENDRE.
            var siegeAlert = root.Q<VisualElement>("siege-alert");
            var lblSiegeAlert = root.Q<Label>("lbl-siege-alert");
            var lblSiegesBadge = root.Q<Label>("lbl-sieges-badge");
            var lblManageBadge = root.Q<Label>("lbl-manage-badge");
            string myUserId = Novgov.Auth.SupabaseAuthClient.CurrentSession?.user?.id;
            var (okSieges, sieges) = await Novgov.Auth.SupabaseDatabaseClient.GetMySieges();
            int defenseCount = 0;
            mostUrgentDefense = null;
            DateTime mostUrgentDeadline = DateTime.MaxValue;
            if (okSieges && sieges != null)
            {
                foreach (var s in sieges)
                {
                    if (s.defender_user_id != myUserId) continue;
                    defenseCount++;
                    DateTime deadline = DateTime.TryParse(s.deadline, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MaxValue;
                    if (mostUrgentDefense == null || deadline < mostUrgentDeadline)
                    {
                        mostUrgentDefense = s;
                        mostUrgentDeadline = deadline;
                    }
                }
            }
            if (siegeAlert != null)
            {
                siegeAlert.style.display = mostUrgentDefense != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (mostUrgentDefense != null && lblSiegeAlert != null)
                {
                    TimeSpan left = mostUrgentDeadline == DateTime.MaxValue ? TimeSpan.Zero : mostUrgentDeadline - DateTime.UtcNow;
                    string others = defenseCount > 1 ? $" (+{defenseCount - 1} autre(s) siège(s))" : "";
                    string rel = Novgov.UI.QuartierText.RelativeToHome(mostUrgentDefense.tile_x, mostUrgentDefense.tile_y);
                    string headline = rel == "de votre QG" ? "⚔ VOTRE QG EST ASSIÉGÉ" : $"⚔ UN DE VOS QUARTIERS ({rel}) EST ASSIÉGÉ";
                    lblSiegeAlert.text = left > TimeSpan.Zero
                        ? $"{headline} ! Il vous reste {(left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:D2}" : $"{left.Minutes} min")} pour placer vos troupes en défense — sinon votre garnison actuelle se défendra seule.{others}"
                        : $"{headline} — la bataille va se jouer d'un instant à l'autre.{others}";
                }
            }
            foreach (var badge in new[] { lblSiegesBadge, lblManageBadge })
            {
                if (badge == null) continue;
                badge.text = defenseCount > 9 ? "9+" : defenseCount.ToString();
                badge.style.display = defenseCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // =====================================================================
        // Petits éléments réutilisables pour les listes dynamiques (Notifications/Sièges/Bâtiments/

        private static VisualElement MakeHubCard()
        {
            var row = new VisualElement();
            row.AddToClassList("hub-card");
            return row;
        }

        private static VisualElement MakeTextColumn(string title, string subtitle = null)
        {
            var col = new VisualElement();
            col.style.flexShrink = 1;
            col.style.flexDirection = FlexDirection.Column;
            var lblTitle = new Label(title);
            lblTitle.AddToClassList("hub-card-title");
            col.Add(lblTitle);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var lblSub = new Label(subtitle);
                lblSub.AddToClassList("hub-card-subtitle");
                col.Add(lblSub);
            }
            return col;
        }

        private static Label MakeCountBadge(string text)
        {
            var badge = new Label(text);
            badge.AddToClassList("hub-count-badge");
            return badge;
        }

        /// <summary>Jauge de "pips" (1 par niveau, plein = déjà atteint) — remplace un texte brut
        /// "Niveau: X/max" par une jauge lisible d'un coup d'oeil, sans dépendre d'aucune texture
        /// chargée à l'exécution (voir le commentaire de ".hub-card" dans Theme.tss).</summary>
        private static VisualElement MakePipRow(int filled, int max)
        {
            var rowEl = new VisualElement();
            rowEl.AddToClassList("hub-pip-row");
            for (int i = 0; i < max; i++)
            {
                var pip = new VisualElement();
                pip.AddToClassList(i < filled ? "hub-pip--filled" : "hub-pip--empty");
                pip.AddToClassList("hub-pip");
                rowEl.Add(pip);
            }
            return rowEl;
        }

        /// <summary>"3h42" / "18min" restant(es) avant l'échéance ISO 8601 donnée — jamais négatif
        /// affiché (une échéance déjà expirée montre "résolution imminente", le siège attend juste
        /// le prochain passage de SiegeResolutionLoop côté serveur, voir MatchSessionManager_Siege.cs).</summary>
        private static string FormatCountdown(string deadlineIso, out bool urgent)
        {
            urgent = false;
            if (!DateTime.TryParse(deadlineIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime deadline))
                return "échéance inconnue";

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return "résolution imminente";

            urgent = remaining.TotalHours < 1;
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h{remaining.Minutes:D2} restant(es)"
                : $"{remaining.Minutes}min restantes";
        }

                /// §9) — jusqu'ici le serveur écrivait (WriteNotification) mais rien ne les lisait jamais.
        /// Pas de push : lues à l'ouverture du HUB (RefreshHubScreen) et sur ce bouton dédié,
        /// exactement comme demandé ("le joueur qui lance de temps en temps son appli pour voir la
        /// notif").</summary>
        private async void RefreshNotificationsScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("Notifications");
            if (root == null) return;
            var scroll = root.Q<ScrollView>("notifications-scroll");
            if (scroll == null) return;
            scroll.Clear();
            var lblLoading = new Label("Chargement des notifications...");
            lblLoading.AddToClassList("hint");
            scroll.Add(lblLoading);

            var (ok, list) = await Novgov.Auth.SupabaseDatabaseClient.GetUnreadNotifications();
            scroll.Clear();
            if (!ok || list == null || list.Length == 0)
            {
                var lbl = new Label("Aucun nouveau rapport. Vous serez prévenu ici quand une de vos Zones est attaquée ou quand un siège se termine.");
                lbl.AddToClassList("hint");
                lbl.style.whiteSpace = WhiteSpace.Normal;
                scroll.Add(lbl);
                return;
            }

            foreach (var n in list)
            {
                var row = MakeHubCard();
                // Texte serveur réécrit à l'affichage ("Zone (66648,44111)" -> "quartier au Nord de
                // votre QG"), voir Novgov.UI.QuartierText.
                row.Add(MakeTextColumn(Novgov.UI.QuartierText.HumanizeServerText(n.message)));

                // Un rapport "Zone attaquée" menait jusqu'ici à un simple "Marquer comme lue" : le
                // joueur devait deviner qu'il fallait revenir au hub puis ouvrir SIÈGES pour réagir.
                if (n.type == "under_attack")
                {
                    var btnGo = new Button();
                    btnGo.text = "VOIR LE SIÈGE";
                    btnGo.AddToClassList("btn-primary");
                    long goId = n.id;
                    btnGo.clicked += async () =>
                    {
                        btnGo.SetEnabled(false);
                        await Novgov.Auth.SupabaseDatabaseClient.MarkNotificationRead(goId);
                        UIScreenManager.Instance.Show("Sieges");
                        RefreshSiegesScreen();
                    };
                    row.Add(btnGo);
                }

                var btnRead = new Button();
                btnRead.text = "Lu";
                long notifId = n.id;
                btnRead.clicked += async () =>
                {
                    btnRead.SetEnabled(false);
                    await Novgov.Auth.SupabaseDatabaseClient.MarkNotificationRead(notifId);
                    RefreshNotificationsScreen();
                    RefreshHubScreen();
                };
                row.Add(btnRead);

                scroll.Add(row);
            }
        }

                /// "en attente de résolution") ou défenseur ("Défendre maintenant" ouvre le même dock de
        /// déploiement que l'attaquant, côté équipe 2 — voir MultiplayerMatchController.DefendSiege).
        /// Compte à rebours en direct (FormatCountdown) : rend l'urgence VISIBLE plutôt qu'un simple
        /// "répondez avant l'échéance" statique — au coeur du "gamefiable" demandé.</summary>
        private async void RefreshSiegesScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("Sieges");
            if (root == null) return;
            var scroll = root.Q<ScrollView>("sieges-scroll");
            if (scroll == null) return;
            scroll.Clear();
            var lblLoading = new Label("Chargement des sièges...");
            lblLoading.AddToClassList("hint");
            scroll.Add(lblLoading);

            string myUserId = Novgov.Auth.SupabaseAuthClient.CurrentSession?.user?.id;
            var (ok, list) = await Novgov.Auth.SupabaseDatabaseClient.GetMySieges();
            scroll.Clear();
            if (!ok || list == null || list.Length == 0)
            {
                var lbl = new Label("Aucun siège en cours. Pour prendre un quartier tenu par un autre joueur, ouvrez la CARTE et touchez un quartier rouge qui touche le vôtre.");
                lbl.AddToClassList("hint");
                lbl.style.whiteSpace = WhiteSpace.Normal;
                scroll.Add(lbl);

                // Écran vide = cul-de-sac : on donne directement le chemin vers l'action concernée.
                var btnOpenMap = new Button();
                btnOpenMap.text = "OUVRIR LA CARTE";
                btnOpenMap.AddToClassList("btn-secondary");
                btnOpenMap.style.marginTop = 12;
                btnOpenMap.clicked += () => Novgov.UI.ZoneMapController.EnsureInstance().Show();
                scroll.Add(btnOpenMap);
                return;
            }

            foreach (var s in list)
            {
                bool isDefender = s.defender_user_id == myUserId;
                string countdown = FormatCountdown(s.deadline, out bool urgent);

                var row = MakeHubCard();
                // Pas d'émoji ici (🛡/🏰 etc.) : plage Unicode "pictographes" sans glyphe de repli
                var col = MakeTextColumn(
                    isDefender ? $"⚔ {Novgov.UI.QuartierText.Title(s.tile_x, s.tile_y)} : assiégé !" : $"Votre siège : {Novgov.UI.QuartierText.Name(s.tile_x, s.tile_y)}",
                    isDefender
                        ? "On vous attaque. Placez vos troupes en défense avant la fin du compte à rebours — sinon votre garnison actuelle se défendra seule."
                        : "Vos troupes sont en place. Le propriétaire a jusqu'à la fin du compte à rebours pour se défendre, puis la bataille se joue toute seule.");
                var lblCountdown = new Label(countdown);
                lblCountdown.AddToClassList(urgent ? "hub-badge-urgent" : "hub-badge-ok");
                col.Add(lblCountdown);
                row.Add(col);

                if (isDefender)
                {
                    var btnDefend = new Button();
                    btnDefend.text = "DÉFENDRE";
                    int tileX = s.tile_x, tileY = s.tile_y;
                    long siegeId = s.id;
                    btnDefend.clicked += () => DefendSiege(tileX, tileY, siegeId);
                    row.Add(btnDefend);
                }

                scroll.Add(row);
            }
        }

        private async void RefreshBuildingsScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("Buildings");
            if (root == null) return;
            var scroll = root.Q<ScrollView>("buildings-scroll");
            if (scroll == null) return;
            scroll.Clear();
            var lblLoading = new Label("Chargement des territoires...");
            lblLoading.AddToClassList("hint");
            scroll.Add(lblLoading);

            var (ok, list) = await Novgov.Auth.SupabaseDatabaseClient.GetOwnedZones();
            scroll.Clear();
            if (!ok || list == null || list.Length == 0)
            {
                var lbl = new Label("Vous n'avez encore aucun quartier. Sur la CARTE, touchez un quartier LIBRE (personne ne l'occupe) et prenez-le : c'est gratuit, immédiat, et il vous rapportera des Points d'Action.");
                lbl.AddToClassList("hint");
                lbl.style.whiteSpace = WhiteSpace.Normal;
                scroll.Add(lbl);

                var btnOpenMap = new Button();
                btnOpenMap.text = "OUVRIR LA CARTE";
                btnOpenMap.AddToClassList("btn-secondary");
                btnOpenMap.style.marginTop = 12;
                btnOpenMap.clicked += () => Novgov.UI.ZoneMapController.EnsureInstance().Show();
                scroll.Add(btnOpenMap);
                return;
            }

            foreach(var z in list)
            {
                var row = MakeHubCard();

                bool shielded = DateTime.TryParse(z.shield_until, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime shieldUntil) && shieldUntil > DateTime.UtcNow;
                int incomePerCycle = z.building_level * 10; // voir MatchSessionManager.ZoneIncomePerZone (10 AP/niveau/5min)

                // Plus de coordonnées de tuile brutes ("Zone (66648,44111)") ni d'index interne
                // ("Bâtiment HQ #12") : illisibles pour un joueur (2026-09-30).
                var col = MakeTextColumn(
                    Novgov.UI.QuartierText.Title(z.tile_x, z.tile_y),
                    $"Niveau {z.building_level} — rapporte +{incomePerCycle} Points d'Action toutes les 5 min, automatiquement");
                col.Add(MakePipRow(z.building_level, 3));
                if (shielded)
                {
                    var lblShield = new Label($"Protégée encore {FormatCountdown(z.shield_until, out _)}");
                    lblShield.AddToClassList("hub-badge-shielded");
                    col.Add(lblShield);
                }
                row.Add(col);

                if (z.building_level < 3)
                {
                    var btnUpgrade = new Button();
                    int cost = z.building_level == 1 ? 200 : 500;
                    btnUpgrade.text = $"Améliorer ({cost} PA)";
                    int tileX = z.tile_x, tileY = z.tile_y;
                    btnUpgrade.clicked += async () =>
                    {
                        btnUpgrade.SetEnabled(false);
                        var (upgraded, newAp, newLevel, error) = await Novgov.Auth.SupabaseDatabaseClient.UpgradeBuilding(tileX, tileY, CityGenerator.ZONE_ZOOM);
                        if (!upgraded) Debug.LogWarning($"[MultiplayerMatchController] Amélioration de ({tileX},{tileY}) refusée : {error}");
                        RefreshBuildingsScreen();
                    };
                    row.Add(btnUpgrade);
                }
                else
                {
                    var lblMax = new Label("NIVEAU MAX");
                    lblMax.AddToClassList("hub-badge-ok");
                    row.Add(lblMax);
                }

                scroll.Add(row);
            }
        }

        private async void RefreshRosterScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("Roster");
            if (root == null) return;
            var scroll = root.Q<ScrollView>("roster-scroll");
            var lblPoints = root.Q<Label>("lbl-action-points");
            if (scroll == null) return;
            scroll.Clear();

            var (okProf, prof) = await Novgov.Auth.SupabaseDatabaseClient.GetProfile();
            int currentAp = prof?.action_points ?? 0;
            if (lblPoints != null) lblPoints.text = $"Solde : {currentAp} PA";

            var (ok, roster) = await Novgov.Auth.SupabaseDatabaseClient.GetRoster();

            string[] unitTypes = Novgov.Auth.SupabaseDatabaseClient.KnownUnitTypes;
            int[] unitCosts = Novgov.Auth.SupabaseDatabaseClient.KnownUnitCosts;
            // Un mot court par type pour que la carte se lise sans avoir à connaître le jeu par
            // coeur — jamais une icône chargée à l'exécution (voir Theme.tss ".hub-card").
            // MÊME ORDRE que KnownUnitTypes (Fantassin, VehiculeCanon, CharLeopard, Mortier, Drone).
            // 2026-09-30 : les descriptions du véhicule canon et du char étaient INVERSÉES — le Char
            // Léopard (500 PV, 150 dégâts, voir UnitTypeStats) était présenté comme "rapide, compromis"
            // et le véhicule canon (250 PV, plus rapide) comme "lourdement blindé".
            string[] unitBlurbs = {
                "Polyvalente, peu coûteuse — la base de toute escouade.",
                "Rapide, bon compromis mobilité/puissance de feu.",
                "Lourdement blindé, dégâts élevés — le poing de votre armée.",
                "Tir de zone à longue portée — ne s'engage jamais directement.",
                "Reconnaissance — pas encore disponible en combat.",
            };
            // Noms affichés : les identifiants internes ("VehiculeCanon", "CharLeopard") s'affichaient
            // tels quels.
            string[] unitDisplayNames = { "Fantassin", "Véhicule canon", "Char Léopard", "Mortier", "Drone" };

            for (int i = 0; i < unitTypes.Length; i++)
            {
                string uType = unitTypes[i];
                int cost = unitCosts[i];
                var item = roster != null ? System.Linq.Enumerable.FirstOrDefault(roster, r => r.unit_type.Equals(uType, System.StringComparison.OrdinalIgnoreCase)) : null;
                int qty = item != null ? item.quantity : 0;
                // Le drone n'est pas déployable (voir UnitSpawnerUI.UnitType : aucune entrée Drone) :
                // le vendre revenait à faire dépenser 200 PA pour une unité inutilisable.
                bool deployable = uType != "Drone";
                bool canAfford = deployable && currentAp >= cost;

                var row = MakeHubCard();
                var col = MakeTextColumn(i < unitDisplayNames.Length ? unitDisplayNames[i] : uType, i < unitBlurbs.Length ? unitBlurbs[i] : null);
                row.Add(col);

                row.Add(MakeCountBadge($"×{qty}"));

                var btnBuy = new Button();
                btnBuy.text = !deployable ? "BIENTÔT" : $"Recruter ({cost} PA)";
                btnBuy.AddToClassList(canAfford ? "btn-primary" : "btn-secondary");
                btnBuy.SetEnabled(canAfford);

                if (canAfford)
                {
                    btnBuy.clicked += async () =>
                    {
                        btnBuy.SetEnabled(false);
                        var (bought, newAp, newQty, error) = await Novgov.Auth.SupabaseDatabaseClient.BuyUnit(uType);
                        if (!bought)
                        {
                            Debug.LogWarning($"[MultiplayerMatchController] Achat de {uType} refusé : {error}");
                        }
                        RefreshRosterScreen();
                    };
                }

                row.Add(btnBuy);
                scroll.Add(row);
            }
        }

        private void BindUI()
        {
            if (uiBound) return;
            if (UIScreenManager.Instance == null)
            {
                Debug.LogError("[MultiplayerMatchController] UIScreenManager.Instance introuvable — UIBootstrap ne s'est-il pas exécuté avant cette scène ?");
                return;
            }
            uiBound = true;

            // --- CONQUÊTE : en-tête + onglet GESTION (refonte du 2026-09-30, ConquestScreen.uxml) --
            // L'onglet CARTE et le passage d'un onglet à l'autre sont gérés par
            // Novgov.UI.ZoneMapController.
            VisualElement conquestRoot = UIScreenManager.Instance.GetScreen("Conquest");

            Button siegeAlertDefendBtn = conquestRoot?.Q<Button>("btn-siege-alert-defend");
            if (siegeAlertDefendBtn != null)
            {
                siegeAlertDefendBtn.clicked += () =>
                {
                    var s = mostUrgentDefense;
                    if (s == null) return;
                    Novgov.UI.UiSfx.Play(Novgov.UI.UiSfx.Sound.RadioRoger);
                    DefendSiege(s.tile_x, s.tile_y, s.id);
                };
            }

            // Retour au menu Solo / En ligne en GARDANT la session (JOUER EN LIGNE ramène ici
            // directement) — distinct de la vraie déconnexion du compte (onglet GESTION). L'ancien
            // unique bouton "DÉCONNEXION" faisait le premier en annonçant le second.
            Button hubBackBtn = conquestRoot?.Q<Button>("btn-hub-back");
            if (hubBackBtn != null)
            {
                hubBackBtn.clicked += () =>
                {
                    Novgov.UI.UiSfx.Play(Novgov.UI.UiSfx.Sound.Tap);
                    Novgov.Network.GameServerClient.Instance?.Disconnect("user_left_lobby");
                    SetUiState(UiState.Hidden);
                    GameManagerUI.Instance?.ReturnToStartupMenu();
                };
            }

            Button signOutBtn = conquestRoot?.Q<Button>("btn-sign-out");
            if (signOutBtn != null)
            {
                signOutBtn.clicked += () =>
                {
                    Novgov.Network.GameServerClient.Instance?.Disconnect("user_signed_out");
                    SupabaseAuthClient.SignOut();
                    SetUiState(UiState.Hidden);
                    GameManagerUI.Instance?.ReturnToStartupMenu("Vous êtes déconnecté de votre compte.");
                };
            }

            // Cartes de l'onglet GESTION (+ RAPPORTS dans l'en-tête) : chacune ouvre son écran, dont
            // le bouton RETOUR revient à la Conquête sur le même onglet.
            foreach (var (buttonName, screenName) in new[] { ("btn-roster", "Roster"), ("btn-buildings", "Buildings"), ("btn-notifications", "Notifications"), ("btn-sieges", "Sieges") })
            {
                Button btn = conquestRoot?.Q<Button>(buttonName);
                if (btn == null) { Debug.LogWarning($"[MultiplayerMatchController] Bouton '{buttonName}' introuvable dans ConquestScreen.uxml."); continue; }
                btn.clicked += () =>
                {
                    Novgov.UI.UiSfx.Play(Novgov.UI.UiSfx.Sound.Tap);
                    OpenManagementScreen(screenName);
                };
            }

            Button leaderboardBtn = conquestRoot?.Q<Button>("btn-leaderboard");
            if (leaderboardBtn != null)
            {
                leaderboardBtn.clicked += () =>
                {
                    Novgov.UI.UiSfx.Play(Novgov.UI.UiSfx.Sound.Tap);
                    LeaderboardController.Show();
                };
            }

            var rosterRoot = UIScreenManager.Instance.GetScreen("Roster");
            rosterRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.Hub));

            var bldgRoot = UIScreenManager.Instance.GetScreen("Buildings");
            bldgRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.Hub));

            var notifRoot = UIScreenManager.Instance.GetScreen("Notifications");
            notifRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.Hub));

            var siegesRoot = UIScreenManager.Instance.GetScreen("Sieges");
            siegesRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.Hub));

            authRoot = UIScreenManager.Instance.GetScreen("Auth");
            authTitleLabel = authRoot.Q<Label>("title-label");
            emailFieldEl = authRoot.Q<TextField>("email-field");
            passwordFieldEl = authRoot.Q<TextField>("password-field");
            usernameContainer = authRoot.Q<VisualElement>("username-container");
            usernameFieldEl = authRoot.Q<TextField>("username-field");
            submitButton = authRoot.Q<Button>("submit-button");
            toggleModeButton = authRoot.Q<Button>("toggle-mode-button");
            authStatusLabel = authRoot.Q<Label>("status-label");

            emailFieldEl.RegisterValueChangedCallback(evt => emailField = evt.newValue);
            passwordFieldEl.RegisterValueChangedCallback(evt => passwordField = evt.newValue);
            usernameFieldEl.RegisterValueChangedCallback(evt => usernameField = evt.newValue);
            authRoot.Q<Toggle>("show-password-toggle").RegisterValueChangedCallback(evt => passwordFieldEl.isPasswordField = !evt.newValue);
            submitButton.clicked += () => { if (uiState == UiState.SignUp) HandleSignUp(); else HandleSignIn(); };
            toggleModeButton.clicked += () =>
            {
                statusMessage = "";
                SetUiState(uiState == UiState.SignUp ? UiState.Login : UiState.SignUp);
            };

            Button authBackButton = authRoot.Q<Button>("back-button");
            if (authBackButton != null)
            {
                authBackButton.clicked += () =>
                {
                    SetUiState(UiState.Hidden);
                    GameManagerUI.Instance?.ReturnToStartupMenu();
                };
            }
            else
            {
                Debug.LogError("[MultiplayerMatchController] Bouton 'back-button' introuvable dans AuthScreen.uxml — le retour depuis Connexion/Création de compte restera inopérant.");
            }

#if UNITY_EDITOR
            // Connexion rapide aux 2 comptes de test (créés le 2026-08-29 sur novgov.com, voir
            // 08-known-issues-and-todo.md §10) — évite de ressaisir email/mot de passe à chaque essai
            // en Éditeur. Rangée entière cachée par défaut dans le UXML (display:none) : rendue
            // visible UNIQUEMENT ici, jamais sur un vrai build Android/iOS.
            VisualElement testAccountsRow = authRoot.Q<VisualElement>("test-accounts-row");
            if (testAccountsRow != null)
            {
                testAccountsRow.style.display = DisplayStyle.Flex;
                BindTestAccountButton(authRoot, "btn-test-account-1", "testlille1@novgov.test", "TestLille1!");
                BindTestAccountButton(authRoot, "btn-test-account-2", "testlille2@novgov.test", "TestLille2!");
            }
#endif

            waitingRoot = UIScreenManager.Instance.GetScreen("Waiting");
            waitingStatusLabel = waitingRoot.Q<Label>("status-label");
            cancelWaitButton = waitingRoot.Q<Button>("btn-cancel-wait");
            if (cancelWaitButton != null) cancelWaitButton.clicked += OnCancelWaitClicked;
            else Debug.LogError("[MultiplayerMatchController] Bouton 'btn-cancel-wait' introuvable dans WaitingScreen.uxml — l'attente d'un adversaire ne pourra pas être annulée.");

            hudRoot = UIScreenManager.Instance.GetScreen("InMatchHud");
            teamBanner = hudRoot.Q<Label>("team-banner");
            phaseLabel = hudRoot.Q<Label>("phase-label");
            ghostBannerLabel = hudRoot.Q<Label>("ghost-banner");
            zoneBarContainer = hudRoot.Q<VisualElement>("zone-bar-container");
            zoneFillTeam1 = hudRoot.Q<VisualElement>("zone-fill-team1");
            zoneFillTeam2 = hudRoot.Q<VisualElement>("zone-fill-team2");

            matchOverRoot = UIScreenManager.Instance.GetScreen("MatchOver");
            resultLabel = matchOverRoot.Q<Label>("result-label");
            ratingLabel = matchOverRoot.Q<Label>("rating-label");
            // 2026-09-30 : "Retour au menu" rechargeait la scène vers le menu Solo/En ligne — le joueur
            // devait repasser par JOUER EN LIGNE, chargement et reconnexion après CHAQUE partie.
            // Rechargement toujours nécessaire (état de partie remis à zéro), mais suivi d'un retour
            // direct à la Conquête (voir GameManagerUI.ReloadSceneThen).
            matchOverRoot.Q<Button>("menu-button").clicked += ReloadSceneBackToHub;
            matchOverRoot.Q<Button>("leaderboard-button").clicked += () => LeaderboardController.Show();
        }

        private void SetUiState(UiState newState)
        {
            // Avis du QG (voir pendingHubNotice) : consommé en QUITTANT le hub, pas en y entrant —
            // RefreshHubScreen doit encore pouvoir l'afficher.
            if (uiState == UiState.Hub && newState != UiState.Hub) pendingHubNotice = null;
            if (newState != UiState.Matchmaking) quitConfirmArmed = false;

            uiState = newState;
            IsFlowActive = newState != UiState.Hidden;
            IsInMatch = newState == UiState.InMatch;
            switch (newState)
            {
                case UiState.Login:
                case UiState.SignUp:
                    RefreshAuthScreen();
                    UIScreenManager.Instance.Show("Auth");
                    break;
                case UiState.Hub:
                    RefreshHubScreen();
                    UIScreenManager.Instance.Show("Conquest");
                    // Onglet (carte/gestion) et contenu de la carte : voir Novgov.UI.ZoneMapController.
                    Novgov.UI.ZoneMapController.EnsureInstance().OnHubShown();
                    _ = GrantDailyActionPoints();
                    break;
                case UiState.Connecting:
                case UiState.Matchmaking:
                    // Reparti à zéro à CHAQUE entrée dans cet état, y compris une ré-entrée sur le
                    // MÊME état visuel (ex: SubmitLocalDeployment rappelle SetUiState(Matchmaking)
                    // alors qu'on y était peut-être déjà) — voir waitingScreenEnteredRealtime : sans
                    // ce reset, l'indicateur "cela peut prendre plusieurs minutes" pourrait apparaître
                    // immédiatement pour une attente qui vient de commencer, héritée du délai déjà
                    // écoulé lors d'une attente PRÉCÉDENTE (ex: recherche d'adversaire longue, suivie
                    // d'un déploiement rapide).
                    waitingScreenEnteredRealtime = Time.realtimeSinceStartup;
                    waitingStatusLabel.text = RenderWaitingScreenText();
                    RefreshCancelWaitButton();
                    UIScreenManager.Instance.Show("Waiting");
                    break;
                case UiState.Deployment:
                    // Rien à afficher ici : le dock de déploiement (UnitSpawnerUI, écran
                    // "DeploymentDock") gère seul son affichage pendant cette phase — voir
                    // UnitSpawnerUI.RefreshDeploymentDockUI(). On masque juste les écrans propres à
                    // ce contrôleur (ex: l'écran "Recherche d'adversaire...").
                    UIScreenManager.Instance.HideAll();
                    break;
                case UiState.InMatch:
                    RefreshHudStaticFields();
                    UIScreenManager.Instance.Show("InMatchHud");
                    break;
                case UiState.MatchOver:
                    UIScreenManager.Instance.Show("MatchOver");
                    break;
                case UiState.Hidden:
                    UIScreenManager.Instance.HideAll();
                    break;
            }
        }

#if UNITY_EDITOR
        /// <summary>Remplit email/mot de passe avec un compte de test et connecte directement — voir
        /// le garde #if UNITY_EDITOR ci-dessus, jamais compilé dans un build réel.</summary>
        private void BindTestAccountButton(VisualElement authRoot, string buttonName, string email, string password)
        {
            Button btn = authRoot.Q<Button>(buttonName);
            if (btn == null)
            {
                Debug.LogWarning($"[MultiplayerMatchController] Bouton '{buttonName}' introuvable dans AuthScreen.uxml.");
                return;
            }
            btn.clicked += () =>
            {
                emailField = email;
                passwordField = password;
                emailFieldEl.value = email;
                passwordFieldEl.value = password;
                HandleSignIn();
            };
        }
#endif

        private void RefreshAuthScreen()
        {
            bool isSignUp = uiState == UiState.SignUp;
            authTitleLabel.text = isSignUp ? "CRÉER UN COMPTE" : "CONNEXION MULTIJOUEUR";
            usernameContainer.style.display = isSignUp ? DisplayStyle.Flex : DisplayStyle.None;
            submitButton.text = isSignUp ? "Créer le compte" : "Se connecter";
            toggleModeButton.text = isSignUp ? "J'ai déjà un compte" : "Pas encore de compte ? Créer un compte";
            authStatusLabel.text = statusMessage;
        }

        private void RefreshHudStaticFields()
        {
            teamBanner.text = localTeamId == 2 ? "ÉQUIPE ROUGE" : "ÉQUIPE BLEUE";
            teamBanner.RemoveFromClassList("team1-badge");
            teamBanner.RemoveFromClassList("team2-badge");
            teamBanner.AddToClassList(localTeamId == 2 ? "team2-badge" : "team1-badge");
            zoneBarContainer.style.display = currentMode == "zone_control" ? DisplayStyle.Flex : DisplayStyle.None;
            ghostBannerLabel.style.display = DisplayStyle.None;
        }

        private void RefreshHudDynamicFields()
        {
            // Dégagement dynamique sous le radar (voir InMatchHudScreen.uxml) — un hardcode de
            // 180px s'y calibrait sur l'ancienne taille FIXE du radar (120px) ; depuis son
            float radarBottom = TacticalRadarUI.BottomEdgeVirtualY;
            hudRoot.style.paddingTop = radarBottom > 0f ? radarBottom + 12f : 40f;

            bool isExecuting = TacticalPathManager.Instance != null && TacticalPathManager.Instance.phaseActuelle == TacticalPathManager.GamePhase.Execution;


            phaseLabel.text = !string.IsNullOrEmpty(statusMessage)
                ? statusMessage
                : (isExecuting ? "EXÉCUTION — résolution du tour, patientez..." : "PLANIFICATION");

            if (currentMode == "zone_control")
            {
                zoneFillTeam1.style.width = new StyleLength(Length.Percent(zoneProgressTeam1));
                zoneFillTeam2.style.width = new StyleLength(Length.Percent(zoneProgressTeam2));
            }
        }
#endif

        /// <summary>
        /// Appelé par TacticalPathManager.LancerExecutionTour() quand IsActive est vrai — envoie les
        /// ordres du joueur local au serveur au lieu d'exécuter une simulation locale. Déclarée EN
        /// DEHORS du bloc #if !UNITY_SERVER ci-dessus (contrairement au reste de la classe) : son
        /// appelant n'est lui-même pas gardé, et son corps ne dépend d'aucun type UI Toolkit.
        /// </summary>
        public void SubmitLocalTurn()
        {
#if !UNITY_SERVER
            var myUnits = UnitAI.AllLivingUnits.Where(u => u.teamID == localTeamId).ToList();
            var orders = new List<UnitOrder>();

            foreach (var unit in myUnits)
            {
                if (unit.tacticalPath.Count == 0) continue;
                var pathNodes = unit.tacticalPath
                    .Select(n => new PathNode { x = n.position.x, y = n.position.y, z = n.position.z, action = (int)n.action })
                    .ToArray();
                orders.Add(new UnitOrder { unit_id = unit.gameObject.name, path = pathNodes });
            }

            GameServerClient.Instance.Send(new NetMessage
            {
                type = "submit_turn",
                turn_number = currentTurnNumber,
                orders = orders.ToArray()
            });

            statusMessage = "Ordres envoyés — en attente de l'adversaire...";
#endif
        }
    }
}
