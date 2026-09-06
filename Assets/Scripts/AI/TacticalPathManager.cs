using UnityEngine;

/// <summary>
/// Point d'entrée du gameplay tactique tour-par-tour : cycle de phases (Planification/Exécution)
/// et état de sélection partagés par tous les fichiers partiels de cette classe —
///   - TacticalPathManager_Input.cs : détection du tap/clic (sélection, cascade porte/fenêtre/bâtiment/sol)
///   - TacticalPathManager_Selection.cs : sélection d'unité (clic direct, cloche blessés, squad-bar)
///   - TacticalPathManager_ContextMenu.cs : ouverture/fermeture des menus d'ordre, confirmation
///   - TacticalPathManager_UI.cs : binding UI Toolkit (barre du bas, squad-bar, ContextMenu)
///   - TacticalPathManager_PathDrawing.cs : tracé des trajectoires (LineRenderer par unité)
///   - TacticalPathManager_Execution.cs : déroulement du tour (Fin de tour → Planification)
/// </summary>
public partial class TacticalPathManager : MonoBehaviour
{
    public enum GamePhase { Planification, CreationPath, Execution }
    // Valeur "1" volontairement absente : occupée jusqu'au 2026-08-30 par Attendre5Min, une action
    // JAMAIS exposée dans aucun menu contextuel (voir TacticalPathManager_ContextMenu.cs — seul
    // "ATTENDRE 30 SECONDES" existe côté UI) et dont le nom était de toute façon trompeur (son
    // implémentation dans UnitAI_Movement.cs attendait 2.5s, jamais 5 minutes). Code mort supprimé
    // plutôt que renommé. Les valeurs explicites des autres actions ne bougent PAS : elles restent
    // strictement identiques pour ne pas casser la compatibilité du protocole réseau (NetMessage.
    // PathNode.action transite en entier brut, jamais par nom).
    // Descendre = 12 (ajouté le 2026-09-03) : jusque-là "DESCENDRE DU TOIT" réutilisait Escalade,
    // ce qui rendait la descente IMPOSSIBLE des deux côtés. Côté client, UnitAI_Movement testait
    // l'action avant la différence de hauteur, donc ExecuteClimb tournait "à l'envers" et
    // ExecuteClimbDown était du code mort ; l'unité restait marquée isRooftopSniper avec son
    // NavMeshAgent désactivé pour le reste de la partie. Côté serveur, l'Escalade sur un point de
    // rue ne trouvait aucun bâtiment et repliait sur un Y=3 arbitraire : l'unité finissait le tour
    // suspendue en l'air au-dessus de la chaussée, toujours en strate Toit. Une action dédiée lève
    // l'ambiguïté au lieu de deviner la direction d'après la géométrie.
    public enum NodeAction { Continuer = 0, Guetter = 2, Embuscade = 3, Escalade = 4, GarnisonFenetre = 5, EntrerBatiment = 6, SortirBatiment = 7, GuetterPorte = 8, Attendre30s = 9, SeCacher = 10, TirMortier = 11, Descendre = 12 }

    [Header("Système")]
    public GamePhase phaseActuelle = GamePhase.Planification;

    [System.Serializable]
    public struct TacticalNode
    {
        public Vector3 position;
        public NodeAction action;
    }

    [Header("UI Références")]
    public GameObject menuPanel;

    public GameObject uniteSelectionnee;
    // Vector3.positiveInfinity (pas Vector3.zero) comme sentinelle "aucun clic en attente" : le plan
    // "Sol" est centré sur l'origine du monde (voir MapTileLoader.GenerateQuadMesh), donc un tap au
    // centre de la carte résolvait littéralement à (0,0,0) et faisait disparaître silencieusement la
    // prévisualisation du tracé (voir DessinerTousLesChemins).
    private Vector3 positionClicTemporaire = Vector3.positiveInfinity;
    private Novgov.Interaction.DoorInteraction selectedDoor = null;
    private Novgov.Interaction.WindowInteraction selectedWindow = null;
    private BuildingStructure selectedBuilding = null;
    private RoadBarrier selectedBarricade = null;
    private bool isBuildingSelected = false;
    private bool isDoorSelected = false;
    private bool isWindowSelected = false;
    private bool isExitDoorAction = false;
    private bool isGroundCheckpointSelected = false;
    private bool isBarricadeSelected = false;
    private bool isBarricadeDeployMenuOpen = false;
    private bool isNearBuildingWall = false;

    /// <summary>Un menu d'ordre (porte/fenêtre/bâtiment/sol/barricade) est actuellement ouvert.
    /// Centralisé pour ne plus jamais oublier d'y ajouter un nouveau type de sélection dans l'un
    /// des multiples endroits qui referment un menu resté ouvert avant d'en ouvrir un autre (voir
    /// historique des bugs "trajet confirmé après ANNULER/tap invalide" — le même oubli avec un
    /// 5e flag ajouté à la main aurait pu se reproduire ici).</summary>
    private bool AnyOrderMenuOpen => isDoorSelected || isWindowSelected || isBuildingSelected || isGroundCheckpointSelected || isBarricadeSelected || isBarricadeDeployMenuOpen;

    private LineRenderer lineRenderer;
    private static bool isPathsDirty = true;

    // Dernière valeur de positionClicTemporaire pour laquelle un tracé a été produit — sert à
    // détecter un changement de cible d'aperçu (voir Update).
    private Vector3 lastPreviewTargetDrawn = Vector3.positiveInfinity;

    // Un redessin est demandé pour la seule unité sélectionnée (changement de cible d'aperçu).
    // Distinct du drapeau statique isPathsDirty, qui invalide le tracé de TOUTES les unités.
    private bool previewRedrawRequested = false;

    /// <summary>Y a-t-il une cible de tap en attente ?
    ///
    /// NE JAMAIS tester la sentinelle avec == / != sur Vector3. L'opérateur d'égalité de Unity ne
    /// compare pas les composantes : il calcule (a-b).sqrMagnitude et le compare à un epsilon. Avec
    /// des composantes infinies, inf - inf = NaN, et TOUTE comparaison impliquant NaN est fausse —
    /// donc `Vector3.positiveInfinity == Vector3.positiveInfinity` vaut FALSE et le `!=` vaut TRUE.
    /// Autrement dit, `positionClicTemporaire != Vector3.positiveInfinity` était TOUJOURS vrai :
    /// le jeu se croyait en permanence en attente d'un aperçu vers un point à l'infini, redessinait
    /// tous les tracés à chaque frame et poussait un point infini dans les LineRenderer.
    /// Un test explicite sur l'infini est la seule façon correcte de lire cette sentinelle.
    /// La logique vit dans Novgov.Core.VectorSentinel — classe pure, donc couverte par les tests
    /// automatiques (voir Assets/Editor/TacticalCoreSelfTest_Sentinel.cs).</summary>
    private static bool HasTapTarget(Vector3 p)
    {
        return Novgov.Core.VectorSentinel.IsSet(p);
    }

    /// <summary>Deux cibles d'aperçu désignent-elles le même point ? Traite correctement le cas
    /// "les deux sont la sentinelle" (voir <see cref="HasTapTarget"/>), que l'opérateur == de
    /// Vector3 rapporte à tort comme différent.</summary>
    private static bool SameTapTarget(Vector3 a, Vector3 b)
    {
        return Novgov.Core.VectorSentinel.Same(a, b);
    }

    public static void SetPathsDirty() { isPathsDirty = true; }

    /// <summary>Solo uniquement (voir TacticalPathManager_Execution.CheckSoloGameOver) — un camp a
    /// été entièrement anéanti. Vérifié explicitement dans Update() plutôt que de compter
    /// uniquement sur l'ordre d'affichage de l'écran GameOver : un recouvrement visuel qui rate ne
    /// doit jamais laisser le joueur continuer à donner des ordres après la fin de la partie.
    /// Remis à false dans Awake() : un rechargement de scène via SceneManager.LoadScene ne
    /// réinitialise PAS les champs static, contrairement à un vrai redémarrage du Play Mode.</summary>
    public static bool IsSoloGameOver = false;

    public static TacticalPathManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
        IsSoloGameOver = false;
#if !UNITY_SERVER
        BindTacticalUI();
#endif
    }

    void Start()
    {
        Instance = this;
        // Initialisation automatique du LineRenderer
        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
        {
            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.startWidth = 0.3f;
            lineRenderer.endWidth = 0.3f;
            lineRenderer.material = SafeMaterialFactory.CreateUnlit(Color.cyan);
            lineRenderer.startColor = Color.cyan;
            lineRenderer.endColor = Color.blue;
        }
        lineRenderer.positionCount = 0;

        // Initialisation automatique du gestionnaire de déploiement d'unités
        if (GetComponent<UnitSpawnerUI>() == null)
        {
            gameObject.AddComponent<UnitSpawnerUI>();
        }

        // Initialisation automatique du Radar Tactique
        if (TacticalRadarUI.Instance != null)
        {
            Debug.Log("[TacticalPathManager] 🛰️ Radar Tactique prêt et connecté.");
        }
    }

    void Update()
    {
#if UNITY_SERVER
        return; // Aucune entrée tactile/souris à traiter côté serveur headless.
#else
        RefreshTacticalUI();
#endif
        // Pulsation "battement de cœur" des lignes de trajectoire : ne touche qu'un multiplicateur
        // de largeur (aucun recalcul de chemin/positions), donc s'exécute chaque frame — y compris
        // pendant l'Exécution où le tracé restant reste affiché — sans réintroduire le coût que
        // isPathDirty a justement été ajouté pour éliminer.
        AnimateTacticalLinesHeartbeat();

        // Partie terminée (voir CheckSoloGameOver) : plus aucun ordre, tracé, ni tap ne doit être
        // traité — vérifié explicitement ici plutôt que de compter uniquement sur le recouvrement
        // visuel de l'écran GameOver.
        if (IsSoloGameOver) return;

        // Bloquer l'assignation de nouveaux ordres pendant l'exécution ou pendant le placement d'unités
        if (phaseActuelle == GamePhase.Execution || UnitSpawnerUI.IsPlacingUnit) return;

        // APERÇU DE DESTINATION (correctif 2026-09-03). Le tracé n'était redessiné que sur
        // isPathsDirty — posé uniquement à la sélection d'une unité et à l'ajout/retrait d'un nœud —
        // ou en phase CreationPath, valeur qui n'est JAMAIS assignée nulle part dans le projet. Aucun
        // tap sur la carte ne déclenchait donc de redessin, et le bloc d'aperçu de
        // TacticalPathManager_PathDrawing (celui qui utilise AppendGridPathSegment, écrit exprès pour
        // montrer le VRAI chemin de la grille serveur) était inatteignable : le joueur confirmait
        // chaque ordre à l'aveugle et ne découvrait le trajet réel qu'après avoir appuyé sur TERMINÉ.
        //
        // Surveiller positionClicTemporaire couvre d'un seul coup TOUS les chemins de tap (sol,
        // bâtiment, porte, fenêtre, toit) et toutes les fermetures de menu, sans dépendre du fait que
        // chacun d'eux pense à lever le drapeau.
        //
        // La comparaison passe OBLIGATOIREMENT par SameTapTarget : écrite avec l'opérateur != de
        // Vector3, elle était vraie à chaque frame (voir HasTapTarget) et ce bloc marquait donc le
        // tracé "à redessiner" 60 fois par seconde — recalcul A*/NavMesh de tous les chemins de
        // toutes les unités en continu sur mobile, plus l'aperçu vers un point infini.
        //
        // Le drapeau posé est CELUI DE L'UNITÉ SÉLECTIONNÉE, jamais le drapeau statique
        // isPathsDirty (correctif 2026-09-04) : l'aperçu ne concerne que cette unité, alors que
        // isPathsDirty force le recalcul du tracé de TOUTES les unités du joueur — chacune repayant
        // un GetComponent<NavMeshAgent>, puis par nœud un NavMesh.SamplePosition de rayon 10 m, un
        // test de décombres et un CalculatePath (ou un A* de grille en ligne). À chaque tap.
        if (!SameTapTarget(positionClicTemporaire, lastPreviewTargetDrawn))
        {
            lastPreviewTargetDrawn = positionClicTemporaire;
            if (uniteSelectionnee != null)
            {
                UnitAI selectedForPreview = uniteSelectionnee.GetComponent<UnitAI>();
                if (selectedForPreview != null) selectedForPreview.isPathDirty = true;
            }
            previewRedrawRequested = true;
        }

        // Tracé fluide et ultra-léger (recalculé uniquement si dirty ou en phase dynamique)
        if (isPathsDirty || previewRedrawRequested || phaseActuelle == GamePhase.CreationPath)
        {
            previewRedrawRequested = false;
            DessinerTousLesChemins();
        }

        // 1. Si le panneau d'action (porte/fenêtre/bâtiment) est ouvert, on attend l'interaction du joueur
        if (menuPanel != null && menuPanel.activeSelf) return;

        HandlePointerInput();
    }
}
