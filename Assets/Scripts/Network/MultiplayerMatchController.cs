using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StreetAct.Auth;
using UnityEngine;
using UnityEngine.AI;

namespace StreetAct.Network
{
    /// <summary>
    /// Orchestration complète du mode multijoueur côté client : login/inscription, matchmaking,
    /// envoi des ordres, lecture des snapshots renvoyés par le serveur. Voir
    /// Assets/_ServerDocs/multiplayer/05-client-integration.md pour le contexte d'intégration.
    /// Le mode solo (TacticalAIPlanner local) n'est jamais impacté : voir le garde
    /// "MultiplayerMatchController.IsActive" ajouté dans TacticalPathManager.LancerExecutionTour().
    /// </summary>
    public class MultiplayerMatchController : MonoBehaviour
    {
        public static MultiplayerMatchController Instance { get; private set; }
        public static bool IsActive { get; private set; }

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

        private enum UiState { Hidden, Login, SignUp, Connecting, Matchmaking, InMatch, MatchOver }
        private UiState uiState = UiState.Hidden;

        private string emailField = "";
        private string passwordField = "";
        private string usernameField = "";
        private string statusMessage = "";

        private int localTeamId = 0;
        private int currentTurnNumber = 1;
        private int lastServerSecondsRemaining = -1;
        private string ghostBannerText = "";
        private float ghostBannerTimer = 0f;
        private string matchOverText = "";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Update()
        {
            if (ghostBannerTimer > 0f) ghostBannerTimer -= Time.deltaTime;
        }

        public void BeginLoginFlow()
        {
            uiState = UiState.Login;
            statusMessage = "";
        }

        // =====================================================================
        // Auth
        // =====================================================================

        private async void HandleSignIn()
        {
            statusMessage = "Connexion en cours...";
            uiState = UiState.Connecting;
            var (ok, error) = await SupabaseAuthClient.SignIn(emailField, passwordField);
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                uiState = UiState.Login;
                return;
            }
            ConnectToGameServer();
        }

        private async void HandleSignUp()
        {
            statusMessage = "Création du compte...";
            uiState = UiState.Connecting;
            var (ok, error) = await SupabaseAuthClient.SignUp(emailField, passwordField, usernameField);
            if (!ok)
            {
                statusMessage = "Échec : " + error;
                uiState = UiState.SignUp;
                return;
            }
            ConnectToGameServer();
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
            client.Send(new NetMessage { type = "join_matchmaking" });

            uiState = UiState.Matchmaking;
            statusMessage = "Recherche d'adversaire...";
        }

        private void HandleServerDisconnected(string reason)
        {
            if (uiState == UiState.InMatch || uiState == UiState.Matchmaking)
            {
                statusMessage = "Connexion au serveur perdue (" + reason + ").";
                uiState = UiState.Login;
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
            uiState = UiState.InMatch;
            if (TacticalPathManager.Instance != null)
                TacticalPathManager.Instance.phaseActuelle = TacticalPathManager.GamePhase.Planification;
        }

        private void OnOpponentGhosted(NetMessage msg)
        {
            ghostBannerText = msg.team_id == localTeamId
                ? "Vous étiez absent — l'IA a joué votre tour à votre place."
                : "Adversaire absent — IA de secours active pour son camp.";
            ghostBannerTimer = 4f;
        }

        private void OnMatchOver(NetMessage msg)
        {
            IsActive = false;
            uiState = UiState.MatchOver;
            matchOverText = msg.winner_team == 0 ? "Partie interrompue."
                : msg.winner_team == localTeamId ? "🏆 VICTOIRE !"
                : "💀 DÉFAITE.";
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
        // UI (IMGUI, cohérent avec le reste du projet — voir GameManagerUI/UnitSpawnerUI)
        // =====================================================================

        void OnGUI()
        {
#if UNITY_SERVER
            return;
#else
            switch (uiState)
            {
                case UiState.Login:
                case UiState.SignUp:
                    DrawAuthScreen();
                    break;
                case UiState.Connecting:
                case UiState.Matchmaking:
                    DrawWaitingScreen();
                    break;
                case UiState.InMatch:
                    DrawInMatchHud();
                    break;
                case UiState.MatchOver:
                    DrawMatchOverScreen();
                    break;
            }
#endif
        }

#if !UNITY_SERVER
        private void DrawAuthScreen()
        {
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            Matrix4x4 origMat = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;
            float virtualH = Screen.height / uiScale;
            bool isSignUp = uiState == UiState.SignUp;

            float w = Mathf.Min(360f, virtualW - 30f);
            float h = isSignUp ? 320f : 260f;
            float x = (virtualW - w) * 0.5f;
            float y = (virtualH - h) * 0.5f;

            GUI.depth = -200;
            GUIStyle titleStyle = new GUIStyle(GUI.skin.box) { fontSize = 15, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = Color.white;
            GUI.Box(new Rect(x, y, w, h), isSignUp ? "⚔️ CRÉER UN COMPTE" : "⚔️ CONNEXION MULTIJOUEUR", titleStyle);

            float fy = y + 40;
            GUI.Label(new Rect(x + 15, fy, w - 30, 20), "Email :");
            emailField = GUI.TextField(new Rect(x + 15, fy + 20, w - 30, 30), emailField);

            fy += 58;
            GUI.Label(new Rect(x + 15, fy, w - 30, 20), "Mot de passe :");
            passwordField = GUI.PasswordField(new Rect(x + 15, fy + 20, w - 30, 30), passwordField, '*');

            fy += 58;
            if (isSignUp)
            {
                GUI.Label(new Rect(x + 15, fy, w - 30, 20), "Pseudo :");
                usernameField = GUI.TextField(new Rect(x + 15, fy + 20, w - 30, 30), usernameField);
                fy += 58;
            }

            GUIStyle btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(x + 15, fy, w - 30, 40), isSignUp ? "Créer le compte" : "Se connecter", btnStyle))
            {
                if (isSignUp) HandleSignUp(); else HandleSignIn();
            }

            fy += 46;
            GUIStyle linkStyle = new GUIStyle(GUI.skin.button) { fontSize = 11 };
            if (GUI.Button(new Rect(x + 15, fy, w - 30, 28), isSignUp ? "J'ai déjà un compte" : "Créer un compte", linkStyle))
            {
                uiState = isSignUp ? UiState.Login : UiState.SignUp;
                statusMessage = "";
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                GUIStyle msgStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 11 };
                msgStyle.normal.textColor = Color.yellow;
                GUI.Label(new Rect(x, y + h + 6, w, 40), statusMessage, msgStyle);
            }

            GUI.matrix = origMat;
        }

        private void DrawWaitingScreen()
        {
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            Matrix4x4 origMat = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;
            float virtualH = Screen.height / uiScale;

            GUIStyle style = new GUIStyle(GUI.skin.box) { fontSize = 14, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.cyan;
            GUI.Box(new Rect(virtualW / 2 - 160, virtualH / 2 - 30, 320, 60), statusMessage, style);

            GUI.matrix = origMat;
        }

        private void DrawInMatchHud()
        {
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            Matrix4x4 origMat = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;

            if (lastServerSecondsRemaining >= 0)
            {
                GUIStyle timerStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, fontStyle = FontStyle.Bold };
                timerStyle.normal.textColor = lastServerSecondsRemaining <= 10 ? Color.red : Color.white;
                GUI.Box(new Rect(virtualW - 130, 60, 115, 34), $"⏱️ {lastServerSecondsRemaining}s", timerStyle);
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                GUIStyle msgStyle = new GUIStyle(GUI.skin.box) { fontSize = 12 };
                msgStyle.normal.textColor = Color.yellow;
                GUI.Box(new Rect(virtualW / 2 - 150, 60, 300, 30), statusMessage, msgStyle);
            }

            if (ghostBannerTimer > 0f)
            {
                GUIStyle ghostStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, fontStyle = FontStyle.Bold };
                ghostStyle.normal.textColor = new Color(1f, 0.6f, 0.1f);
                GUI.Box(new Rect(virtualW / 2 - 170, 96, 340, 34), "🤖 " + ghostBannerText, ghostStyle);
            }

            GUI.matrix = origMat;
        }

        private void DrawMatchOverScreen()
        {
            float uiScale = Mathf.Clamp(Screen.width / 450f, 1.35f, 2.2f);
            Matrix4x4 origMat = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float virtualW = Screen.width / uiScale;
            float virtualH = Screen.height / uiScale;

            GUIStyle style = new GUIStyle(GUI.skin.box) { fontSize = 20, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect(virtualW / 2 - 150, virtualH / 2 - 60, 300, 80), matchOverText, style);

            if (GUI.Button(new Rect(virtualW / 2 - 90, virtualH / 2 + 30, 180, 42), "Retour au menu"))
            {
                uiState = UiState.Hidden;
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }

            GUI.matrix = origMat;
        }
#endif
    }
}
