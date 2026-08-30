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
        private string statusMessage = "";

        private int localTeamId = 0;
        private string currentMode = "deathmatch";
        private float zoneProgressTeam1 = 0f;
        private float zoneProgressTeam2 = 0f;
        private int currentTurnNumber = 1;
        private int lastServerSecondsRemaining = -1;
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
            statusMessage = "Connexion en cours...";
            SetUiState(UiState.Connecting);
            var (ok, error) = await SupabaseAuthClient.SignIn(emailField, passwordField);
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                SetUiState(UiState.Login);
                return;
            }
            SetUiState(UiState.ModeSelect);
        }

        private async void HandleSignUp()
        {
            statusMessage = "Création du compte...";
            SetUiState(UiState.Connecting);
            var (ok, error) = await SupabaseAuthClient.SignUp(emailField, passwordField, usernameField);
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                SetUiState(UiState.SignUp);
                return;
            }
            SetUiState(UiState.ModeSelect);
        }

        [System.Serializable] private class ServerInstanceEntry { public string id; public int public_port; }
        [System.Serializable] private class ServerInstanceList { public ServerInstanceEntry[] items; }

        // Une instance sans heartbeat depuis plus longtemps que ça est considérée morte/plantée et
        // ignorée (voir MatchSessionManager.InstanceHeartbeatSeconds = 2s côté serveur — largement
        // de quoi tolérer une latence réseau normale sans pour autant router vers une instance figée).
        private const int InstanceStaleSeconds = 10;

        /// <summary>
        /// Choisit une instance de serveur de jeu LIBRE avant de s'y connecter (voir schema.sql §7,
        /// table "server_instances") — remplace la connexion directe à un port fixe unique d'avant
        /// le pool multi-instances. Préfère une instance où quelqu'un attend déjà pour LE MÊME mode
        /// (pour converger vers elle plutôt que vers une instance vide au hasard, et ainsi former
        /// une paire), sinon prend la première instance libre disponible.
        /// </summary>
        /// <summary>Échec avant même la connexion TCP (matchmaking injoignable ou aucune instance
        /// libre) — route vers l'écran pertinent selon le mode : ZoneResult pour une conquête (voir
        /// ZoneMapController, abonné à OnZoneResult), ModeSelect pour Deathmatch/Zone de Contrôle.</summary>
        private void FailConnection(string message)
        {
            if (selectedMode == "conquest")
            {
                OnZoneResult?.Invoke(message);
            }
            else
            {
                statusMessage = message;
                SetUiState(UiState.ModeSelect);
            }
        }

        private IEnumerator ConnectToGameServerCoroutine()
        {
            statusMessage = "Recherche d'un serveur de jeu libre...";
            SetUiState(UiState.Matchmaking);

            string queueColumn = selectedMode == "zone_control" ? "waiting_zone_control" : "waiting_deathmatch";
            string cutoffIso = System.DateTime.UtcNow.AddSeconds(-InstanceStaleSeconds).ToString("o");
            string url = $"{SupabaseAuthClient.RestBaseUrl}/server_instances?status=neq.busy&updated_at=gt.{UnityWebRequest.EscapeURL(cutoffIso)}&order={queueColumn}.desc,updated_at.desc&limit=1&select=id,public_port";

            int chosenPort = -1;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
                req.SetRequestHeader("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.access_token);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    FailConnection("Impossible de contacter le service de matchmaking.");
                    yield break;
                }

                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    ServerInstanceEntry[] instances = JsonUtility.FromJson<ServerInstanceList>(wrapped).items;
                    if (instances != null && instances.Length > 0) chosenPort = instances[0].public_port;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[MultiplayerMatchController] Parsing server_instances échoué : {ex.Message}");
                }
            }

            if (chosenPort < 0)
            {
                FailConnection("Tous les serveurs sont occupés — réessaie dans un instant.");
                yield break;
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

        private void HandleServerDisconnected(string reason)
        {
            if (uiState == UiState.InMatch || uiState == UiState.Matchmaking || uiState == UiState.Deployment)
            {
                statusMessage = "Connexion au serveur perdue (" + reason + ").";
                SetUiState(UiState.Login);
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
                case "turn_result": StartCoroutine(PlaySnapshotsCoroutine(msg)); break;
                case "match_over": OnMatchOver(msg); break;
                case "zone_captured": OnZoneCaptured(msg); break;
                case "zone_attack_result": OnZoneAttackResult(msg); break;
            }
        }

        private void OnZoneCaptured(NetMessage msg)
        {
            OnZoneResult?.Invoke($"Zone ({msg.zone_tile_x},{msg.zone_tile_y}) capturée sans résistance !");
        }

        private void OnZoneAttackResult(NetMessage msg)
        {
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

        private void OnMatchFound(NetMessage msg)
        {
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

            if (currentMode == "conquest")
            {
                // La géométrie RÉELLE de la Zone attaquée (bâtiments + sol + NavMesh) doit être
                // chargée sur CE client avant d'ouvrir le déploiement — sans ça, le joueur placerait
                // ses unités sur l'ancienne carte encore affichée à l'écran.
                statusMessage = "Chargement de la Zone attaquée...";
                StartCoroutine(LoadMatchMapThenOpenDeployment(true, msg.zone_tile_x, msg.zone_tile_y));
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
            StartCoroutine(LoadMatchMapThenOpenDeployment(msg.has_home_tile, msg.zone_tile_x, msg.zone_tile_y));
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
        private IEnumerator LoadMatchMapThenOpenDeployment(bool hasRealTile, int tileX, int tileY)
        {
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();

            if (hasRealTile)
            {
                Novgov.Generation.ZoneManager.EnsureInstance().LoadZone(tileX, tileY);
            }
            else
            {
                MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
                if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
                if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            }

            float maxWait = hasRealTile ? 60f : 30f;
            while (cityGen != null && !cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            if (cityGen != null && !cityGen.IsCityReady)
            {
                Debug.LogError($"[MultiplayerMatchController] Carte non prête après {(hasRealTile ? 60f : 30f):F0}s (tuile réelle={hasRealTile}) — abandon, jamais d'ouverture du déploiement sur une carte non confirmée.");
                statusMessage = "Échec du chargement de la carte — nouvelle tentative nécessaire.";
                GameServerClient.Instance?.Disconnect("map_load_failed");
                yield break;
            }

            OpenDeploymentDock();
        }

        /// <summary>Placement manuel (voir 03-network-protocol.md, "submit_deployment"/
        /// "deployment_result") : chaque joueur choisit où poser sa PROPRE escouade, dans son propre
        /// dock, verrouillé sur son camp — voir UnitSpawnerUI.OpenDockForMultiplayerDeployment. Le
        /// serveur valide et diffuse le résultat final via "deployment_result" (OnDeploymentResult),
        /// qui est ce qui spawn réellement les unités sur CE client — y compris les siennes, au cas
        /// où le serveur ait dû recadrer une position hors de la zone légale.</summary>
        private void OpenDeploymentDock()
        {
            UnitSpawnerUI.Instance.ClearAllUnits();
            UnitSpawnerUI.Instance.maxUnitsPerTeam = 4;
            UnitSpawnerUI.Instance.OpenDockForMultiplayerDeployment(localTeamId);
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

            if (msg.deployed_units != null)
            {
                foreach (var u in msg.deployed_units)
                {
                    var type = (UnitSpawnerUI.UnitType)u.unit_type;
                    Vector3 pos = new Vector3(u.x, u.y, u.z);
                    UnitSpawnerUI.Instance.SpawnUnitAt(type, pos, u.team_id, forcedName: u.unit_id);
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

            SetUiState(UiState.InMatch);
        }

        private void OnOpponentGhosted(NetMessage msg)
        {
            // Depuis le rétablissement du substitut IA (voir UnitAI.isGhosted /
            // MatchSessionManager.ApplyForPlayer), un camp absent n'est plus totalement passif : ce
            // texte reflète maintenant ce qui se passe réellement, plutôt que de laisser croire à des
            // mannequins immobiles.
            ghostBannerText = msg.team_id == localTeamId
                ? "Vous étiez absent — une IA de secours a joué vos unités ce tour-ci."
                : "Adversaire absent — une IA de secours a joué ses unités ce tour-ci.";
            ghostBannerTimer = 4f;
            if (ghostBannerLabel != null)
            {
                ghostBannerLabel.text = ghostBannerText;
                ghostBannerLabel.style.display = DisplayStyle.Flex;
            }
        }

        private void OnMatchOver(NetMessage msg)
        {
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

        private IEnumerator PlaySnapshotsCoroutine(NetMessage msg)
        {
            currentTurnNumber = msg.turn_number + 1;

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

                foreach (UnitState state in snap.units)
                {
                    visibleThisTick.Add(state.unit_id);

                    if (!unitLookup.TryGetValue(state.unit_id, out UnitAI unit) || unit == null)
                    {
                        // Première apparition de cette unité côté client : elle vient d'être repérée
                        // et n'existe pas encore en scène (deployment_result ne contenait que ma
                        // propre équipe, voir MatchSessionManager.RunDeploymentPhase) — on la fait
                        // apparaître directement à sa position révélée.
                        var newType = (UnitSpawnerUI.UnitType)state.unit_type;
                        Vector3 spawnPos = new Vector3(state.x, state.y, state.z);
                        unit = UnitSpawnerUI.Instance.SpawnUnitAt(newType, spawnPos, state.team_id, forcedName: state.unit_id);
                        if (unit == null) continue;
                        unitLookup[state.unit_id] = unit;
                        unit.isPlayerControlled = (unit.teamID == localTeamId);
                        var newAgent = unit.GetComponent<NavMeshAgent>();
                        if (newAgent != null) newAgent.enabled = false;
                    }

                    unit.SetVisualsVisibility(true);

                    Vector3 newPos = new Vector3(state.x, state.y, state.z);
                    float moveSpeed = previousPositions.TryGetValue(state.unit_id, out Vector3 prevPos)
                        ? Vector3.Distance(prevPos, newPos) / intervalSec
                        : 0f;
                    previousPositions[state.unit_id] = newPos;

                    unit.transform.position = newPos;
                    unit.transform.rotation = Quaternion.Euler(0f, state.ry, 0f);
                    unit.SetNetworkHealth(state.health);
                    unit.SetNetworkAnimState(state.shooting, moveSpeed);
                    if (state.dead) unit.ApplyNetworkDeath();
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
                yield return new WaitForSeconds(intervalSec);
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
            modeSelectRoot.Q<Button>("btn-deathmatch").clicked += () => { selectedMode = "deathmatch"; StartCoroutine(ConnectToGameServerCoroutine()); };
            modeSelectRoot.Q<Button>("btn-zone-control").clicked += () => { selectedMode = "zone_control"; StartCoroutine(ConnectToGameServerCoroutine()); };

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
                    UIScreenManager.Instance.Show("ModeSelect");
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
            timerLabel.text = lastServerSecondsRemaining >= 0 ? $"⏱️ {lastServerSecondsRemaining}s" : "";
            timerLabel.style.color = lastServerSecondsRemaining <= 10 && lastServerSecondsRemaining >= 0
                ? new StyleColor(Color.red) : new StyleColor(new Color(0.886f, 0.910f, 0.925f));

            phaseLabel.text = !string.IsNullOrEmpty(statusMessage)
                ? statusMessage
                : (TacticalPathManager.Instance != null && TacticalPathManager.Instance.phaseActuelle == TacticalPathManager.GamePhase.Execution
                    ? "EXÉCUTION" : "PLANIFICATION");

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
