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

    // 2026-09-12 (retour joueur : "je ne peux plus rien sélectionner après avoir donné un ordre à
    // une seule unité") : le log Editor.log d'une vraie session de test a confirmé un vrai clic UI
    // Toolkit sur FIN DE TOUR (pas un bug de sélection) juste après un seul ordre — le tour entier
    // partait avec une seule unité sur plusieurs ayant reçu un ordre, sans qu'aucun avertissement ne
    // le signale avant qu'il ne soit trop tard. Fenêtre de confirmation "retapez pour confirmer",
    // même mécanisme que ShowInvalidTapFeedback (aucun nouvel écran/dialogue) : un premier tap sur
    // FIN DE TOUR alors qu'il reste au moins une unité SANS AUCUN ORDRE affiche un avertissement et
    // n'arme rien d'autre ; un second tap dans les 3s qui suivent est traité comme une confirmation
    // explicite et termine bien le tour.
    private float pendingEndTurnConfirmUntil = -1f;

    /// <summary>Vrai si au moins une unité VIVANTE de MON camp (isPlayerControlled — jamais celles de
    /// l'adversaire, hors de mon contrôle) n'a encore aucun point de trajectoire ce tour-ci.</summary>
    private static bool AnyPlayerUnitWithoutOrders()
    {
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.isPlayerControlled && (u.tacticalPath == null || u.tacticalPath.Count == 0))
                return true;
        }
        return false;
    }

    public void LancerExecutionTour()
    {
        if (phaseActuelle == GamePhase.Execution) return;

        if (Time.time > pendingEndTurnConfirmUntil && AnyPlayerUnitWithoutOrders())
        {
            pendingEndTurnConfirmUntil = Time.time + 3f;
#if !UNITY_SERVER
            ShowInvalidTapFeedback("Des unités n'ont reçu aucun ordre — retapez FIN DE TOUR pour confirmer", 3f);
#endif
            return;
        }
        pendingEndTurnConfirmUntil = -1f;

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

        // Lancer les ordres pour toutes les unités (IA et Joueur).
        //
        // PAS DE PLAFOND DE DÉPLACEMENT POUR LE JOUEUR EN SOLO (rétabli le 2026-09-04). Un
        // raccourcissement du chemin au budget du tour (50 m) avait été inséré ici le 2026-09-03
        // pour aligner le solo sur les règles en ligne. Il était à la fois cassé et non désiré :
        //   - CASSÉ : il abandonnait tous les nœuds au-delà du budget. Quand c'était le PREMIER
        //     nœud qui dépassait — le cas courant, la caméra de commandement montre ~190 m de
        //     terrain, le joueur désigne donc naturellement des points à 60-100 m — le chemin
        //     entier était vidé, UnitAI.ExecuterOrdres ne trouvait plus rien à exécuter et l'unité
        //     ne bougeait PAS DU TOUT. Le serveur, lui, ne jette rien : il coupe le tronçon pile à
        //     la limite (TacticalResolver.TruncateToMovementBudget), donc l'unité avance toujours.
        //     Le commentaire retiré prétendait précisément s'aligner sur ce serveur.
        //   - NON DÉSIRÉ : c'était une modification d'équilibrage jamais demandée ; le solo n'a
        //     jamais plafonné le déplacement du joueur.
        // Les ordres du joueur sont donc exécutés en entier, comme avant. Le budget reste appliqué
        // par le SERVEUR pour les parties en ligne, où il départage deux camps.
        foreach (var unit in livingUnits)
        {
            if (!unit.isPlayerControlled) unit.PlanifierTourIA();
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
        // L'action dure tant que des unités avancent ou exécutent des pauses tactiques.
        //
        // Le plafond de sécurité était un 45s fixe, alors qu'un SEUL checkpoint "ATTENDRE 30
        // SECONDES" — proposé dans presque tous les menus contextuels — en consomme 30 à lui seul. Un
        // trajet A(halte 30s) -> B -> C était donc systématiquement coupé à 45s : ForcerFinExecution
        // appelait StopAllCoroutines() puis ResetOrderState(), B et C n'étaient jamais exécutés, la
        // ligne de trajet disparaissait et le joueur n'en était pas informé. Le plafond tient
        // maintenant compte des haltes réellement demandées.
        float safetyCap = ComputeExecutionSafetyCap();
        float safetyMovementTimer = 0f;
        while (UnitesEncoreEnDeplacementOuAction() && safetyMovementTimer < safetyCap)
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

    /// <summary>Plafond de sécurité du tour : une allocation de base pour le déplacement, plus le
    /// temps des haltes tactiques RÉELLEMENT demandées sur le chemin le plus chargé. Empêche le
    /// plafond de trancher au milieu d'un ordre légitime, tout en gardant un garde-fou borné contre
    /// une unité définitivement bloquée.
    ///
    /// Le total est PLAFONNÉ (correctif 2026-09-04). Ce plafond est global à la boucle d'exécution :
    /// l'allonger allonge d'autant la fenêtre pendant laquelle une unité définitivement coincée
    /// (char nez à nez, seuil de porte inatteignable) retient toute l'escouade ET le joueur devant
    /// un écran figé. Les haltes s'exécutant en PARALLÈLE, empiler leur durée n'a pas de sens :
    /// trois haltes portaient le plafond à plus de deux minutes.</summary>
    private float ComputeExecutionSafetyCap()
    {
        const float baseMovementAllowance = 45.0f;
        const float perWaitNodeSeconds = 31.0f; // 30s de halte + une marge de transition
        const float absoluteCapSeconds = 110.0f; // ~45s de trajet + 2 haltes, jamais plus

        int maxWaitNodes = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || u.tacticalPath == null) continue;

            int waitNodes = 0;
            for (int n = 0; n < u.tacticalPath.Count; n++)
            {
                if (u.tacticalPath[n].action == NodeAction.Attendre30s) waitNodes++;
            }
            if (waitNodes > maxWaitNodes) maxWaitNodes = waitNodes;
        }

        return Mathf.Min(baseMovementAllowance + perWaitNodeSeconds * maxWaitNodes, absoluteCapSeconds);
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

        // Compter les unités qui n'ont PAS fini leur trajet avant de tout effacer : la troncature était
        // entièrement silencieuse, le joueur voyait juste sa ligne de trajet disparaître et croyait à
        // un ordre perdu.
        //
        // Uniquement les unités DU JOUEUR (correctif 2026-09-04) : le message lui demande de
        // « redonner un ordre », ce qui n'a aucun sens pour une unité ennemie qu'il ne commande pas et
        // ne voit peut-être même pas. Il annonçait des unités inexistantes de son point de vue.
        int truncatedUnits = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || !u.isPlayerControlled) continue;
            if (u.tacticalPath == null || u.tacticalPath.Count == 0) continue;
            if (u.GetCurrentNodeIndex() < u.tacticalPath.Count) truncatedUnits++;
        }

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead)
            {
                u.StopAllCoroutines();
                u.ResetOrderState();
            }
        }

        if (truncatedUnits > 0)
        {
            string message = truncatedUnits == 1
                ? "1 unité n'a pas terminé son trajet — redonnez-lui un ordre."
                : $"{truncatedUnits} unités n'ont pas terminé leur trajet — redonnez-leur un ordre.";
            Debug.Log($"<color=yellow>[TacticalPathManager] {message}</color>");
#if !UNITY_SERVER
            if (UnitSpawnerUI.Instance != null) UnitSpawnerUI.Instance.ShowMessage(message, 3.5f);
#endif
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

        // Poser le drapeau ICI (correctif 2026-09-04) : il ne l'était NULLE PART, alors que trois
        // garde-fous le lisent — le return anticipé d'Update, hideBottomBar dans RefreshTacticalUI et
        // le masquage du dock de déploiement. Tous étaient donc inatteignables. Or le recouvrement
        // visuel ne suffit pas : UIScreenManager force pickingMode = Ignore sur le "root" de chaque
        // écran, les taps traversent donc le voile de fin de partie et le joueur pouvait continuer à
        // sélectionner ses unités, tracer des trajets et lancer un tour DERRIÈRE l'écran VICTOIRE.
        IsSoloGameOver = true;

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
                // MASQUER AVANT DE RECHARGER (correctif 2026-09-04). UIScreenManager est en
                // DontDestroyOnLoad et se détruit lui-même s'une instance existe déjà : ses
                // VisualElement survivent donc intacts à LoadScene, écran GameOver toujours en
                // display:Flex. Comme rien n'appelait jamais SetVisible("GameOver", false), le voile
                // à 85% d'opacité restait collé par-dessus la partie rechargée, y compris par-dessus
                // le menu de démarrage : le joueur ne pouvait plus rien atteindre et seul un
                // redémarrage complet de l'application débloquait la situation. Le chemin
                // multijoueur, lui, encadrait déjà son LoadScene par HideAll().
                UIScreenManager.Instance.HideAll();
                IsSoloGameOver = false; // la scène rechargée redémarre une partie jouable
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
