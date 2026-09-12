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
/// Usage : "Unity.exe -batchmode -nographics -quit -projectPath ... -executeMethod
/// TacticalSelectionAutoTest.RunAll -logFile chemin.log" — cherche "[TacticalSelectionAutoTest]"
/// dans le log. Toute assertion ratée lève une exception, que Unity remonte en code de sortie non
/// nul en mode batch (mêmes conventions que ServerBuildScript/AndroidTestBuildScript).
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
}
