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
    public class MultiplayerMatchController : MonoBehaviour
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
        private enum UiState { Hidden, ModeSelect, Login, SignUp, Connecting, Matchmaking, Deployment, InMatch, MatchOver }
        private UiState uiState = UiState.Hidden;

        private string selectedMode = "deathmatch";

        // Zone de Conquête ciblée par AttackZone() — ignorés par le serveur pour deathmatch/zone_control
        // (voir NetMessage.zone_tile_x/zone_tile_y).
        private int attackTileX;
        private int attackTileY;

        /// <summary>Résultat d'une demande de conquête INSTANTANÉE (zone_captured/zone_attack_result,
        /// pas de combat) — voir Novgov.UI.ZoneMapController, seul abonné actuel, qui affiche le
        /// message sur ZoneResultScreen. Le cas "combat de conquête" (match_found -> déploiement ->
        /// match_over) passe par le flux UiState normal (OnMatchOver), pas par cet event.</summary>
        public static event System.Action<string> OnZoneResult;

        private string emailField = "";
        private string passwordField = "";
        private string usernameField = "";

        private int localTeamId = 0;
        public int LocalTeamId => localTeamId;
        public static Dictionary<string, int> LostUnits = new Dictionary<string, int>();

        // Composition RÉELLE déployée pour mon camp (unit_type -> quantité), capturée à
        // OnDeploymentResult (correctif 2026-09-06 — voir OnMatchOver) : ProcessLostUnitsAsync lit
        // LostUnits pour décrémenter la caserne, mais rien n'écrivait jamais dedans — aucune perte
        // au combat n'était donc jamais déduite, un joueur pouvait redéployer indéfiniment des
        // unités pourtant mortes. Comparé à l'effectif encore vivant à OnMatchOver pour en déduire
        // les pertes, sans dépendre d'un nouveau message serveur.
        private Dictionary<string, int> deployedRosterCountByType = new Dictionary<string, int>();

        private string currentMode = "deathmatch";
        private float zoneProgressTeam1 = 0f;
        private float zoneProgressTeam2 = 0f;
        private int currentTurnNumber = 1;
        private int lastServerSecondsRemaining = -1;
        public static int PhaseSecondsRemaining => Instance != null ? Instance.lastServerSecondsRemaining : 0;
        // 2026-09-06 : garde contre un second "turn_result" qui démarrerait une deuxième
        // PlaySnapshotsCoroutine en parallèle de la première (jamais vu en pratique, mais rien ne
        // l'empêchait) — la coroutine en cours resterait alors valide, une deuxième relirait par
        // dessus les MÊMES unités en même temps, une source de bugs visuels difficile à reproduire.
        private bool isPlayingSnapshots = false;
        private string statusMessage = "";
        private string ghostBannerText = "";
        private float ghostBannerTimer = 0f;

        // Références UI Toolkit mises en cache une fois dans BindUI().
        private VisualElement authRoot, waitingRoot, hudRoot, matchOverRoot;
        private Label authTitleLabel, authStatusLabel, waitingStatusLabel;
        private TextField emailFieldEl, passwordFieldEl, usernameFieldEl;
        private VisualElement usernameContainer;
        private Button submitButton, toggleModeButton;
        private Label teamBanner, phaseLabel, timerLabel, ghostBannerLabel, resultLabel, ratingLabel;
        private VisualElement zoneBarContainer, zoneFillTeam1, zoneFillTeam2;
        private bool uiBound = false;

        // 2026-09-06 : offre "jouer contre l'IA en attendant" (bouton + minuteur + StartPracticeVsAI)
        // retirée sur demande explicite — aucune mention d'IA ne doit apparaître dans les files
        // d'attente Deathmatch/Zone de Contrôle. Elle n'était de toute façon jamais éligible pour
        // Conquête/Entraînement (déjà exclus), donc plus aucun mode ne peut plus l'atteindre.

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
                if (waitingStatusLabel != null) waitingStatusLabel.text = statusMessage;
            }
            else if (uiState == UiState.InMatch)
            {
                RefreshHudDynamicFields();
            }
        }

        public async void BeginLoginFlow()
        {
            // 2026-09-06 : deux tentatives précédentes ici (déconnexion forcée du Joueur Virtuel,
            // puis un simple "ignorer la session enregistrée") réglaient la lecture mais pas le fond
            // du problème — PlayerPrefs vivait dans une case du Registre Windows PARTAGÉE entre
            // l'Éditeur principal et ses clones Multiplayer Play Mode, donc SE CONNECTER depuis un
            // Joueur Virtuel écrasait quand même cette case, et l'Éditeur principal en héritait au
            // lancement suivant. Corrigé à la racine dans SupabaseAuthClient (voir
            // Novgov.Core.EditorPlayerPrefsScope) : chaque identité a maintenant sa propre case, donc
            // ce code redevient l'implémentation normale, sans cas particulier Éditeur ici.
            if (SupabaseAuthClient.HasSavedSession())
            {
                statusMessage = "Reconnexion...";
                SetUiState(UiState.Connecting);
                var (ok, _) = await SupabaseAuthClient.TryRestoreSession();
                if (ok)
                {
                    SetUiState(UiState.ModeSelect);
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
            SetUiState(UiState.ModeSelect);
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
            SetUiState(UiState.ModeSelect);
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
            if (selectedMode != "conquest")
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
                zone_tile_x = selectedMode == "conquest" ? attackTileX : homeTileX,
                zone_tile_y = selectedMode == "conquest" ? attackTileY : homeTileY,
                has_home_tile = hasHomeTile
            });

            statusMessage = selectedMode == "conquest" ? $"Attaque de la Zone ({attackTileX},{attackTileY})..." : "Recherche d'adversaire...";
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

        /// <summary>2026-09-06 : jusqu'ici "deathmatch"/"zone_control" n'étaient JAMAIS déclenchés
        /// depuis l'UI — seuls AttackZone (Conquête, un joueur contre une garnison IA, jamais un
        /// adversaire vivant) et StartPracticeVsAI (accessible uniquement DEPUIS une file d'attente
        /// déjà ouverte) appelaient ConnectToGameServerCoroutine. Le vrai appariement à deux joueurs
        /// vivants (DetermineMatchCacheKey côté serveur) existait donc dans le protocole sans aucun
        /// bouton pour l'atteindre. Ajouté ici, appelé par btn-deathmatch/btn-zone-control
        /// (ModeSelectScreen.uxml, voir BindUI).</summary>
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
                statusMessage = DescribeDisconnectReason(reason);
                if (Novgov.Auth.SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(Novgov.Auth.SupabaseAuthClient.CurrentSession.access_token))
                {
                    SetUiState(UiState.ModeSelect);
                }
                else
                {
                    SetUiState(UiState.Login);
                }
                IsActive = false;
                IsDeploymentPhaseActive = false;
            }
        }

        // =====================================================================
        // Messages serveur
        // =====================================================================

        private void HandleServerMessage(NetMessage msg)
        {
            switch (msg.type)
            {
                case "match_found": OnMatchFound(msg); break;
                case "deployment_result": OnDeploymentResult(msg); break;
                case "turn_timer": lastServerSecondsRemaining = msg.seconds_remaining; break;
                case "opponent_ghosted": OnOpponentGhosted(msg); break;
                case "turn_result": if (!isPlayingSnapshots) StartCoroutine(PlaySnapshotsCoroutine(msg)); break;
                case "match_over": StartCoroutine(DeferredMatchOver(msg)); break;
                case "city_verify_result": OnCityVerifyResult(msg); break;
                case "zone_captured": OnZoneCaptured(msg); break;
                case "zone_attack_result": OnZoneAttackResult(msg); break;
            }
        }

        /// <summary>Vrai quand le serveur vient d'envoyer un résultat de Conquête INSTANTANÉE
        /// (zone_captured / zone_attack_result) : il referme systématiquement la socket juste après
        /// (voir MatchSessionManager_Conquest, `attacker.Close()` sur chacun de ces chemins). Cette
        /// fermeture est donc NORMALE et attendue — sans ce drapeau, HandleServerDisconnected la
        /// traitait comme une panne réseau, affichait "Connexion au serveur perdue" et renvoyait au
        /// menu une frame après l'ouverture du panneau de résultat, que le joueur n'avait donc jamais
        /// le temps de lire (2026-09-07).</summary>
        private bool expectingCloseAfterZoneResult = false;

        private void OnZoneCaptured(NetMessage msg)
        {
            expectingCloseAfterZoneResult = true;
            OnZoneResult?.Invoke($"Zone ({msg.zone_tile_x},{msg.zone_tile_y}) capturée sans résistance ! (+{msg.rating_delta} classement)");
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
                "already_owned" => "Cette Zone vous appartient déjà.",
                "server_busy" => "Serveur occupé — réessayez dans un instant.",
                // "not_adjacent" (voir MatchSessionManager.RunConquestRequest) : le serveur vérifie
                // désormais lui-même la contiguïté du territoire, une Zone lointaine n'est plus
                // attaquable même via un client modifié.
                "not_adjacent" => "Cette Zone n'est pas adjacente à votre territoire.",
                // "zone_taken" : Zone neutre capturée par quelqu'un d'autre entre votre demande et
                // la réponse du serveur (deux joueurs visant la même Zone neutre au même instant).
                "zone_taken" => "Trop tard — quelqu'un d'autre vient de capturer cette Zone.",
                _ => "Attaque de la Zone impossible pour le moment.",
            };
            OnZoneResult?.Invoke(message);
        }

        private int? preMatchExplorationTileX = null;
        private int? preMatchExplorationTileY = null;

        private void OnMatchFound(NetMessage msg)
        {
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

            if (currentMode == "conquest")
            {
                // La géométrie RÉELLE de la Zone attaquée (bâtiments + sol + NavMesh) doit être
                // chargée sur CE client avant d'ouvrir le déploiement — sans ça, le joueur placerait
                // ses unités sur l'ancienne carte encore affichée à l'écran.
                statusMessage = "Chargement de la Zone attaquée...";
                StartCoroutine(LoadMatchMapThenOpenDeployment(true, msg.zone_tile_x, msg.zone_tile_y, msg.city_data_json));
                return;
            }

            // Deathmatch/Zone de Contrôle (2026-08-30, "des milliers de cartes") : le serveur peut
            // désormais assigner la vraie tuile GPS d'un des deux joueurs (msg.has_home_tile) au lieu
            // de toujours la carte par défaut fixe — voir MatchSessionManager.TryStartMatch/
            // DetermineMatchCacheKey. AVANT ce correctif, le client gardait affichée sa propre ville
            // réelle (chargée avant même la connexion, voir GameManagerUI.StartDeviceGPS) pendant que
            // le serveur simulait sur la carte par défaut, ce qui produisait exactement les bugs
            // remontés en test (déploiement sur des polygones qui n'existent pas sur la carte du
            // serveur, positions "décalées") — il faut donc TOUJOURS charger explicitement la carte
            // que le serveur a réellement choisie, jamais faire confiance à ce qui est déjà affiché.
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
        private async System.Threading.Tasks.Task LoadZoneWithHqAsync(int tileX, int tileY, string json)
        {
            string zoneId = $"{tileX},{tileY}";
            var (ok, building) = await Novgov.Auth.SupabaseDatabaseClient.GetBuilding(zoneId);
            if (ok && building != null)
            {
                var cityGen = FindAnyObjectByType<CityGenerator>();
                if (cityGen != null) cityGen.HQBuildingIndex = building.building_index;
            }
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

            // 2026-09-06 : 60s/30s -> 300s (5 min), même demande/même raison que
            // MatchSessionManager.DeploymentSeconds/MapReadyMaxWaitSeconds côté serveur — sans ce
            // relèvement en parallèle, ce plafond CLIENT abandonnait bien avant que le délai généreux
            // du serveur n'ait la moindre chance de servir.
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

        // ÉQUITÉ GÉOMÉTRIQUE, 2ème étage (2026-09-12) — voir NetMessage.city_verify/
        // city_verify_result et MatchState.AuthoritativeCityHash (serveur) pour le contexte complet.
        // Vrai UNIQUEMENT entre l'envoi de "city_verify" et la réception de "city_verify_result" (ou
        // l'expiration du filet de sécurité ci-dessous) — jamais laissé à true plus longtemps, sinon
        // une résolution tardive/inattendue d'un ancien city_verify_result déclencherait une
        // resynchronisation hors de propos.
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
            // Récupération PROACTIVE de la caserne dès l'ouverture du déploiement (correctif
            // 2026-09-06) — sans ça, UnitSpawnerUI.CurrentRoster restait null jusqu'au tout premier
            // appel de GetRoster() (jamais garanti d'avoir eu lieu avant que le joueur ne tape sur un
            // bouton de déploiement), et le contrôle de caserne y traitait un roster absent comme
            // "0 unité possédée" pour tout : voir le commentaire dans UnitSpawnerUI.StartPlacingUnit.
            // Lancée ici en tâche de fond, largement avant que le joueur n'ait fini de charger sa
            // carte et ne puisse taper sur un bouton d'unité.
            _ = Novgov.Auth.SupabaseDatabaseClient.GetRoster();

            UnitSpawnerUI.Instance.ClearAllUnits();
            UnitSpawnerUI.Instance.maxUnitsPerTeam = 4;
            UnitSpawnerUI.Instance.OpenDockForMultiplayerDeployment(localTeamId);

            // Centrage de la caméra sur la zone de déploiement du joueur local
            if (TacticalCamera.Instance != null)
            {
                Vector3 center = localTeamId == 1 ? new Vector3(-25f, 0f, -25f) : new Vector3(25f, 0f, 25f);
                TacticalCamera.Instance.focusPosition = center;
                if (localTeamId == 2)
                {
                    // L'équipe 2 déploie depuis le coin Nord-Est, on tourne la caméra à 180° pour faire face au champ de bataille
                    TacticalCamera.Instance.currentYaw = 225f;
                }
                else
                {
                    TacticalCamera.Instance.currentYaw = 45f;
                }
            }
            IsDeploymentPhaseActive = true;

            MusicManager.SetGameplayVolume();
            SetUiState(UiState.Deployment);

            // Signale au serveur que CE client a fini de charger sa carte et voit maintenant
            // réellement son dock de déploiement — voir MatchSessionManager.RunDeploymentPhase, qui
            // attend ce signal des DEUX joueurs avant de démarrer le vrai compte à rebours de 45s
            // (sinon le chargement de ville pouvait à lui seul consommer tout le timer, voir rapport
            // de bug "des unités bleues et rouges apparaissent d'un coup sans jamais avoir pu placer
            // les miennes").
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
            // "DÉPLOIEMENT (...)" collé sur "PLANIFICATION", bug remonté en jeu).
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

            // "roster_trimmed" (2026-09-08) : au moins UN des placements que J'AI moi-même soumis
            // dépassait le budget serveur (nombre d'unités ou points, voir MatchSessionManager.
            // FilterRosterToBudget) et a été écarté INDIVIDUELLEMENT — le reste de mon déploiement
            // est bien celui que j'ai choisi, aux positions que j'ai choisies (plus de remplacement
            // en bloc par une escouade fixe sans rapport, voir §19 de 08-known-issues-and-todo.md).
            // Le dock ne connaît pas encore ce budget en points (seulement un nombre d'unités, voir
            // OpenDeploymentDock) : ce message est le seul moyen pour l'instant de savoir qu'une
            // partie du déploiement demandé n'a pas pu tenir.
            if (msg.reason == "roster_trimmed")
            {
                statusMessage = "Une partie de votre déploiement dépassait le budget autorisé (unités trop lourdes) — le reste a été posé tel quel.";
            }

            SetUiState(UiState.InMatch);
        }

        private void OnOpponentGhosted(NetMessage msg)
        {
            // CORRIGÉ 2026-09-08 — ce texte affirmait "une IA de secours a joué vos/ses unités" dans
            // TOUS les cas, alors que c'est FAUX pour Deathmatch/Zone de Contrôle. Il ne reflétait
            // que le chemin "vivant" (`ApplyForPlayer`, Conquête/Entraînement), qui appelle
            // réellement `TacticalAIPlanner.PlanifierTourIA()` pour un joueur ghosté. Le chemin PUR
            // (`ApplyForPlayerPure`, Deathmatch/Zone de Contrôle — voir son propre commentaire :
            // "plutôt que TacticalAIPlanner... ses unités TIENNENT LA POSITION") ne lance JAMAIS
            // aucune IA — un camp ghosté y reste simplement immobile (mais riposte s'il est attaqué,
            // comme toute unité). Un vrai joueur PvP voyait donc, à chaque tour manqué (le sien ou
            // celui de l'adversaire), une bannière lui affirmant noir sur blanc qu'une IA venait de
            // jouer à sa place — signalé par un joueur (2026-09-08) : "il y a toujours de l'IA dans
            // le multijoueur alors qu'on a dit pas d'IA". `currentMode` distingue les deux moteurs
            // sans nouveau champ réseau : "deathmatch"/"zone_control" -> chemin pur, jamais d'IA ;
            // "conquest"/"practice_ai" -> chemin vivant, IA réelle.
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

        /// <summary>Alimente LostUnits (correctif 2026-09-06) en comparant, PAR TYPE, la composition
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

        private async System.Threading.Tasks.Task ProcessLostUnitsAsync()
        {
            if (LostUnits.Count == 0) return;
            var (ok, roster) = await Novgov.Auth.SupabaseDatabaseClient.GetRoster();
            if (ok && roster != null)
            {
                foreach (var loss in LostUnits)
                {
                    var item = System.Linq.Enumerable.FirstOrDefault(roster, r => r.unit_type.Equals(loss.Key, System.StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        int newQty = System.Math.Max(0, item.quantity - loss.Value);
                        await Novgov.Auth.SupabaseDatabaseClient.UpsertRosterItem(loss.Key, newQty);
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task ClaimBuildingAsync(string zoneId)
        {
            int rndIndex = UnityEngine.Random.Range(1, 10);
            await Novgov.Auth.SupabaseDatabaseClient.ClaimBuilding(zoneId, rndIndex);
        }

        private IEnumerator DeferredMatchOver(NetMessage msg)
        {
            // Attendre la fin du rejeu (tour final) s'il y en a un en cours, pour ne pas couper
            // brutalement l'animation de mort de la dernière unité et cacher le champ de bataille
            // derrière l'écran de fin.
            while (isPlayingSnapshots)
            {
                yield return null;
            }

            // 2026-09-12 (retour joueur : "un menu sort alors qu'il ne devrait pas y être" à la fin
            // d'une partie) : la toute fin de PlaySnapshotsBody remet phaseActuelle à Planification
            // (ré-active la sélection/le menu contextuel) AVANT que cette coroutine ne reprenne la
            // main ici — au moins une frame durant laquelle le joueur peut encore sélectionner une
            // unité ou ouvrir un menu d'ordre sur une partie déjà terminée côté serveur, laissant un
            // menu contextuel ouvert par-dessus/derrière l'écran de fin qui s'affiche juste après.
            // Fermé explicitement avant d'afficher cet écran, quoi qu'il ait pu se passer pendant
            // cette fenêtre.
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

            ComputeLostUnitsFromDeployedVsAlive();
            _ = ProcessLostUnitsAsync();
            if (isVictory && Novgov.Generation.ZoneManager.Instance != null)
            {
                string zoneId = $"{Novgov.Generation.ZoneManager.Instance.CurrentTileX},{Novgov.Generation.ZoneManager.Instance.CurrentTileY}";
                _ = ClaimBuildingAsync(zoneId);
            }

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
                    resultText = $"VICTOIRE ! Zone ({msg.zone_tile_x},{msg.zone_tile_y}) conquise.";
                else if (msg.winner_team == localTeamId && msg.reason == "zone_lost_race")
                    resultText = $"Garnison vaincue, mais un autre joueur a capturé la Zone ({msg.zone_tile_x},{msg.zone_tile_y}) juste avant vous.";
                else if (msg.winner_team == localTeamId)
                    resultText = "Garnison vaincue.";
                else
                    resultText = $"DÉFAITE — la Zone ({msg.zone_tile_x},{msg.zone_tile_y}) reste aux mains de son propriétaire.";
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

        /// <summary>Joue le tour reçu du serveur, en garantissant que le verrou isPlayingSnapshots
        /// est TOUJOURS relâché — voir le finally.</summary>
        private IEnumerator PlaySnapshotsCoroutine(NetMessage msg)
        {
            isPlayingSnapshots = true;
            currentTurnNumber = msg.turn_number + 1;

            // REJEU SOUS GARDE (2026-09-07). Deux états doivent être rétablis quoi qu'il arrive,
            // sinon le client est définitivement bloqué :
            //   - isPlayingSnapshots : le verrou qui fait ignorer tout turn_result reçu pendant un
            //     rejeu (voir HandleServerMessage). Bloqué à true, le client ignore DÉFINITIVEMENT
            //     tous les tours suivants pendant que le serveur le fantômise à chaque tour ;
            //   - phaseActuelle : TacticalPathManager.Update sort immédiatement tant qu'elle vaut
            //     Execution, donc le joueur ne peut plus ni sélectionner une unité, ni poser un
            //     point, ni atteindre FIN DE TOUR.
            // Les deux sont rétablis en fin de PlaySnapshotsBody, donc sautés dès que celui-ci lève
            // (un SpawnUnitAt qui renvoie null, une unité détruite en cours de rejeu, ou deux unités
            // de même nom faisant lever ToDictionary).
            //
            // Un simple `try { yield return PlaySnapshotsBody(msg); } finally { ... }` NE SUFFIT PAS :
            // Unity déroule lui-même l'itérateur imbriqué, donc une exception levée dans MoveNext()
            // du corps ne repasse jamais par la machine à états de CETTE méthode — le finally n'est
            // émis que dans son Dispose(), que Unity n'appelle pas sur une coroutine avortée. On
            // pompe donc l'itérateur à la main, exactement comme MatchSessionManager.RunMatchGuarded
            // le fait côté serveur et pour la même raison (yield interdit dans un try/catch).
            IEnumerator inner = PlaySnapshotsBody(msg);
            while (true)
            {
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[MultiplayerMatchController] Exception pendant le rejeu du tour — récupération pour ne pas figer la partie : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    RecoverFromFailedReplay();
                    yield break;
                }
                if (!moved) break;
                yield return inner.Current;
            }

            isPlayingSnapshots = false;
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
            var unitLookup = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude)
                .ToDictionary(u => u.gameObject.name, u => u);
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

                // Glissement fluide (2026-09-11) : la position/rotation de ce tick ne sont plus posées
                // instantanément puis figées jusqu'au prochain (250 ms plus tard, voir TickDurationMs)
                // — ça se voyait comme une saccade, un "téléport" d'1 m toutes les 250 ms au lieu d'un
                // mouvement continu. On mémorise ici le départ/arrivée de chaque unité pour ce tick et
                // on interpole frame par frame pendant l'attente, après la boucle ci-dessous.
                var lerpFromPos = new Dictionary<UnitAI, Vector3>();
                var lerpFromRot = new Dictionary<UnitAI, Quaternion>();
                var lerpToPos = new Dictionary<UnitAI, Vector3>();
                var lerpToRot = new Dictionary<UnitAI, Quaternion>();

                // Retour visuel de combat (2026-09-11) : tirs à rejouer une fois toutes les unités de
                // ce tick connues (résolution de shoot_target_id différée après la boucle ci-dessous,
                // qui peut encore faire apparaître la cible si c'est sa première apparition côté
                // client) — mais AVANT la boucle d'interpolation, tant que les transforms sont encore
                // à leur position PRÉCÉDENTE (celle du tick d'avant), l'instant exact où le tir part.
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
                        // skipSafeSpawnAdjustment: true — même correctif que OnDeploymentResult ci-dessus :
                        // cette position vient d'un tick déjà résolu par le serveur (voir TacticalResolver),
                        // pas d'un placement frais. Sans ce garde, une unité ennemie qui vient d'être
                        // repérée pouvait apparaître visuellement à un endroit différent de sa VRAIE
                        // position logique (celle que le serveur et les autres clients utilisent).
                        unit = UnitSpawnerUI.Instance.SpawnUnitAt(newType, spawnPos, state.team_id, forcedName: state.unit_id, skipSafeSpawnAdjustment: true);
                        if (unit == null) continue;
                        unitLookup[state.unit_id] = unit;
                        unit.isPlayerControlled = (unit.teamID == localTeamId);
                        var newAgent = unit.GetComponent<NavMeshAgent>();
                        if (newAgent != null) newAgent.enabled = false;
                        justSpawned = true;
                    }

                    unit.SetVisualsVisibility(true);

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

                // Destruction de bâtiment (2026-09-12) : jusqu'ici WallDestroyed n'avait AUCUN
                // consommateur réseau (voir §19.9.3 de 08-known-issues-and-todo.md) — le bâtiment
                // restait visuellement intact chez les deux joueurs alors que le serveur le savait
                // détruit. buildingId est un INDEX dans BuildingStructure.AllBuildings (voir
                // TacticalGridBuilder) — ApplyNetworkDestruction (jamais TakeDamage/DestroyEnvironment)
                // ne retire JAMAIS ce bâtiment de cette liste, pour que cet index reste valide pour
                // tout le reste de la partie (voir son commentaire dans DestructibleEnvironment.cs).
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

        private async System.Threading.Tasks.Task GrantDailyActionPoints()
        {
            if (Novgov.Auth.SupabaseAuthClient.CurrentSession == null || Novgov.Auth.SupabaseAuthClient.CurrentSession.user == null) return;
            string userId = Novgov.Auth.SupabaseAuthClient.CurrentSession.user.id;
            string lastClaimStr = UnityEngine.PlayerPrefs.GetString($"LastDailyAPClaim_{userId}", "");
            string todayStr = System.DateTime.UtcNow.ToString("yyyyMMdd");
            if (lastClaimStr != todayStr)
            {
                var (okBuildings, bList) = await Novgov.Auth.SupabaseDatabaseClient.GetBuildings();
                int bonus = 50; 
                if (okBuildings && bList != null) bonus += bList.Length * 10;

                Novgov.Auth.SupabaseDatabaseClient.AddActionPoints(userId, bonus);
                UnityEngine.PlayerPrefs.SetString($"LastDailyAPClaim_{userId}", todayStr);
                UnityEngine.PlayerPrefs.Save();
            }
            RefreshModeSelectScreen();
        }

        private async void RefreshModeSelectScreen()
        {
            var root = UIScreenManager.Instance.GetScreen("ModeSelect");
            if (root == null) return;
            var lblUser = root.Q<Label>("lbl-username");
            var lblAP = root.Q<Label>("lbl-action-points");

            var (okProf, prof) = await Novgov.Auth.SupabaseDatabaseClient.GetProfile();
            if (okProf && prof != null)
            {
                if (lblUser != null) lblUser.text = $"Commandant {prof.username}";
                if (lblAP != null) lblAP.text = $"Points d'Action : {prof.action_points}";
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
            lblLoading.style.color = Color.white;
            scroll.Add(lblLoading);

            var (ok, list) = await Novgov.Auth.SupabaseDatabaseClient.GetBuildings();
            scroll.Clear();
            if (!ok || list == null || list.Length == 0)
            {
                var lbl = new Label("Vous ne possédez aucun territoire (bâtiment). Partez à la conquête de Zones pour en gagner !");
                lbl.style.color = Color.white;
                lbl.style.whiteSpace = WhiteSpace.Normal;
                scroll.Add(lbl);
                return;
            }

            foreach(var b in list)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.paddingTop = 8;
                row.style.paddingBottom = 8;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new Color(1,1,1,0.2f);
                
                var lblInfo = new Label($"Zone: {b.zone_id} | Index: {b.building_index}");
                lblInfo.style.color = Color.white;
                lblInfo.style.fontSize = 16;
                row.Add(lblInfo);

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
            if (lblPoints != null) lblPoints.text = $"Solde : {currentAp} AP";

            var (ok, roster) = await Novgov.Auth.SupabaseDatabaseClient.GetRoster();

            // Source unique (correctif 2026-09-06) : ce tableau était dupliqué ici avec des noms
            // ("Canon", "Char") qui ne correspondent à aucune valeur réelle de UnitType — voir le
            // commentaire de SupabaseDatabaseClient.KnownUnitTypes pour le détail du bug que ça
            // causait (Canon/Char indéfiniment indéployables après achat).
            string[] unitTypes = Novgov.Auth.SupabaseDatabaseClient.KnownUnitTypes;
            int[] unitCosts = Novgov.Auth.SupabaseDatabaseClient.KnownUnitCosts;

            for (int i = 0; i < unitTypes.Length; i++)
            {
                string uType = unitTypes[i];
                int cost = unitCosts[i];
                var item = roster != null ? System.Linq.Enumerable.FirstOrDefault(roster, r => r.unit_type.Equals(uType, System.StringComparison.OrdinalIgnoreCase)) : null;
                int qty = item != null ? item.quantity : 0;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.paddingTop = 8;
                row.style.paddingBottom = 8;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new Color(1,1,1,0.2f);
                
                var lblName = new Label($"{uType} (Possédé: {qty})");
                lblName.style.color = Color.white;
                lblName.style.fontSize = 16;
                row.Add(lblName);

                var btnBuy = new Button();
                btnBuy.text = $"Recruter ({cost} AP)";
                btnBuy.style.backgroundColor = currentAp >= cost ? new Color(0.2f, 0.6f, 0.2f) : new Color(0.5f, 0.5f, 0.5f);
                
                if (currentAp >= cost)
                {
                    btnBuy.clicked += async () =>
                    {
                        btnBuy.SetEnabled(false);
                        int newAp = currentAp - cost;
                        await Novgov.Auth.SupabaseDatabaseClient.UpdateProfile(prof.username, newAp);
                        await Novgov.Auth.SupabaseDatabaseClient.UpsertRosterItem(uType, qty + 1);
                        RefreshRosterScreen();
                    };
                }
                else
                {
                    btnBuy.SetEnabled(false);
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

            VisualElement modeSelectRoot = UIScreenManager.Instance.GetScreen("ModeSelect");
            Button conquestBtn = modeSelectRoot?.Q<Button>("btn-conquest");
            if (conquestBtn != null)
            {
                conquestBtn.clicked += () => Novgov.UI.ZoneMapController.EnsureInstance().Show();
            }
            
            Button zoneMapBtn = modeSelectRoot?.Q<Button>("btn-zone-map");
            if (zoneMapBtn != null) zoneMapBtn.clicked += () => Novgov.UI.ZoneMapController.EnsureInstance().Show();

            // Seul vrai mode où deux comptes différents s'affrontent en direct, synchronisés par le
            // même serveur (voir StartDeathmatch/StartZoneControl) — la Conquête ci-dessus est
            // toujours un joueur seul contre une garnison IA.
            Button deathmatchBtn = modeSelectRoot?.Q<Button>("btn-deathmatch");
            if (deathmatchBtn != null) deathmatchBtn.clicked += StartDeathmatch;

            Button zoneControlBtn = modeSelectRoot?.Q<Button>("btn-zone-control");
            if (zoneControlBtn != null) zoneControlBtn.clicked += StartZoneControl;

            Button rosterBtn = modeSelectRoot?.Q<Button>("btn-roster");
            if (rosterBtn != null) rosterBtn.clicked += () => {
                UIScreenManager.Instance.Show("Roster");
                RefreshRosterScreen();
            };

            Button buildingsBtn = modeSelectRoot?.Q<Button>("btn-buildings");
            if (buildingsBtn != null) buildingsBtn.clicked += () => {
                UIScreenManager.Instance.Show("Buildings");
                RefreshBuildingsScreen();
            };

            Button backToStartupBtn = modeSelectRoot?.Q<Button>("btn-back-startup");
            if (backToStartupBtn != null)
            {
                backToStartupBtn.clicked += () =>
                {
                    Novgov.Network.GameServerClient.Instance?.Disconnect("user_left_lobby");
                    SetUiState(UiState.Hidden);
                    GameManagerUI.Instance?.ReturnToStartupMenu();
                };
            }
            var rosterRoot = UIScreenManager.Instance.GetScreen("Roster");
            rosterRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.ModeSelect));

            var bldgRoot = UIScreenManager.Instance.GetScreen("Buildings");
            bldgRoot?.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(evt => SetUiState(UiState.ModeSelect));

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

            hudRoot = UIScreenManager.Instance.GetScreen("InMatchHud");
            teamBanner = hudRoot.Q<Label>("team-banner");
            phaseLabel = hudRoot.Q<Label>("phase-label");
            timerLabel = hudRoot.Q<Label>("timer-label");
            ghostBannerLabel = hudRoot.Q<Label>("ghost-banner");
            zoneBarContainer = hudRoot.Q<VisualElement>("zone-bar-container");
            zoneFillTeam1 = hudRoot.Q<VisualElement>("zone-fill-team1");
            zoneFillTeam2 = hudRoot.Q<VisualElement>("zone-fill-team2");

            matchOverRoot = UIScreenManager.Instance.GetScreen("MatchOver");
            resultLabel = matchOverRoot.Q<Label>("result-label");
            ratingLabel = matchOverRoot.Q<Label>("rating-label");
            matchOverRoot.Q<Button>("menu-button").clicked += () =>
            {
                SetUiState(UiState.Hidden);
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            };
            matchOverRoot.Q<Button>("leaderboard-button").clicked += () => LeaderboardController.Show();
        }

        private void SetUiState(UiState newState)
        {
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
                case UiState.ModeSelect:
                    RefreshModeSelectScreen();
                    UIScreenManager.Instance.Show("ModeSelect");
                    _ = GrantDailyActionPoints();
                    break;
                case UiState.Connecting:
                case UiState.Matchmaking:
                    waitingStatusLabel.text = statusMessage;
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
            // agrandissement dynamique (TacticalRadarUI, jusqu'à 230px, 2026-09-02), ce hardcode
            // aurait laissé la bannière équipe/timer chevaucher le radar. Suit sa vraie hauteur
            // réelle à chaque frame (espace UI Toolkit, voir BottomEdgeVirtualY) avec une petite
            // marge ; repli sur une valeur raisonnable si le radar est masqué (vue 3D Action) pour
            // ne pas coller la bannière tout en haut de l'écran.
            float radarBottom = TacticalRadarUI.BottomEdgeVirtualY;
            hudRoot.style.paddingTop = radarBottom > 0f ? radarBottom + 12f : 40f;

            bool isExecuting = TacticalPathManager.Instance != null && TacticalPathManager.Instance.phaseActuelle == TacticalPathManager.GamePhase.Execution;

            // 2026-09-06 : compte à rebours retiré de l'affichage sur demande explicite ("enlève le
            // temps dans tous les états, ne stresse pas le joueur") — la limite de temps serveur
            // existe toujours en coulisses (PlanningSeconds/DeploymentSeconds, généreuses, 5 min pour
            // le déploiement) comme filet de sécurité contre un adversaire réellement absent, mais ne
            // s'affiche plus nulle part. lastServerSecondsRemaining reste alimenté par "turn_timer"
            // (PhaseSecondsRemaining en dépend encore ailleurs) mais n'est plus lu ici.
            timerLabel.text = "";

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
