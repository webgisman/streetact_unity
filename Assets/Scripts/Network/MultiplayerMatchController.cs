using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Auth;
using UnityEngine;
using UnityEngine.AI;
#if !UNITY_SERVER
using UnityEngine.UIElements;
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

        private void ConnectToGameServer()
        {
            statusMessage = "Connexion au serveur de jeu...";

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
            client.Send(new NetMessage { type = "join_matchmaking", mode = selectedMode });

            statusMessage = "Recherche d'adversaire...";
            SetUiState(UiState.Matchmaking);
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
            }
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

            // Placement manuel (voir 03-network-protocol.md, "submit_deployment"/"deployment_result") :
            // chaque joueur choisit où poser sa PROPRE escouade, dans son propre dock, verrouillé sur
            // son camp — voir UnitSpawnerUI.OpenDockForMultiplayerDeployment. Le serveur valide et
            // diffuse le résultat final (les DEUX camps) via "deployment_result" (OnDeploymentResult
            // ci-dessous), qui est ce qui spawn réellement les unités sur CE client — y compris les
            // siennes, au cas où le serveur ait dû recadrer une position hors de la zone légale.
            UnitSpawnerUI.Instance.ClearAllUnits();
            UnitSpawnerUI.Instance.maxUnitsPerTeam = 4;
            UnitSpawnerUI.Instance.OpenDockForMultiplayerDeployment(localTeamId);
            IsDeploymentPhaseActive = true;

            MusicManager.SetGameplayVolume();
            SetUiState(UiState.Deployment);
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
            UnitSpawnerUI.Instance.ClearAllUnits();

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
            SetUiState(UiState.InMatch);
        }

        private void OnOpponentGhosted(NetMessage msg)
        {
            ghostBannerText = msg.team_id == localTeamId
                ? "Vous étiez absent — vos unités sont restées immobiles ce tour-ci."
                : "Adversaire absent — ses unités sont restées immobiles ce tour-ci.";
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
            string resultText = msg.winner_team == 0 ? "Partie interrompue."
                : msg.winner_team == localTeamId ? "VICTOIRE !"
                : "DÉFAITE.";
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
                foreach (UnitState state in snap.units)
                {
                    if (!unitLookup.TryGetValue(state.unit_id, out UnitAI unit) || unit == null) continue;

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
            modeSelectRoot.Q<Button>("btn-deathmatch").clicked += () => { selectedMode = "deathmatch"; ConnectToGameServer(); };
            modeSelectRoot.Q<Button>("btn-zone-control").clicked += () => { selectedMode = "zone_control"; ConnectToGameServer(); };

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
