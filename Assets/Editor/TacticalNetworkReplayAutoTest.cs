using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Test Éditeur AUTOMATISÉ du rejeu réseau cosmétique (2026-09-12) — même esprit et mêmes
/// techniques que TacticalSelectionAutoTest.cs (voir son en-tête), mais pour
/// DestructibleEnvironment.ApplyNetworkDestruction (destruction de bâtiment enfin transmise au
/// client, voir §19.9.3 de 08-known-issues-and-todo.md) plutôt que la sélection.
///
/// Usage : voir l'en-tête de TacticalSelectionAutoTest.cs pour la ligne de commande EXACTE à
/// utiliser — TOUJOURS avec "-buildTarget StandaloneWindows64 -standaloneBuildSubtarget Player"
/// explicites, jamais omis : un précédent build serveur (ServerBuildScript) laisse
/// EditorUserBuildSettings sur "Server" pour toute invocation batch suivante, ce qui masque tout le
/// code client-only sans le moindre message d'erreur clair (piège trouvé en écrivant ces tests).
///
/// Ce que ce test garantit, et qui aurait été facile à casser sans lui : ApplyNetworkDestruction NE
/// DOIT JAMAIS (1) infliger de dégâts aux unités proches — le serveur autoritaire a déjà décidé qui
/// meurt via les événements Death du même snapshot — ni (2) retirer le bâtiment de
/// BuildingStructure.AllBuildings, puisque TacticalGridBuilder identifie chaque bâtiment par son
/// INDEX dans cette liste, figé pour toute la partie côté moteur pur (Deathmatch/Zone de Contrôle) :
/// le retirer décalerait l'identité de tous les bâtiments suivants pour le reste de la partie.
/// </summary>
public static class TacticalNetworkReplayAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== TacticalNetworkReplayAutoTest : destruction de bâtiment côté client (rejeu réseau) ===");

        RunIsolated("ApplyNetworkDestruction détruit visuellement SANS toucher aux PV d'une unité voisine",
            TestNetworkDestructionDoesNotDamageNearbyUnit, ref passed, ref failed);
        RunIsolated("ApplyNetworkDestruction ne retire PAS le bâtiment de BuildingStructure.AllBuildings",
            TestNetworkDestructionKeepsBuildingIndexStable, ref passed, ref failed);
        RunIsolated("ApplyNetworkDestruction est idempotente (un second appel ne fait rien de plus)",
            TestNetworkDestructionIsIdempotent, ref passed, ref failed);
        RunIsolated("PlayNetworkShotEffects déclenche l'alerte radar (retour 2026-09-12, radar muet en multijoueur)",
            TestPlayNetworkShotEffectsPingsRadar, ref passed, ref failed);

        Debug.Log($"[TacticalNetworkReplayAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new System.Exception($"[TacticalNetworkReplayAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
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
            Debug.LogError($"[TacticalNetworkReplayAutoTest] EXCEPTION pendant '{label}' : {e}");
            ok = false;
        }
        if (ok) { passed++; Debug.Log($"[TacticalNetworkReplayAutoTest] OK — {label}"); }
        else { failed++; Debug.LogError($"[TacticalNetworkReplayAutoTest] ECHEC — {label}"); }
    }

    /// <summary>Bâtiment réel minimal (BuildingStructure + DestructibleEnvironment, colliders réels
    /// pour que le calcul d'empreinte locale de DestroyEnvironmentInternal ait quelque chose à
    /// mesurer) — un simple Cube fait l'affaire, comme pour les unités de test dans
    /// TacticalSelectionAutoTest.MakeRealUnit.</summary>
    private static DestructibleEnvironment MakeRealBuilding(Vector3 worldPos)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "TestBuilding";
        go.transform.position = worldPos;
        go.transform.localScale = new Vector3(10f, 6f, 10f);

        BuildingStructure bs = go.AddComponent<BuildingStructure>(); // OnEnable() inscrit dans AllBuildings
        bs.polygonFootprint = new System.Collections.Generic.List<Vector2>
        {
            new Vector2(worldPos.x - 5f, worldPos.z - 5f), new Vector2(worldPos.x + 5f, worldPos.z - 5f),
            new Vector2(worldPos.x + 5f, worldPos.z + 5f), new Vector2(worldPos.x - 5f, worldPos.z + 5f),
        };

        DestructibleEnvironment env = go.AddComponent<DestructibleEnvironment>();
        env.maxHealth = 300f;
        env.health = 300f;

        // Start() est privé, jamais appelé hors Play Mode — invoqué par réflexion comme pour UnitAI
        // (voir TacticalSelectionAutoTest.MakeRealUnit) : il ne fait ici que lire GetComponent<...>(),
        // aucun risque de DontDestroyOnLoad contrairement à TacticalPathManager.Start().
        MethodInfo startMethod = typeof(DestructibleEnvironment).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
        if (startMethod == null) throw new System.Exception("DestructibleEnvironment.Start() introuvable par réflexion.");
        startMethod.Invoke(env, null);

        return env;
    }

    private static UnitAI MakeUnitInsideFootprint(Vector3 worldPos)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "TestOccupant";
        go.transform.position = worldPos;
        UnitAI ai = go.AddComponent<UnitAI>();
        ai.teamID = 1;
        ai.isPlayerControlled = true;
        ai.teamAssignedBySpawner = true;
        MethodInfo startMethod = typeof(UnitAI).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
        startMethod.Invoke(ai, null);
        return ai;
    }

    private static bool TestNetworkDestructionDoesNotDamageNearbyUnit()
    {
        DestructibleEnvironment env = MakeRealBuilding(Vector3.zero);
        UnitAI occupant = MakeUnitInsideFootprint(new Vector3(1f, 0.9f, 1f)); // dans l'empreinte 10x10
        int healthBefore = occupant.health;

        env.ApplyNetworkDestruction();

        if (!env.isDestroyed)
        {
            Debug.LogError("[TacticalNetworkReplayAutoTest] isDestroyed attendu à true après ApplyNetworkDestruction.");
            return false;
        }
        if (occupant.health != healthBefore)
        {
            Debug.LogError($"[TacticalNetworkReplayAutoTest] PV de l'occupant modifiés par ApplyNetworkDestruction " +
                            $"(avant={healthBefore}, après={occupant.health}) — devrait être purement cosmétique.");
            return false;
        }
        return true;
    }

    private static bool TestNetworkDestructionKeepsBuildingIndexStable()
    {
        DestructibleEnvironment envA = MakeRealBuilding(new Vector3(-20f, 0f, 0f));
        DestructibleEnvironment envB = MakeRealBuilding(new Vector3(20f, 0f, 0f));
        int countBefore = BuildingStructure.AllBuildings.Count;
        int indexOfB = BuildingStructure.AllBuildings.IndexOf(envB.GetComponent<BuildingStructure>());

        envA.ApplyNetworkDestruction();

        if (BuildingStructure.AllBuildings.Count != countBefore)
        {
            Debug.LogError($"[TacticalNetworkReplayAutoTest] AllBuildings.Count a changé " +
                            $"(avant={countBefore}, après={BuildingStructure.AllBuildings.Count}) — un buildingId envoyé " +
                            $"par le serveur pour un AUTRE bâtiment désignerait maintenant le mauvais bâtiment.");
            return false;
        }
        int indexOfBAfter = BuildingStructure.AllBuildings.IndexOf(envB.GetComponent<BuildingStructure>());
        if (indexOfBAfter != indexOfB)
        {
            Debug.LogError($"[TacticalNetworkReplayAutoTest] L'index du bâtiment B a changé (avant={indexOfB}, après={indexOfBAfter}).");
            return false;
        }
        return true;
    }

    private static bool TestNetworkDestructionIsIdempotent()
    {
        DestructibleEnvironment env = MakeRealBuilding(Vector3.zero);
        env.ApplyNetworkDestruction();
        // Un deuxième appel (ex: le même buildingId répété sur un snapshot suivant, voir le
        // commentaire "delta, pas cumulatif" dans MatchSessionManager_CombatPure.cs) ne doit lever
        // aucune exception ni rien changer d'autre — juste ressortir immédiatement (isDestroyed déjà
        // vrai), voir la garde en tête de ApplyNetworkDestruction.
        env.ApplyNetworkDestruction();
        return env.isDestroyed;
    }

    /// <summary>Retour de jeu 2026-09-12 (« il faut que le joueur voie les unités ennemies pendant un
    /// combat, et qu'elles soient aussi visibles sur le radar, pas seulement en mode multijoueur ») :
    /// le vrai ShootAt (solo/Conquête « vivante ») appelle FogOfWarEntity.NotifyAttack(), qui
    /// déclenche l'onde rouge de TacticalRadarUI — seule alerte indiquant OÙ un combat a lieu sans que
    /// le joueur ait déjà l'œil dessus. PlayNetworkShotEffects (rejeu cosmétique du multijoueur)
    /// reproduisait tous les autres effets (son, traceur, flash) mais oubliait CELUI-CI : le radar
    /// restait muet pendant un échange de tirs multijoueur. Vérifie ici que l'appel a bien été ajouté,
    /// via le compte de <c>TacticalRadarUI.activePings</c> (privé, lu par réflexion) qui doit augmenter
    /// de 1 après un seul appel à PlayNetworkShotEffects sur un tireur non-mortier.</summary>
    private static bool TestPlayNetworkShotEffectsPingsRadar()
    {
        // TacticalRadarUI.Instance créerait normalement l'instance via DontDestroyOnLoad si aucune
        // n'existe déjà — interdit hors Play Mode (voir l'en-tête de TacticalSelectionAutoTest.cs,
        // même piège que TacticalPathManager.Start()). On crée donc l'instance nous-mêmes AVANT tout
        // appel à .Instance, exactement comme MakeRealManager() évite Start() pour la même raison —
        // ici Awake() (qui s'exécute, lui, automatiquement via AddComponent même hors Play Mode) suffit
        // à poser _instance, donc le prochain accès à .Instance le trouvera déjà et ne recréera rien.
        GameObject radarGo = new GameObject("TestTacticalRadarUI");
        TacticalRadarUI radar = radarGo.AddComponent<TacticalRadarUI>();

        FieldInfo pingsField = typeof(TacticalRadarUI).GetField("activePings", BindingFlags.NonPublic | BindingFlags.Instance);
        if (pingsField == null) throw new System.Exception("TacticalRadarUI.activePings introuvable par réflexion.");
        var pingsBefore = (System.Collections.IList)pingsField.GetValue(radar);
        int countBefore = pingsBefore.Count;

        UnitAI shooter = MakeUnitInsideFootprint(new Vector3(0f, 0.9f, 0f)); // fantassin ordinaire, pas un mortier
        shooter.PlayNetworkShotEffects(new Vector3(10f, 0.9f, 0f));

        var pingsAfter = (System.Collections.IList)pingsField.GetValue(radar);
        if (pingsAfter.Count != countBefore + 1)
        {
            Debug.LogError($"[TacticalNetworkReplayAutoTest] activePings attendu à {countBefore + 1}, obtenu {pingsAfter.Count} — " +
                            "PlayNetworkShotEffects n'a pas déclenché l'alerte radar.");
            return false;
        }
        return true;
    }
}
