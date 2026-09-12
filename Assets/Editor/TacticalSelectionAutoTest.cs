using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Test Éditeur AUTOMATISÉ de la sélection tactile (2026-09-12) — répond directement à "il faut
/// changer complètement le mode de debug et de diagnostique" : jusqu'ici, chaque bug de sélection
/// (2026-09-09 vehicle-pivot, 2026-09-12 char-vole-fantassin) n'a été confirmé qu'en lisant le vrai
/// Editor.log d'une session de test humaine en pleine nuit — aucun moyen de le rejouer soi-même.
///
/// Ce fichier exécute un VRAI Physics.Raycast contre de VRAIS UnitAI (colliders configurés par leur
/// vrai UnitAI.Start(), invoqué par réflexion puisqu'il est privé et normalement appelé par Unity
/// juste avant la première frame — inutile en mode Éditeur hors Play Mode) et appelle directement
/// TacticalPathManager.ResolveClosestPlayerUnit (extrait de HandlePointerInput le même jour pour
/// exactement cet usage). Pas besoin du Play Mode ni de l'Input System : OnEnable() (qui inscrit
/// l'unité dans UnitAI.AllLivingUnits) s'exécute déjà de lui-même à l'AddComponent, même hors Play
/// Mode — seul Start() (déféré par Unity jusqu'à la première frame, qui n'arrive jamais hors Play
/// Mode) a besoin d'un coup de pouce par réflexion.
///
/// Usage : "Unity.exe -batchmode -nographics -quit -buildTarget StandaloneWindows64
/// -standaloneBuildSubtarget Player -projectPath ... -executeMethod TacticalSelectionAutoTest.RunAll
/// -logFile chemin.log" — cherche "[TacticalSelectionAutoTest]" dans le log. Toute assertion ratée
/// lève une exception, que Unity remonte en code de sortie non nul en mode batch (mêmes conventions
/// que ServerBuildScript/AndroidTestBuildScript).
///
/// PIÈGE TROUVÉ EN CONDITIONS RÉELLES (2026-09-12) — TOUJOURS préciser -buildTarget ET
/// -standaloneBuildSubtarget Player explicitement, JAMAIS les omettre en supposant qu'Unity revient
/// à un mode Éditeur normal par défaut : la cible/sous-cible de build actives sont un état PERSISTANT
/// du projet (EditorUserBuildSettings, sur disque), pas remis à zéro entre deux invocations
/// "-executeMethod" séparées. Un précédent build serveur (ServerBuildScript.BuildLinuxServer,
/// "-standaloneBuildSubtarget Server") laisse UNITY_SERVER défini pour TOUTE invocation suivante tant
/// qu'on ne repasse pas explicitement sur "Player" — auquel cas tout le code client-only (ici,
/// TacticalPathManager_UI.cs en entier, y compris CycleSelectGroup) est invisible par réflexion,
/// sans le moindre message d'erreur qui l'indique clairement (juste "méthode introuvable").
///
/// Ne sauvegarde JAMAIS la scène temporaire créée pour ces tests — aucun risque de polluer les
/// vraies scènes du projet (Assets/Scenes/SampleScene.unity, etc.).
/// </summary>
public static class TacticalSelectionAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== TacticalSelectionAutoTest : sélection tactile en conditions réelles (raycast + colliders réels) ===");

        // Une NOUVELLE scène vide avant CHAQUE test (pas une seule pour les 3) : sans ça, les
        // GameObjects de test d'un test précédent restent dans la scène ET dans UnitAI.
        // AllLivingUnits, et peuvent fausser le test suivant — trouvé en conditions réelles ici
        // même (TestCharLeopard2 du 2e test traînait encore et se faisait sélectionner par le 3e,
        // qui ne s'attendait à AUCUNE unité proche). Recréer la scène détruit proprement les
        // anciens GameObjects (OnDisable() les retire de AllLivingUnits), jamais de sauvegarde —
        // voir l'en-tête de fichier.
        RunIsolated("Tap visant un fantassin au sol, à côté d'un char : le fantassin doit gagner (régression 2026-09-12)",
            TestInfantryNextToTankWins, ref passed, ref failed);
        RunIsolated("Tap direct sur le char lui-même : le char doit toujours gagner (non-régression)",
            TestDirectTapOnTankStillWins, ref passed, ref failed);
        RunIsolated("Aucune unité près du tap : la sélection ne doit rien retourner",
            TestNoUnitNearTapReturnsNull, ref passed, ref failed);
        RunIsolated("FIN DE TOUR bloque (reste en Planification) si une unité n'a aucun ordre",
            TestEndTurnBlocksWithoutOrders, ref passed, ref failed);
        RunIsolated("FIN DE TOUR passe en Exécution une fois TOUTES les unités ordonnées",
            TestEndTurnProceedsWhenAllOrdered, ref passed, ref failed);
        RunIsolated("Barre d'escouade : le cycle passe par toutes les unités du groupe puis boucle",
            TestSquadBarCycleGoesThroughAllUnits, ref passed, ref failed);

        Debug.Log($"[TacticalSelectionAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new System.Exception($"[TacticalSelectionAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    private static void RunIsolated(string label, System.Func<bool> test, ref int passed, ref int failed)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Run(label, test, ref passed, ref failed);
    }

    private static void Run(string label, System.Func<bool> test, ref int passed, ref int failed)
    {
        bool ok;
        try { ok = test(); }
        catch (System.Exception e)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] EXCEPTION pendant '{label}' : {e}");
            ok = false;
        }
        if (ok) { passed++; Debug.Log($"[TacticalSelectionAutoTest] OK — {label}"); }
        else { failed++; Debug.LogError($"[TacticalSelectionAutoTest] ECHEC — {label}"); }
    }

    // ---- Fabrique d'unité réelle (colliders/anchor configurés par le vrai UnitAI.Start()) --------

    private static UnitAI MakeRealUnit(string name, Vector3 worldPos, Vector3 localScale, int team)
    {
        // Cube/Capsule = un vrai MeshRenderer avec de vrais bounds, exactement ce que UnitAI.Start()
        // interroge (GetComponentsInChildren<Renderer>()) pour dimensionner le collider — pas besoin
        // du vrai modèle 3D du jeu, juste d'un Renderer réel avec des bounds réels.
        GameObject go = GameObject.CreatePrimitive(worldPos.y > 0.9f ? PrimitiveType.Cube : PrimitiveType.Capsule);
        go.name = name;
        go.transform.position = worldPos;
        go.transform.localScale = localScale;

        UnitAI ai = go.AddComponent<UnitAI>(); // OnEnable() s'exécute ici, inscrit dans AllLivingUnits
        ai.teamID = team;
        ai.isPlayerControlled = true;
        ai.teamAssignedBySpawner = true; // saute la (re)détection par nom dans Start()
        ai.sourceUnitType = name;

        // Start() est privé et normalement appelé par Unity juste avant la première frame — jamais
        // hors Play Mode. On l'invoque nous-mêmes : c'est LUI qui configure le vrai BoxCollider/
        // CapsuleCollider recentré sur bounds.center (voir UnitAI.cs) que SelectionAnchorWorldPos lit.
        MethodInfo startMethod = typeof(UnitAI).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
        if (startMethod == null) throw new System.Exception("UnitAI.Start() introuvable par réflexion — a-t-elle été renommée ?");
        startMethod.Invoke(ai, null);

        return ai;
    }

    /// <summary>Caméra oblique (~65° d'inclinaison), même principe que la vraie vue "Commandement"
    /// (TacticalCamera/CameraStateManager, bridée à 75°) — c'est PRÉCISÉMENT cette obliquité qui fait
    /// diverger la position écran projetée du centre 3D d'un véhicule haut de sa position au sol.</summary>
    private static Camera MakeObliqueCommandCamera(Vector3 lookAtGroundPoint)
    {
        GameObject camGo = new GameObject("TestCommandCamera");
        Camera cam = camGo.AddComponent<Camera>();
        camGo.transform.position = lookAtGroundPoint + new Vector3(0f, 18f, -14f);
        camGo.transform.LookAt(lookAtGroundPoint, Vector3.up);
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 500f;
        cam.pixelRect = new Rect(0, 0, 1920, 1080); // résolution de référence, sans fenêtre réelle nécessaire
        return cam;
    }

    private static UnitAI RaycastForUnit(Camera cam, Vector2 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            UnitAI hitUnit = hit.collider.GetComponent<UnitAI>() ?? hit.collider.GetComponentInParent<UnitAI>();
            if (hitUnit != null && hitUnit.isPlayerControlled && !hitUnit.isDead) return hitUnit;
        }
        return null;
    }

    // ---- Tests --------------------------------------------------------------------------------

    private static bool TestInfantryNextToTankWins()
    {
        // Char large (2.2m x 1.9m x 4.5m, cohérent avec un CharLeopard réel) juste à côté d'un
        // fantassin posé au sol — assez proches pour que le rayon d'un tap visant le fantassin passe
        // dans le volume 3D du char si l'ancre de sélection n'est pas correctement ramenée au sol.
        UnitAI tank = MakeRealUnit("TestCharLeopard", new Vector3(0f, 0.95f, 0f), new Vector3(2.2f, 1.9f, 4.5f), 1);
        UnitAI infantry = MakeRealUnit("TestFantassin", new Vector3(2.0f, 0.9f, 0f), Vector3.one, 1);

        Camera cam = MakeObliqueCommandCamera(new Vector3(1f, 0f, 0f));

        // Le joueur vise le fantassin : le point tapé est SA position au sol projetée à l'écran.
        Vector2 tapScreenPos = cam.WorldToScreenPoint(infantry.transform.position);

        UnitAI raycastUnit = RaycastForUnit(cam, tapScreenPos);
        UnitAI result = TacticalPathManager.ResolveClosestPlayerUnit(tapScreenPos, cam, raycastUnit, 60f);

        if (result != infantry)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] Attendu Fantassin, obtenu {(result != null ? result.gameObject.name : "null")} " +
                            $"(raycastUnit brut = {(raycastUnit != null ? raycastUnit.gameObject.name : "null")}, tapScreenPos={tapScreenPos})");
            return false;
        }
        return true;
    }

    private static bool TestDirectTapOnTankStillWins()
    {
        UnitAI tank = MakeRealUnit("TestCharLeopard2", new Vector3(0f, 0.95f, 0f), new Vector3(2.2f, 1.9f, 4.5f), 1);
        UnitAI infantry = MakeRealUnit("TestFantassin2", new Vector3(6f, 0.9f, 0f), Vector3.one, 1); // loin, hors tolérance

        Camera cam = MakeObliqueCommandCamera(new Vector3(0f, 0f, 0f));

        // Vise le CENTRE VISUEL réel du char (bounds.center, pas transform.position) — c'est
        // précisément la position qu'un joueur tape visuellement pour désigner un véhicule.
        Vector3 tankVisualCenter = tank.GetComponent<Renderer>().bounds.center;
        Vector2 tapScreenPos = cam.WorldToScreenPoint(tankVisualCenter);

        UnitAI raycastUnit = RaycastForUnit(cam, tapScreenPos);
        UnitAI result = TacticalPathManager.ResolveClosestPlayerUnit(tapScreenPos, cam, raycastUnit, 60f);

        if (result != tank)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] Attendu CharLeopard, obtenu {(result != null ? result.gameObject.name : "null")} " +
                            $"(raycastUnit brut = {(raycastUnit != null ? raycastUnit.gameObject.name : "null")}, tapScreenPos={tapScreenPos})");
            return false;
        }
        return true;
    }

    private static bool TestNoUnitNearTapReturnsNull()
    {
        Vector3 lookAt = Vector3.zero;
        UnitAI farUnit = MakeRealUnit("TestFantassinLoin", new Vector3(50f, 0.9f, 50f), Vector3.one, 1);
        Camera cam = MakeObliqueCommandCamera(lookAt);

        // Ne PAS supposer que cam.pixelWidth/pixelHeight vaut le pixelRect qu'on vient de poser —
        // en mode -batchmode -nographics, sans vrai affichage, Unity peut résoudre une résolution
        // "écran" différente (trouvé en conditions réelles : ce test échouait à tort, l'unité
        // "lointaine" se faisant quand même sélectionner). Calcule plutôt la VRAIE position écran de
        // l'unité, puis vise un point délibérément à 500px de là (grande marge, très au-delà des 60px
        // de tolérance) — indépendant de toute hypothèse sur la résolution effective de la caméra.
        Vector3 unitScreenPos = cam.WorldToScreenPoint(farUnit.transform.position);
        Vector2 tapScreenPos = new Vector2(unitScreenPos.x + 500f, unitScreenPos.y + 500f);

        UnitAI raycastUnit = RaycastForUnit(cam, tapScreenPos);
        UnitAI result = TacticalPathManager.ResolveClosestPlayerUnit(tapScreenPos, cam, raycastUnit, 60f);

        if (result != null)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] Attendu null, obtenu {result.gameObject.name} " +
                            $"(unitScreenPos={unitScreenPos}, tapScreenPos={tapScreenPos})");
            return false;
        }
        return true;
    }

    // ---- Garde-fou FIN DE TOUR (2026-09-12) ----------------------------------------------------
    //
    // LancerExecutionTour() décide phaseActuelle SYNCHRONE MENT avant de démarrer sa coroutine
    // d'exécution (StartCoroutine) — on peut donc vérifier le résultat de la décision (bloqué ou
    // non) sans jamais laisser cette coroutine tourner. Une coroutine démarrée hors Play Mode ne
    // progresse de toute façon jamais (rien ne pompe MoveNext() sans une vraie boucle de jeu) : elle
    // reste inoffensivement suspendue à son premier "yield return new WaitForSeconds(...)" jusqu'à
    // ce que le processus batch se termine (-quit).

    private static TacticalPathManager MakeRealManager()
    {
        // Awake() suffit (s'exécute déjà à l'AddComponent, même hors Play Mode) : LancerExecutionTour
        // ne lit ni lineRenderer ni UnitSpawnerUI, les deux seules choses que configure Start(), donc
        // pas besoin de l'invoquer par réflexion ici. Volontaire : Start() instancie aussi
        // TacticalRadarUI.Instance, dont le singleton paresseux appelle DontDestroyOnLoad — INTERDIT
        // hors Play Mode ("The following game object is invoking the DontDestroyOnLoad method...
        // cannot be part of an editor script"), trouvé en conditions réelles en écrivant ce test.
        GameObject go = new GameObject("TestTacticalPathManager");
        TacticalPathManager mgr = go.AddComponent<TacticalPathManager>();
        return mgr;
    }

    private static bool TestEndTurnBlocksWithoutOrders()
    {
        TacticalPathManager mgr = MakeRealManager();
        MakeRealUnit("TestSansOrdre1", new Vector3(0f, 0.9f, 0f), Vector3.one, 1);
        MakeRealUnit("TestSansOrdre2", new Vector3(3f, 0.9f, 0f), Vector3.one, 1);
        // Aucune des deux n'a reçu le moindre ordre (tacticalPath vide par défaut, voir UnitAI.cs).

        mgr.phaseActuelle = TacticalPathManager.GamePhase.Planification;
        mgr.LancerExecutionTour();

        if (mgr.phaseActuelle != TacticalPathManager.GamePhase.Planification)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] Attendu phase=Planification (bloqué), obtenu {mgr.phaseActuelle}");
            return false;
        }
        return true;
    }

    private static bool TestEndTurnProceedsWhenAllOrdered()
    {
        TacticalPathManager mgr = MakeRealManager();
        UnitAI u1 = MakeRealUnit("TestAvecOrdre1", new Vector3(0f, 0.9f, 0f), Vector3.one, 1);
        UnitAI u2 = MakeRealUnit("TestAvecOrdre2", new Vector3(3f, 0.9f, 0f), Vector3.one, 1);
        u1.AddTacticalNode(new TacticalPathManager.TacticalNode { position = u1.transform.position, action = TacticalPathManager.NodeAction.Guetter });
        u2.AddTacticalNode(new TacticalPathManager.TacticalNode { position = u2.transform.position, action = TacticalPathManager.NodeAction.Guetter });

        mgr.phaseActuelle = TacticalPathManager.GamePhase.Planification;
        try
        {
            mgr.LancerExecutionTour();
        }
        catch (System.Exception e)
        {
            // La transition de phase qui nous intéresse ici est déjà SYNCHRONE, avant que
            // LancerExecutionTour ne lance la coroutine de mouvement réel (ExecuterOrdres ->
            // NavMeshAgent) — sans NavMesh bakée dans cette scène de test minimale, cette partie
            // PEUT légitimement échouer ; on l'ignore, seule la décision de phase compte ici.
            Debug.Log($"[TacticalSelectionAutoTest] (ignoré, hors du périmètre de ce test) exception après la transition de phase : {e.Message}");
        }

        if (mgr.phaseActuelle != TacticalPathManager.GamePhase.Execution)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] Attendu phase=Execution (toutes les unités ordonnées), obtenu {mgr.phaseActuelle}");
            return false;
        }
        return true;
    }

    // ---- Barre d'escouade (2026-09-12) -----------------------------------------------------
    //
    // TacticalPathManager_UI.CycleSelectGroup : la façon FIABLE d'enchaîner la sélection de
    // plusieurs unités (clic sur l'icône de type dans le coin haut-droit, cycle + recentre la
    // caméra), totalement indépendante du tap 3D et de ses soucis d'occlusion/angle de caméra —
    // jamais vérifiée par un test avant aujourd'hui malgré son rôle central dans "la gestion des
    // unités". Privée, mais un simple List<UnitAI> en paramètre — invoquée par réflexion, sans
    // avoir besoin du binding UI Toolkit (elle ne touche jamais squadBarEl elle-même).

    private static bool TestSquadBarCycleGoesThroughAllUnits()
    {
        TacticalPathManager mgr = MakeRealManager();
        // SelectionnerUnite (appelée par CycleSelectGroup) lit Camera.main pour son son de
        // confirmation — absent des autres tests de ce fichier, qui n'appellent jamais
        // SelectionnerUnite directement (seulement ResolveClosestPlayerUnit, une méthode statique
        // pure). Trouvé en conditions réelles ici même (NullReferenceException sur Camera.main).
        GameObject camGo = new GameObject("TestCameraForSquadBar");
        camGo.AddComponent<Camera>().tag = "MainCamera";

        UnitAI u1 = MakeRealUnit("TestSquadA", new Vector3(0f, 0.9f, 0f), Vector3.one, 1);
        UnitAI u2 = MakeRealUnit("TestSquadB", new Vector3(5f, 0.9f, 0f), Vector3.one, 1);
        UnitAI u3 = MakeRealUnit("TestSquadC", new Vector3(10f, 0.9f, 0f), Vector3.one, 1);
        var group = new System.Collections.Generic.List<UnitAI> { u1, u2, u3 };

        MethodInfo cycleMethod = typeof(TacticalPathManager).GetMethod("CycleSelectGroup", BindingFlags.NonPublic | BindingFlags.Instance);
        if (cycleMethod == null)
        {
            var candidates = typeof(TacticalPathManager).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name.IndexOf("Cycle", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name);
            throw new System.Exception("TacticalPathManager.CycleSelectGroup introuvable par réflexion. Candidats contenant 'Cycle' : [" + string.Join(", ", candidates) + "]");
        }

        FieldInfo selectedField = typeof(TacticalPathManager).GetField("uniteSelectionnee");
        if (selectedField == null) throw new System.Exception("TacticalPathManager.uniteSelectionnee introuvable.");

        // Rien de sélectionné au départ -> le 1er cycle doit prendre la PREMIÈRE unité du groupe
        // (voir CycleSelectGroup : currentIndex reste -1 si uniteSelectionnee ne correspond à rien
        // du groupe, (currentIndex + 1) % count retombe donc sur l'index 0).
        cycleMethod.Invoke(mgr, new object[] { group });
        var selected1 = (GameObject)selectedField.GetValue(mgr);
        if (selected1 != u1.gameObject)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] 1er cycle : attendu {u1.gameObject.name}, obtenu {(selected1 != null ? selected1.name : "null")}");
            return false;
        }

        cycleMethod.Invoke(mgr, new object[] { group });
        var selected2 = (GameObject)selectedField.GetValue(mgr);
        if (selected2 != u2.gameObject)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] 2e cycle : attendu {u2.gameObject.name}, obtenu {(selected2 != null ? selected2.name : "null")}");
            return false;
        }

        cycleMethod.Invoke(mgr, new object[] { group });
        var selected3 = (GameObject)selectedField.GetValue(mgr);
        if (selected3 != u3.gameObject)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] 3e cycle : attendu {u3.gameObject.name}, obtenu {(selected3 != null ? selected3.name : "null")}");
            return false;
        }

        // 4e cycle : doit boucler et reprendre la 1ère unité.
        cycleMethod.Invoke(mgr, new object[] { group });
        var selected4 = (GameObject)selectedField.GetValue(mgr);
        if (selected4 != u1.gameObject)
        {
            Debug.LogError($"[TacticalSelectionAutoTest] 4e cycle (bouclage attendu) : attendu {u1.gameObject.name}, obtenu {(selected4 != null ? selected4.name : "null")}");
            return false;
        }
        return true;
    }
}
