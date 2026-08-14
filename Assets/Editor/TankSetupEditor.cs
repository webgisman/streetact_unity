using UnityEngine;
using UnityEditor;
using UnityEngine.AI;

public class TankSetupEditor
{
    [MenuItem("Tools/Configurer le Tank (Leopard 2)")]
    public static void SetupTank()
    {
        string prefabPath = "Assets/kucher/Tank Leopard2/Prefabs/Leopard2.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        
        if (prefab == null)
        {
            Debug.LogError($"[TankSetup] Impossible de trouver le prefab à {prefabPath}. Vérifiez qu'il n'a pas été déplacé.");
            return;
        }

        // Commence l'édition du prefab
        string assetPath = AssetDatabase.GetAssetPath(prefab);
        using (var editingScope = new PrefabUtility.EditPrefabContentsScope(assetPath))
        {
            GameObject tankInstance = editingScope.prefabContentsRoot;

            // 1. Ajouter NavMeshAgent
            NavMeshAgent agent = tankInstance.GetComponent<NavMeshAgent>();
            if (agent == null) agent = tankInstance.AddComponent<NavMeshAgent>();
            agent.radius = 2.5f; // Évite qu'il ne rentre dans les polygones (bâtiments)
            agent.height = 3.0f;
            agent.baseOffset = 0.0f; // S'assure qu'il reste posé sur le sol
            agent.speed = 4.0f; // Un peu plus lent qu'un fantassin
            agent.acceleration = 6.0f;
            agent.angularSpeed = 60f; // Tourne lentement
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

            // 2. Ajouter CapsuleCollider (si absent)
            CapsuleCollider col = tankInstance.GetComponent<CapsuleCollider>();
            if (col == null) col = tankInstance.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0, 1.5f, 0);
            col.radius = 2.0f;
            col.height = 3.0f;

            // 3. Ajouter UnitAI
            UnitAI unitAI = tankInstance.GetComponent<UnitAI>();
            if (unitAI == null) unitAI = tankInstance.AddComponent<UnitAI>();
            
            unitAI.isTank = true;
            unitAI.health = 500;
            unitAI.porteeDetection = 25f;

            // 4. Assigner Tourelle et Canon
            Transform turret = null;
            Transform cannon = null;

            foreach (Transform t in tankInstance.GetComponentsInChildren<Transform>(true))
            {
                string lowerName = t.name.ToLower();
                if (lowerName.Contains("turret") || lowerName.Contains("tourelle")) 
                    turret = t;
                if (lowerName.Contains("cannon") || lowerName.Contains("barrel") || lowerName.Contains("canon")) 
                    cannon = t;
            }

            unitAI.turretBone = turret;
            unitAI.cannonBone = cannon;

            if (turret == null) Debug.LogWarning("[TankSetup] Tourelle introuvable dans le prefab.");
            if (cannon == null) Debug.LogWarning("[TankSetup] Canon introuvable dans le prefab.");

            Debug.Log("<color=green><b>[TankSetup] Tank Leopard 2 configuré avec succès !</b></color>");
        }
        
        AssetDatabase.Refresh();
    }
}
