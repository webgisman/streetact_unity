using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public partial class TacticalPathManager
{
    // ==========================================
    // EXÉCUTION DU TOUR (lancement, coroutine dynamique, fin de tour)
    // ==========================================

    [Header("Paramètres de Tour")]
    private AudioSource uiAudioSource;
    private Coroutine turnExecutionCoroutine;

    // Capturés au lancement du tour pour détecter une victoire/défaite en mode solo (voir
    // ForcerFinExecution) : compare l'effectif de chaque camp AVANT/APRÈS ce tour, plutôt que de
    // juste regarder qui est à 0 à la fin — sinon un camp jamais déployé (0 unité dès le départ,
    // ex: l'ennemi si le joueur n'a encore rien placé pour lui) déclencherait une fausse victoire
    // dès le tout premier "Fin de tour".
    private int team1CountAtTurnStart;
    private int team2CountAtTurnStart;

    private static int CountLivingByTeam(int team)
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.teamID == team) count++;
        }
        return count;
    }

    public void LancerExecutionTour()
    {
        if (phaseActuelle == GamePhase.Execution) return;

        team1CountAtTurnStart = CountLivingByTeam(1);
        team2CountAtTurnStart = CountLivingByTeam(2);

        // Son de validation et Vibration
        if (uiAudioSource == null) uiAudioSource = gameObject.AddComponent<AudioSource>();
        AudioClip startSound = ProceduralAudioBuilder.CreateClickSound();
        if (startSound != null) uiAudioSource.PlayOneShot(startSound, 0.7f);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif

        phaseActuelle = GamePhase.Execution;
        Debug.Log("--- DÉBUT DE LA PHASE D'EXÉCUTION (Action en cours) ---");

        SelectionnerUnite(null); // On désélectionne tout
        if (menuPanel != null) menuPanel.SetActive(false);

        // Nettoyer les anciens marqueurs de waypoints holographiques au début de l'exécution
        WaypointMarker[] existingMarkers = FindObjectsByType<WaypointMarker>(FindObjectsInactive.Include);
        foreach (var wm in existingMarkers)
        {
            if (wm != null) Destroy(wm.gameObject);
        }

        // MULTIJOUEUR : le calcul du tour est entièrement délégué au serveur autoritaire (voir
        // Assets/Scripts/Network/MultiplayerMatchController.cs). On envoie nos ordres et on
        // n'exécute JAMAIS de simulation locale ni de planification IA pour l'adversaire.
        if (Novgov.Network.MultiplayerMatchController.IsActive)
        {
            Novgov.Network.MultiplayerMatchController.Instance.SubmitLocalTurn();
            return;
        }

        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        List<UnitAI> livingUnits = new List<UnitAI>();
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead) livingUnits.Add(u);
        }

        // Lancer les ordres pour toutes les unités (IA et Joueur)
        foreach (var unit in livingUnits)
        {
            if (!unit.isPlayerControlled)
            {
                unit.PlanifierTourIA();
            }
            unit.ExecuterOrdres();
        }

        if (turnExecutionCoroutine != null) StopCoroutine(turnExecutionCoroutine);
        turnExecutionCoroutine = StartCoroutine(ExecuterTourCoroutine());
    }

    /// <summary>
    /// Coroutine d'action dynamique : le tour dure le temps que les unités parcourent leur chemin,
    /// exécutent leurs checkpoints (Attendre, Guetter), tirent sur les ennemis croisés,
    /// puis balayent et sécurisent leur zone d'arrivée avant de redonner la main.
    /// </summary>
    private System.Collections.IEnumerator ExecuterTourCoroutine()
    {
        // Laisser le temps aux agents et coroutines de démarrer
        yield return new WaitForSeconds(0.4f);

        // 1. PHASE DE PROGRESSION & ACTIONS AUX CHECKPOINTS
        // L'action dure tant que des unités avancent ou exécutent des pauses tactiques (supporte l'attente 30s)
        float safetyMovementTimer = 0f;
        while (UnitesEncoreEnDeplacementOuAction() && safetyMovementTimer < 45.0f)
        {
            safetyMovementTimer += Time.deltaTime;
            yield return null;
        }

        // 2. PHASE DE BALAYAGE FINAL ET SÉCURISATION DU SECTEUR
        // Si les unités sont arrivées mais qu'un duel est en cours (cibles visibles en portée),
        // on laisse jusqu'à 2 secondes pour échanger les tirs finaux
        float combatResolutionTimer = 0f;
        while (UnitesEncoreEnCombat() && combatResolutionTimer < 2.0f)
        {
            combatResolutionTimer += Time.deltaTime;
            yield return null;
        }

        // Si personne ne bougeait et personne n'était en combat, laisser un bref instant de réactivité (0.8s)
        if (safetyMovementTimer == 0f && combatResolutionTimer == 0f)
        {
            yield return new WaitForSeconds(0.8f);
        }

        ForcerFinExecution();
    }

    /// <summary>
    /// Vérifie si au moins une unité vivante est en train de marcher ou d'effectuer une action de checkpoint.
    /// </summary>
    private bool UnitesEncoreEnDeplacementOuAction()
    {
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.IsMovingOrActing())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Vérifie si au moins une unité a une cible ennemie vivante dans sa ligne de mire et à portée.
    /// </summary>
    private bool UnitesEncoreEnCombat()
    {
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.HasActiveTargetInRange())
            {
                return true;
            }
        }
        return false;
    }

    public void ForcerFinExecution()
    {
        if (turnExecutionCoroutine != null)
        {
            StopCoroutine(turnExecutionCoroutine);
            turnExecutionCoroutine = null;
        }

        phaseActuelle = GamePhase.Planification;

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead)
            {
                u.StopAllCoroutines();
                u.ResetOrderState();
            }
        }

        // Nettoyer les marqueurs au sol
        foreach (var marker in FindObjectsByType<WaypointMarker>(FindObjectsInactive.Exclude))
        {
            Destroy(marker.gameObject);
        }

        Debug.Log("--- FIN DU TOUR. SECTEUR BALAYÉ. RETOUR À LA PLANIFICATION ---");

#if !UNITY_SERVER
        // Le mode multijoueur a sa propre fin de partie côté serveur (MultiplayerMatchController/
        // MatchSessionManager) — ne pas interférer ici, ce check ne concerne que le solo, qui
        // jusqu'ici ne se terminait JAMAIS (boucle infinie même un camp totalement anéanti).
        if (!Novgov.Network.MultiplayerMatchController.IsActive) CheckSoloGameOver();
#endif
    }

    /// <summary>Victoire/défaite en solo : un camp qui EXISTAIT en début de tour (au moins 1 unité
    /// vivante) et qui se retrouve à 0 après ce tour a perdu. Comparé au début de tour plutôt qu'à
    /// "0 maintenant" tout court pour ne pas déclarer une victoire au tout premier "Fin de tour"
    /// simplement parce qu'un camp n'a encore jamais été déployé.</summary>
    private void CheckSoloGameOver()
    {
        int team1Now = CountLivingByTeam(1);
        int team2Now = CountLivingByTeam(2);

        bool team1Wiped = team1CountAtTurnStart > 0 && team1Now == 0;
        bool team2Wiped = team2CountAtTurnStart > 0 && team2Now == 0;
        if (!team1Wiped && !team2Wiped) return;

        string result = (team1Wiped && team2Wiped) ? "MATCH NUL"
            : team2Wiped ? "VICTOIRE" : "DÉFAITE";
        string detail = (team1Wiped && team2Wiped) ? "Les deux camps ont été anéantis."
            : team2Wiped ? "L'ennemi a été entièrement anéanti." : "Votre escouade a été entièrement anéantie.";
        ShowSoloGameOver(result, detail);
    }

    private void ShowSoloGameOver(string result, string detail)
    {
        if (UIScreenManager.Instance == null) return;
        var root = UIScreenManager.Instance.GetScreen("GameOver");
        if (root == null)
        {
            Debug.LogError("[TacticalPathManager] Écran 'GameOver' introuvable (UXML non chargé) — victoire/défaite ne pourra pas s'afficher.");
            return;
        }

        var resultLabel = root.Q<UnityEngine.UIElements.Label>("result-label");
        var detailLabel = root.Q<UnityEngine.UIElements.Label>("detail-label");
        var menuButton = root.Q<UnityEngine.UIElements.Button>("menu-button");
        if (resultLabel != null) resultLabel.text = result;
        if (detailLabel != null) detailLabel.text = detail;
        if (menuButton != null && menuButton.userData == null)
        {
            // Câblé une seule fois — userData sert de marqueur puisque cet écran, contrairement
            // aux autres, n'est bindé qu'à la demande (pas de BindXUI() dédié au lancement).
            menuButton.userData = true;
            menuButton.clicked += () =>
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            };
        }
        UIScreenManager.Instance.SetVisible("GameOver", true);
    }

    public void SignalerFinMouvement(UnitAI unit)
    {
        // Résolution dynamique gérée par ExecuterTourCoroutine
    }
}
