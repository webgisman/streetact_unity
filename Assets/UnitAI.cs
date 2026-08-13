using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class UnitAI : MonoBehaviour
{
    [Header("Unit Settings")]
    public int teamID;

    private NavMeshAgent agent;
    private UnitAI targetEnemy;
    private bool hasEngagedCombat = false;
    
    // Nouveautés pour les logs et la synchro
    private Vector3 initialTargetPos;
    private bool navMeshReady = false;

    void Start()
    {
        // Récupérer son propre composant NavMeshAgent
        agent = GetComponent<NavMeshAgent>();

        // EXTRÊMEMENT IMPORTANT : Par défaut, le masque de l'agent est "Everything" (tout).
        // Cela inclut la zone "Not Walkable" (Area 1) ! Donc l'agent marche sur les bâtiments.
        // On doit exclure "Not Walkable" (Area 1) du masque.
        int notWalkableArea = NavMesh.GetAreaFromName("Not Walkable");
        agent.areaMask = ~(1 << notWalkableArea);

        Debug.Log($"[UnitAI {gameObject.name}] Start. AreaMask is {agent.areaMask}. NotWalkable index: {notWalkableArea}");

        // Trouver toutes les instances de UnitAI actives dans la scène
        // Note: FindObjectsByType est la méthode standard et optimisée pour Unity 6
        UnitAI[] allUnits = Object.FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        float closestDistance = Mathf.Infinity;
        targetEnemy = null;

        // Identifier l'unité ennemie la plus proche
        foreach (UnitAI unit in allUnits)
        {
            // On s'ignore soi-même et on ignore les unités de la même équipe
            if (unit == this || unit.teamID == this.teamID)
                continue;

            float distance = Vector3.Distance(transform.position, unit.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                targetEnemy = unit;
            }
        }

        // On a trouvé un ennemi, mais on NE BOUGE PAS encore. On attend le NavMesh !
        if (targetEnemy != null)
        {
            initialTargetPos = targetEnemy.transform.position;
            Debug.Log($"[UnitAI {gameObject.name}] Target acquired: {targetEnemy.gameObject.name} at {initialTargetPos}. Waiting for NavMesh...");
        }
        else
        {
            Debug.Log($"[UnitAI {gameObject.name}] No target found.");
        }
    }

    public void OnNavMeshReady()
    {
        navMeshReady = true;
        if (targetEnemy != null)
        {
            Debug.Log($"[UnitAI {gameObject.name}] NavMesh is READY! Setting destination to {initialTargetPos}");
            bool pathSuccess = agent.SetDestination(initialTargetPos);
            Debug.Log($"[UnitAI {gameObject.name}] SetDestination result: {pathSuccess}. Agent isOnNavMesh: {agent.isOnNavMesh}");
        }
    }

    void Update()
    {
        // Logs de pathfinding une fois par seconde
        if (navMeshReady && agent.hasPath)
        {
            if (Time.frameCount % 60 == 0)
            {
                Debug.Log($"[UnitAI {gameObject.name}] Moving. Status: {agent.pathStatus}, Corners: {agent.path.corners.Length}, DistToTarget: {Vector3.Distance(transform.position, targetEnemy.transform.position)}");
            }
        }

        // Si on a une cible et qu'on n'a pas encore engagé le combat
        if (targetEnemy != null && !hasEngagedCombat)
        {
            float distance = Vector3.Distance(transform.position, targetEnemy.transform.position);
            
            // Si la distance devient inférieure à 2.5 mètres
            if (distance < 2.5f)
            {
                Debug.Log($"[UnitAI {gameObject.name}] Combat engagé avec l'ennemi !");
                hasEngagedCombat = true; // Le booléen assure que le message ne s'affiche qu'une seule fois
            }
        }
    }
}
