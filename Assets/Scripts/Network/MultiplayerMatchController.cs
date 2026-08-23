using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Auth;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;

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

        private enum UiState { Hidden, ModeSelect, Login, SignUp, Connecting, Matchmaking, InMatch, MatchOver }
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
#if !UNITY_SERVER
            BindUI();
#endif
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

            client.OnMessage += HandleServerMessage;
            client.OnDisconnected += HandleServerDisconnected;
            client.Connect(SupabaseAuthClient.CurrentSession.access_token);
            client.Send(new NetMessage { type = "join_matchmaking", mode = selectedMode });

            statusMessage = "Recherche d'adversaire...";
            SetUiState(UiState.Matchmaking);
        }

        private void HandleServerDisconnected(string reason)
        {
            if (uiState == UiState.InMatch || uiState == UiState.Matchmaking)
            {
                statusMessage = "Connexion au serveur perdue (" + reason + ").";
                SetUiState(UiState.Login);
                IsActive = false;
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

            UnitSpawnerUI.Instance.ClearAllUnits();
            UnitSpawnerUI.Instance.AutoDeployBattlefield();

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
                ? "Vous étiez absent — l'IA a joué votre tour à votre place."
                : "Adversaire absent — IA de secours active pour son camp.";
            ghostBannerTimer = 4f;
            if (ghostBannerLabel != null)
            {
                ghostBannerLabel.text = "🤖 " + ghostBannerText;
                ghostBannerLabel.style.display = DisplayStyle.Flex;
            }
        }

        private void OnMatchOver(NetMessage msg)
        {
            IsActive = false;
            string resultText = msg.winner_team == 0 ? "Partie interrompue."
                : msg.winner_team == localTeamId ? "🏆 VICTOIRE !"
                : "💀 DÉFAITE.";
            string ratingText = msg.your_new_rating > 0
                ? $"Classement : {msg.your_new_rating} ({(msg.rating_delta >= 0 ? "+" : "")}{msg.rating_delta})"
                : "";

            if (resultLabel != null) resultLabel.text = resultText;
            if (ratingLabel != null) ratingLabel.text = ratingText;
            SetUiState(UiState.MatchOver);
        }

        /// <summary>
        /// Appelé par TacticalPathManager.LancerExecutionTour() quand IsActive est vrai — envoie les
        /// ordres du joueur local au serveur au lieu d'exécuter une simulation locale.
        /// </summary>
        public void SubmitLocalTurn()
        {
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

#if !UNITY_SERVER
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
            authTitleLabel.text = isSignUp ? "⚔️ CRÉER UN COMPTE" : "⚔️ CONNEXION MULTIJOUEUR";
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
    }
}
