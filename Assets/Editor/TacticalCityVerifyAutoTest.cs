using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Test Éditeur AUTOMATISÉ de l'"équité géométrique, 2ème étage" (2026-09-12) — voir
/// MatchState.AuthoritativeCityHash, NetMessage.city_verify/city_verify_result et
/// CityGenerator.ApplyAuthoritativeBuildings. Même esprit/mêmes techniques que
/// TacticalSelectionAutoTest.cs (voir son en-tête pour la ligne de commande EXACTE et le piège
/// EditorUserBuildSettings) : de VRAIS objets (CityGenerator, BuildingStructure), aucune simulation
/// séparée.
///
/// Couvre les deux briques neuves :
/// 1. TacticalGridBuilder.ComputeBuildingListHash — déterministe, insensible à l'ordre de la liste,
///    sensible à un vrai écart de géométrie.
/// 2. CityGenerator.CreateBuildingObjectFromAuthoritative (invoquée par réflexion : privée, et le
///    chemin public ApplyAuthoritativeBuildings passe par une coroutine qui ne tourne pas toute
///    seule hors Play Mode — voir la note dans RunAll ci-dessous) — reconstruit un bâtiment
///    directement depuis une structure déjà résolue (sans JSON Overpass ni algorithme de
///    subdivision/portes-fenêtres aléatoire) : vérifie que l'empreinte, la hauteur, le nombre de
///    portes/fenêtres ET l'edgeIndex de chaque porte (dérivé géométriquement, jamais transmis sur
///    le réseau) sont corrects.
/// </summary>
public static class TacticalCityVerifyAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== TacticalCityVerifyAutoTest : équité géométrique, 2ème étage ===");

        Run("ComputeBuildingListHash : même liste -> toujours le même hash", TestHashIsDeterministic, ref passed, ref failed);
        Run("ComputeBuildingListHash : insensible à l'ordre des bâtiments dans la liste", TestHashOrderInsensitive, ref passed, ref failed);
        Run("ComputeBuildingListHash : une empreinte différente change le hash", TestHashDetectsFootprintDifference, ref passed, ref failed);
        Run("ComputeBuildingListHash : un bâtiment en moins change le hash", TestHashDetectsCountDifference, ref passed, ref failed);
        Run("CreateBuildingObjectFromAuthoritative : reconstruit empreinte/hauteur/portes/fenêtres à l'identique", TestCreateBuildingObjectFromAuthoritative, ref passed, ref failed);

        Debug.Log($"[TacticalCityVerifyAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new System.Exception($"[TacticalCityVerifyAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    private static void Run(string label, System.Func<bool> test, ref int passed, ref int failed)
    {
        bool ok;
        try { ok = test(); }
        catch (System.Exception e)
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] EXCEPTION pendant '{label}' : {e}");
            ok = false;
        }
        if (ok) { passed++; Debug.Log($"[TacticalCityVerifyAutoTest] OK — {label}"); }
        else { failed++; Debug.LogError($"[TacticalCityVerifyAutoTest] ECHEC — {label}"); }
    }

    private static TacticalBuilding MakeBuilding(int id, Vector2 offset)
    {
        return new TacticalBuilding
        {
            id = id,
            height = 6f,
            footprint = new List<Vector2> {
                offset + new Vector2(-5, -5), offset + new Vector2(5, -5),
                offset + new Vector2(5, 5), offset + new Vector2(-5, 5)
            },
            doors = new List<TacticalDoor> {
                new TacticalDoor { position = offset + new Vector2(0, -5.05f), entryDirection = new Vector2(0, -1), width = 1.4f }
            },
            windows = new List<TacticalWindow> {
                new TacticalWindow { id = 1, position = offset + new Vector2(3, -5.05f), outwardNormal = new Vector2(0, -1), floorLevel = 0 }
            }
        };
    }

    private static bool TestHashIsDeterministic()
    {
        var buildings = new List<TacticalBuilding> { MakeBuilding(0, Vector2.zero), MakeBuilding(1, new Vector2(20, 0)) };
        int h1 = TacticalGridBuilder.ComputeBuildingListHash(buildings);
        int h2 = TacticalGridBuilder.ComputeBuildingListHash(buildings);
        return h1 == h2;
    }

    private static bool TestHashOrderInsensitive()
    {
        var forward = new List<TacticalBuilding> { MakeBuilding(0, Vector2.zero), MakeBuilding(1, new Vector2(20, 0)) };
        var reversed = new List<TacticalBuilding> { MakeBuilding(1, new Vector2(20, 0)), MakeBuilding(0, Vector2.zero) };
        return TacticalGridBuilder.ComputeBuildingListHash(forward) == TacticalGridBuilder.ComputeBuildingListHash(reversed);
    }

    private static bool TestHashDetectsFootprintDifference()
    {
        var baseline = new List<TacticalBuilding> { MakeBuilding(0, Vector2.zero) };
        var moved = new List<TacticalBuilding> { MakeBuilding(0, new Vector2(0.5f, 0f)) }; // décalé de 50cm, bien au-dessus du mm de quantification
        return TacticalGridBuilder.ComputeBuildingListHash(baseline) != TacticalGridBuilder.ComputeBuildingListHash(moved);
    }

    private static bool TestHashDetectsCountDifference()
    {
        var full = new List<TacticalBuilding> { MakeBuilding(0, Vector2.zero), MakeBuilding(1, new Vector2(20, 0)) };
        var missing = new List<TacticalBuilding> { MakeBuilding(0, Vector2.zero) };
        return TacticalGridBuilder.ComputeBuildingListHash(full) != TacticalGridBuilder.ComputeBuildingListHash(missing);
    }

    private static bool TestCreateBuildingObjectFromAuthoritative()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);

        GameObject cityGenGo = new GameObject("TestCityGenerator");
        CityGenerator cityGen = cityGenGo.AddComponent<CityGenerator>();

        TacticalBuilding template = MakeBuilding(0, Vector2.zero);
        // Copie défensive AVANT l'appel : certaines des fonctions de maillage réutilisées telles
        // quelles depuis CreateBuildingObject (Triangulate/CreateWallsMesh/...) mutent leur liste de
        // sommets EN PLACE (même comportement, déjà présent, dans le pipeline normal — aucune
        // régression introduite ici) — comparer après coup contre template.footprint lirait la
        // liste déjà réordonnée, pas ce qui a réellement été transmis à la reconstruction.
        var expectedFootprint = new List<Vector2>(template.footprint);

        MethodInfo method = typeof(CityGenerator).GetMethod("CreateBuildingObjectFromAuthoritative", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new System.Exception("CityGenerator.CreateBuildingObjectFromAuthoritative introuvable par réflexion.");

        GameObject parentGo = new GameObject("TestCityRoot");
        object result = method.Invoke(cityGen, new object[] { template, parentGo.transform });
        if (!(bool)result)
        {
            Debug.LogError("[TacticalCityVerifyAutoTest] CreateBuildingObjectFromAuthoritative a renvoyé false (échec de création).");
            return false;
        }

        // OnEnable() (qui inscrit dans BuildingStructure.AllBuildings) ne s'exécute PAS
        // automatiquement en mode batch Éditeur hors Play Mode (contrairement à Play Mode/un vrai
        // build, où AddComponent le déclenche toujours de façon synchrone) — même piège que Start()
        // ailleurs dans ces suites de tests (voir TacticalSelectionAutoTest.MakeRealUnit), sauf qu'ici
        // c'est OnEnable, pas Start, qui est concerné pour BuildingStructure. Invoqué ici par
        // réflexion pour que CE TEST reflète la réalité du jeu — le code de PRODUCTION
        // (CreateBuildingObjectFromAuthoritative) n'a besoin d'aucun contournement : en Play Mode
        // réel, Unity appelle déjà OnEnable tout seul.
        BuildingStructure created = parentGo.GetComponentInChildren<BuildingStructure>();
        if (created == null)
        {
            Debug.LogError("[TacticalCityVerifyAutoTest] Aucun BuildingStructure trouvé sous TestCityRoot après CreateBuildingObjectFromAuthoritative.");
            return false;
        }
        MethodInfo onEnable = typeof(BuildingStructure).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);
        if (onEnable == null) throw new System.Exception("BuildingStructure.OnEnable introuvable par réflexion.");
        onEnable.Invoke(created, null);

        if (BuildingStructure.AllBuildings.Count != 1)
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] BuildingStructure.AllBuildings.Count attendu à 1, obtenu {BuildingStructure.AllBuildings.Count}.");
            return false;
        }

        if (created.polygonFootprint.Count != expectedFootprint.Count)
        {
            Debug.LogError("[TacticalCityVerifyAutoTest] Empreinte reconstruite de taille différente.");
            return false;
        }
        for (int i = 0; i < expectedFootprint.Count; i++)
        {
            if (Vector2.Distance(created.polygonFootprint[i], expectedFootprint[i]) > 0.01f)
            {
                Debug.LogError($"[TacticalCityVerifyAutoTest] Sommet {i} de l'empreinte différent : attendu {expectedFootprint[i]}, obtenu {created.polygonFootprint[i]}.");
                return false;
            }
        }

        if (!Mathf.Approximately(created.height, template.height))
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] Hauteur reconstruite différente : attendu {template.height}, obtenu {created.height}.");
            return false;
        }

        if (created.doors.Count != 1)
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] Nombre de portes attendu à 1, obtenu {created.doors.Count}.");
            return false;
        }
        // La porte de test est posée sur l'arête (-5,-5)->(5,-5), soit l'arête d'INDEX 0 du polygone
        // construit ci-dessus (footprint[0]=(-5,-5), footprint[1]=(5,-5)) — dérivé géométriquement
        // par ResolveEdgeIndexAndDistance, jamais transmis sur le réseau.
        if (created.doors[0].edgeIndex != 0)
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] edgeIndex de la porte attendu à 0 (arête sud), obtenu {created.doors[0].edgeIndex}.");
            return false;
        }

        if (created.windows.Count != 1)
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] Nombre de fenêtres attendu à 1, obtenu {created.windows.Count}.");
            return false;
        }
        // Même formule que GenerateDoorsAndWindows : floorY = 1.4 + floorLevel * 3.0, ici floorLevel=0 -> 1.4.
        if (!Mathf.Approximately(created.windows[0].position.y, 1.4f))
        {
            Debug.LogError($"[TacticalCityVerifyAutoTest] Hauteur Y de la fenêtre attendue à 1.4 (floorLevel=0), obtenue {created.windows[0].position.y}.");
            return false;
        }

        return true;
    }
}
