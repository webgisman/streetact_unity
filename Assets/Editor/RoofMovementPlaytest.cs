#if !UNITY_SERVER
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using NodeAction = TacticalPathManager.NodeAction;

/// <summary>
/// Essai automatique des déplacements d'infanterie sur les toits (2026-10-03, retour joueur : « sur
/// le toit, je lui demande d'aller plus loin dans la rue, il marche un peu sur le toit et s'arrête »),
/// avec le VRAI moteur — même code que le serveur de jeu (UnitAI.ExecuterOrdres, NavMesh, physique)
/// — sur le vrai quartier de test (66648,44110), sans serveur ni compte :
///   1. monter sur un toit ;
///   2. un tap de rue « accroché » à un décor reste un point de SOL (planification) ;
///   3. toit -> rue lointaine : descente par un bord sur rue, arrivée au point, sans téléportation ;
///   4. toit -> rue avec GUETTER : la posture est bien prise une fois en bas ;
///   5. toit -> toit d'un autre immeuble : l'unité y arrive.
///
///   Unity.exe -batchmode -projectPath "..." -executeMethod RoofMovementPlaytest.Run -logFile roof.log
/// Code de sortie 0 = tout est vert. Résultats : lignes "[RoofPlaytest]" du journal.
/// </summary>
[InitializeOnLoad]
public static class RoofMovementPlaytest
{
    private const string ActivePrefKey = "Novgov.RoofPlaytest.Active";
    private const int TileX = 66648, TileY = 44110;

    static RoofMovementPlaytest()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void Run()
    {
        EditorPrefs.SetBool(ActivePrefKey, true);
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !EditorPrefs.GetBool(ActivePrefKey, false)) return;
        EditorPrefs.SetBool(ActivePrefKey, false);
        new GameObject("RoofMovementPlaytestRunner").AddComponent<Runner>();
    }

    private class Runner : MonoBehaviour
    {
        private int passed, failed;
        private float maxFrameJump;
        private UnitAI watched;
        private Vector3 lastWatchedPos;

        private void Check(bool ok, string label)
        {
            if (ok) { passed++; Debug.Log($"[RoofPlaytest] OK — {label}"); }
            else { failed++; Debug.LogError($"[RoofPlaytest] ECHEC — {label}"); }
        }

        private void Update()
        {
            if (watched == null) return;
            float jump = Vector3.Distance(watched.transform.position, lastWatchedPos);
            if (jump > maxFrameJump) maxFrameJump = jump;
            lastWatchedPos = watched.transform.position;
        }

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private static float RoofSurfaceY(BuildingStructure b, Vector3 xz)
        {
            float top = b.height + 6f;
            if (Physics.Raycast(new Vector3(xz.x, top, xz.z), Vector3.down, out RaycastHit hit, top + 1f, UnitAI.WorldGeometryMask, QueryTriggerInteraction.Ignore))
                return hit.point.y + 0.05f;
            return b.height + 0.05f;
        }

        /// <summary>Point de rue (NavMesh, hors de tout bâtiment) en partant du centre de
        /// <paramref name="b"/> dans la direction <paramref name="angleDeg"/>, à au moins
        /// <paramref name="minDistance"/> m du bord.</summary>
        private static Vector3? StreetPointFrom(BuildingStructure b, float angleDeg, float minDistance)
        {
            Vector3 dir = Quaternion.Euler(0f, angleDeg, 0f) * Vector3.forward;
            Vector3 c = new Vector3(b.centroid.x, 0.05f, b.centroid.z);
            float leftAt = -1f;
            for (float d = 1f; d < 120f; d += 0.5f)
            {
                Vector3 p = c + dir * d;
                if (leftAt < 0f) { if (!b.ContainsPoint2D(p)) leftAt = d; continue; }
                if (d - leftAt < minDistance) continue;
                if (BuildingStructure.FindBuildingAt(p) != null) continue;
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 0.8f, NavMesh.AllAreas) && hit.position.y < 1f)
                    return hit.position;
            }
            return null;
        }

        private IEnumerator RunOrder(UnitAI unit, Vector3 position, NodeAction action, float timeout = 90f)
        {
            unit.ClearTacticalPath();
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode { position = position, action = action });
            watched = unit;
            lastWatchedPos = unit.transform.position;
            maxFrameJump = 0f;
            unit.ExecuterOrdres();
            yield return null;
            float t = 0f;
            while (unit.IsMovingOrActing() && t < timeout) { t += Time.deltaTime; yield return null; }
            watched = null;
            if (t >= timeout) Debug.LogError($"[RoofPlaytest] ordre {action} toujours en cours après {timeout}s");
        }

        private IEnumerator ClimbOnto(UnitAI unit, BuildingStructure b)
        {
            Vector3 roof = new Vector3(b.centroid.x, 0f, b.centroid.z);
            roof.y = RoofSurfaceY(b, roof);
            yield return RunOrder(unit, roof, NodeAction.Escalade);
        }

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3f);
            Novgov.Generation.ZoneManager.EnsureInstance().LoadZone(TileX, TileY);
            var city = Object.FindFirstObjectByType<CityGenerator>();
            float wait = 0f;
            while ((city == null || !city.IsCityReady || BuildingStructure.AllBuildings.Count < 50) && wait < 120f)
            {
                wait += Time.deltaTime;
                if (city == null) city = Object.FindFirstObjectByType<CityGenerator>();
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            Debug.Log($"[RoofPlaytest] Quartier ({TileX},{TileY}) prêt : {BuildingStructure.AllBuildings.Count} bâtiments.");

            // Immeuble de départ A (assez haut, près du centre) et sa rue ; immeuble B séparé par une rue.
            BuildingStructure a = null, b = null;
            Vector3? start = null, farStreet = null;
            foreach (var cand in BuildingStructure.AllBuildings)
            {
                if (cand == null || cand.height < 4.5f || cand.centroid.magnitude > 70f) continue;
                Vector3? s = StreetPointFrom(cand, 0f, 1.5f) ?? StreetPointFrom(cand, 90f, 1.5f) ?? StreetPointFrom(cand, 180f, 1.5f) ?? StreetPointFrom(cand, 270f, 1.5f);
                if (s == null) continue;
                Vector3? far = null;
                for (float ang = 0f; ang < 360f && far == null; ang += 45f)
                {
                    Vector3? f = StreetPointFrom(cand, ang, 25f);
                    var path = new NavMeshPath();
                    if (f != null && NavMesh.CalculatePath(s.Value, f.Value, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete) far = f;
                }
                if (far == null) continue;
                a = cand; start = s; farStreet = far;
                break;
            }
            if (a == null) { Check(false, "aucun immeuble de test trouvé"); Finish(); yield break; }

            MethodInfo continuous = typeof(UnitAI).GetMethod("IsRoofWalkContinuous", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var cand in BuildingStructure.AllBuildings)
            {
                if (cand == null || cand == a || cand.height < 4.5f) continue;
                float d = Flat(cand.centroid, a.centroid);
                if (d < 15f || d > 45f) continue;
                Vector3 from = new Vector3(a.centroid.x, RoofSurfaceY(a, a.centroid), a.centroid.z);
                Vector3 to = new Vector3(cand.centroid.x, RoofSurfaceY(cand, cand.centroid), cand.centroid.z);
                if (continuous != null && (bool)continuous.Invoke(null, new object[] { from, to })) continue;
                b = cand;
                break;
            }
            Debug.Log($"[RoofPlaytest] A={a.name} (h={a.height:F1}) départ={start.Value:F1} rue lointaine={farStreet.Value:F1} B={(b != null ? b.name : "aucun")}");

            UnitAI unit = UnitSpawnerUI.Instance.SpawnUnitAt(UnitSpawnerUI.UnitType.Fantassin, start.Value, 1, "Fantassin_Test", skipSafeSpawnAdjustment: true);
            yield return new WaitForSeconds(1.5f);

            // 1. Monter sur le toit de A.
            yield return ClimbOnto(unit, a);
            Check(unit.isRooftopSniper && unit.transform.position.y > UnitAI.RoofStrataThresholdY && BuildingStructure.FindBuildingAt(unit.transform.position) == a,
                $"1. escalade : sur le toit de A (y={unit.transform.position.y:F2})");

            // 2. Planification : un tap de rue qui accroche un arbre (y=3) près de A reste un point de sol.
            MethodInfo project = typeof(TacticalPathManager).GetMethod("ProjectOnRoofSurface", BindingFlags.NonPublic | BindingFlags.Static);
            Vector3 treeHit = farStreet.Value + Vector3.up * 3f;
            Vector3 planned = (Vector3)project.Invoke(null, new object[] { treeHit, a });
            Check(planned.y <= UnitAI.RoofStrataThresholdY, $"2. tap de rue accroché à un décor -> point de sol (y={planned.y:F2})");
            Vector3 roofTap = new Vector3(a.centroid.x, 1f, a.centroid.z);
            Vector3 plannedRoof = (Vector3)project.Invoke(null, new object[] { roofTap, a });
            Check(plannedRoof.y > UnitAI.RoofStrataThresholdY, $"2b. tap sur son propre toit -> point de toit (y={plannedRoof.y:F2})");

            // 3. Toit -> rue lointaine (DESCENDRE ET Y ALLER).
            yield return RunOrder(unit, farStreet.Value, NodeAction.Descendre);
            Vector3 p = unit.transform.position;
            Check(Flat(p, farStreet.Value) < 2.5f && p.y < 1f && !unit.isRooftopSniper,
                $"3. toit -> rue à {Flat(start.Value, farStreet.Value):F0} m : arrivé à {Flat(p, farStreet.Value):F1} m de la cible, y={p.y:F2}");
            Check(maxFrameJump < 2.5f, $"3b. aucune téléportation (plus grand saut en une image : {maxFrameJump:F2} m)");
            Check(!a.unitsOnRoof.Contains(unit), "3c. l'unité n'est plus inscrite sur le toit de A");

            // 4. Remonter, puis DESCENDRE ET GUETTER.
            yield return ClimbOnto(unit, a);
            unit.isGuarding = false;
            yield return RunOrder(unit, farStreet.Value, NodeAction.Guetter);
            p = unit.transform.position;
            Check(Flat(p, farStreet.Value) < 2.5f && p.y < 1f && unit.isGuarding,
                $"4. toit -> rue + GUETTER : à {Flat(p, farStreet.Value):F1} m, posture de guet={unit.isGuarding}");

            // 5. Remonter, puis passer sur le toit d'un autre immeuble séparé par une rue.
            if (b != null)
            {
                yield return ClimbOnto(unit, a);
                Vector3 bRoof = new Vector3(b.centroid.x, 0f, b.centroid.z);
                bRoof.y = RoofSurfaceY(b, bRoof);
                yield return RunOrder(unit, bRoof, NodeAction.Escalade);
                p = unit.transform.position;
                Check(BuildingStructure.FindBuildingAt(p) == b && p.y > UnitAI.RoofStrataThresholdY && unit.isRooftopSniper,
                    $"5. toit de A -> toit de B ({Flat(a.centroid, b.centroid):F0} m) : sur {BuildingStructure.FindBuildingAt(p)?.name ?? "aucun bâtiment"}, y={p.y:F2}");
            }
            else Debug.LogWarning("[RoofPlaytest] 5. ignoré : aucun immeuble B séparé par une rue trouvé.");

            Finish();
        }

        private void Finish()
        {
            Debug.Log($"[RoofPlaytest] RÉSULTAT : {passed} OK, {failed} échec(s).");
            int code = failed == 0 && passed > 0 ? 0 : 1;
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
#endif
