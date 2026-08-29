using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public partial class UnitAI : MonoBehaviour
{
    // ==========================================
    // VARIABLES ET CONFIGURATION DE BASE
    // ==========================================

    [Header("Unit Settings")]
    public int teamID;
    public bool isPlayerControlled = true;
    // Mis à true par UnitSpawnerUI.SpawnUnitAt juste après avoir fixé teamID/isPlayerControlled,
    // pour que Start() (qui s'exécute après, une fois Unity prêt) ne les écrase pas via la
    // détection par nom ci-dessous — voir Start().
    [System.NonSerialized] public bool teamAssignedBySpawner = false;
    public bool isVisible = true; // Visibilité par rapport au brouillard de guerre
    public bool isSelected = false; // Permet de savoir si le joueur planifie pour cette unité
    public float maxMovementPerTurn = 50f;

    [Header("Combat")]
    public int health = 100;
    public float maxHealth = 100f;
    public bool isDead = false;
    
    [Header("Système d'Armes")]
    public float porteeDetection = 15f;
    public float weaponScale = 1.0f;
    public Vector3 weaponPosOffset;
    public Vector3 weaponRotOffset;

    [Header("Mode Tank")]
    public bool isTank = false;
    public Transform turretBone;
    public Transform cannonBone;

    [Header("Mode Mortier")]
    public bool isMortar = false;
    public bool isMortarFiringMode = false;
    public Vector3 mortarTargetLock;
    public float mortarSalvoTimer = 0f;

    [Header("Escalade & Toits")]
    public bool isClimbing = false;
    private bool _isRooftopSniperManual = false;
    public bool isRooftopSniper
    {
        get => (!isTank && (transform.position.y > 2.2f || _isRooftopSniperManual));
        set => _isRooftopSniperManual = value;
    }

    [Header("Garnison & Bâtiments")]
    public bool isGarrisoned = false;
    public BuildingStructure.BuildingWindow currentWindow = null;
    public BuildingStructure currentBuilding = null;

    // --- VARIABLES PRIVÉES COMMUNES ---
    private GameObject equippedWeapon;
    private AudioSource combatAudioSource;
    private float shootCooldown = 0f;
    private float stuckThreshold = 1.5f;
    [HideInInspector] public UnityEngine.AI.NavMeshObstacle obstacle;

    // Prefabs WarFX
    private GameObject muzzleFlashPrefab;
    private GameObject bulletImpactPrefab;
    
    // Sons
    private AudioClip deathSoundClip;

    private NavMeshAgent agent;
    private Animator animator;
    private bool navMeshReady = false;

    // Tactical Path
    public List<TacticalPathManager.TacticalNode> tacticalPath = new List<TacticalPathManager.TacticalNode>();
    [HideInInspector] public int currentNodeIndex = 0;
    [HideInInspector] public bool isPathDirty = true;
    [HideInInspector] public List<Vector3> cachedDrawPoints = new List<Vector3>();
    private bool isExecuting = false;

    [Header("Tactique & Furtivité")]
    public bool isCamouflaged = false;
    public bool isGuarding = false;

    private Transform hipsBone;
    private Vector3 initialHipsLocalPos;

    private GameObject selectionRing;
    private AudioSource footstepAudioSource;

    private Transform healthBarBg;
    private Transform healthBarFill;
    public LineRenderer tacticalLineRenderer;

    public bool isCanonVehicle = false;

    // Registre global optimisé pour éliminer tous les FindObjectsByType coûteux
    public static readonly List<UnitAI> AllLivingUnits = new List<UnitAI>();

    void OnEnable()
    {
        if (!AllLivingUnits.Contains(this)) AllLivingUnits.Add(this);
    }

    void OnDisable()
    {
        AllLivingUnits.Remove(this);
    }

    /// <summary>
    /// Initialisation et configuration dynamique de l'unité (Physique, UI, Sons).
    /// </summary>
    void Start()
    {
        if (!AllLivingUnits.Contains(this)) AllLivingUnits.Add(this);

        // Auto-détection de l'équipe par nom d'objet (utile pour une unité placée à la main dans
        // l'Éditeur, ex: "Unite_1"/"Unite_2") — SAUF si le spawner (UnitSpawnerUI.SpawnUnitAt) a déjà
        // assigné l'équipe explicitement, ce qu'il signale via teamAssignedBySpawner. Sans ce garde-
        // fou, un nom comme "Fantassin_1_2" (équipe 1, 2e unité déployée) contient "_2" et Start()
        // — qui s'exécute APRÈS l'affectation explicite du spawner — la faisait basculer côté
        // ennemi : la 2e unité de chaque type déployée pour le joueur devenait injouable.
        string objName = gameObject.name.ToLower();
        if (!teamAssignedBySpawner)
        {
            if (objName.Contains("_2") || objName.Contains("enemy") || objName.Contains("ennemi") || objName.Contains("team2"))
            {
                isPlayerControlled = false;
                teamID = 2;
            }
            else if (objName.Contains("_1") || objName.Contains("team1") || objName.Contains("joueur") || objName.Contains("player"))
            {
                isPlayerControlled = true;
                teamID = 1;
            }
        }

        // --- INTEGRATION AUTOMATIQUE DU MARQUEUR TACTIQUE ---
        if (GetComponent<UnitTacticalMarker>() == null)
        {
            var marker = gameObject.AddComponent<UnitTacticalMarker>();
            marker.markerColor = isPlayerControlled ? Color.blue : Color.red;
        }
        else
        {
            var marker = GetComponent<UnitTacticalMarker>();
            marker.markerColor = isPlayerControlled ? Color.blue : Color.red;
            marker.RefreshMarker();
        }

        // Assignation des composants 3D au layer 'Units_3D' pour masquage en vue 2D
        int units3DLayer = LayerMask.NameToLayer("Units_3D");
        if (units3DLayer != -1)
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (!r.name.Contains("TacticalMarker"))
                {
                    r.gameObject.layer = units3DLayer;
                }
            }
        }

        // Optimisation CPU : Ne pas calculer l'animation des os quand le modèle 3D est masqué
        Animator anim = GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        }
        // ----------------------------------------------------

        agent = GetComponent<NavMeshAgent>();

        if (GetComponent<FogOfWarEntity>() == null)
        {
            gameObject.AddComponent<FogOfWarEntity>();
        }

        // AUTO-DETECTION DU TANK ET DU CANON-VEHICLE :
        // Si l'utilisateur pose le prefab brut sans le configurer, on le détecte !
        bool isLeopard = objName.Contains("leopard");
        isCanonVehicle = objName.Contains("canon");
        if (objName.Contains("mortier") || objName.Contains("mortar") || objName.Contains("turret"))
        {
            isMortar = true;
        }

        if (isLeopard || isCanonVehicle || isMortar)
        {
            isTank = true;
            
            // 1. CALCUL DYNAMIQUE PROPRE DES DIMENSIONS (Fini les valeurs magiques)
            Bounds bounds = new Bounds(transform.position, Vector3.zero);
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            bool hasBounds = false;
            foreach (Renderer r in renderers)
            {
                if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue;
                if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                else { bounds.Encapsulate(r.bounds); }
            }

            // 2. CONFIGURATION PHYSIQUE (BoxCollider parfait)
            BoxCollider box = gameObject.GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            
            // Convertir le centre global en coordonnée locale pour le collider
            box.center = transform.InverseTransformPoint(bounds.center);
            // Calculer la taille locale
            if (transform.lossyScale != Vector3.zero && transform.lossyScale.x != 0 && transform.lossyScale.y != 0 && transform.lossyScale.z != 0)
                box.size = new Vector3(bounds.size.x / transform.lossyScale.x, bounds.size.y / transform.lossyScale.y, bounds.size.z / transform.lossyScale.z);

            // 3. ANCRAGE NAVMESH ET RAYON
            if (agent != null)
            {
                // Le rayon repousse les autres agents de manière proportionnelle à la vraie taille
                agent.radius = Mathf.Max(bounds.size.x, bounds.size.z) / 2.2f;
                
                // baseOffset mathématiquement parfait pour que le point le plus bas (les chenilles) touche le sol
                agent.baseOffset = transform.position.y - bounds.min.y;
            }
            
            Rigidbody rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
        }
        else
        {
            CapsuleCollider cap = gameObject.GetComponent<CapsuleCollider>();
            if (cap == null) cap = gameObject.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0, 1f, 0);
            cap.radius = 0.4f;
            cap.height = 2f;
            
            Rigidbody rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
        }
        
        // DÉSACTIVER l'agent immédiatement pour éviter les erreurs NavMesh
        agent.enabled = false;
        
        animator = GetComponentInChildren<Animator>();
        if (animator != null)
        {
            animator.applyRootMotion = false;
            
            // Récupérer l'os du bassin pour annuler manuellement la translation Mixamo
            hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hipsBone != null)
            {
                initialHipsLocalPos = hipsBone.localPosition;
            }
        }
        
        // --- SETUP SELECTION RING (Effet Visuel) ---
        selectionRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(selectionRing.GetComponent<Collider>());
        selectionRing.transform.SetParent(this.transform);
        selectionRing.transform.localPosition = new Vector3(0, 0.05f, 0); // Au ras du sol
        selectionRing.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
        
        Material ringMat = SafeMaterialFactory.CreateUnlit(new Color(1f, 0.8f, 0f, 0.65f)); // Jaune/Or
        selectionRing.GetComponent<MeshRenderer>().sharedMaterial = ringMat;
        selectionRing.SetActive(false); // Caché par défaut

        // --- SETUP AUDIO (MOUVEMENT & MOTEURS) ---
        footstepAudioSource = gameObject.AddComponent<AudioSource>();
        footstepAudioSource.spatialBlend = 0.35f; // Audible et clair depuis la caméra tactique
        footstepAudioSource.rolloffMode = AudioRolloffMode.Linear;
        footstepAudioSource.minDistance = 15f;
        footstepAudioSource.maxDistance = 140f;
        footstepAudioSource.volume = isTank ? 0.85f : 0.65f;
        footstepAudioSource.playOnAwake = false;
        
        if (isTank)
        {
            AudioClip vehicleClip = null;
            if (isCanonVehicle) vehicleClip = Resources.Load<AudioClip>("Sounds/vehicle-walk-sound");
            else vehicleClip = Resources.Load<AudioClip>("Sounds/tank-walk_sound");
            
            if (vehicleClip == null) vehicleClip = ProceduralAudioBuilder.CreateVehicleEngineSound();
            footstepAudioSource.clip = vehicleClip;
            footstepAudioSource.loop = true;
        }
        else
        {
            AudioClip realFootstep = Resources.Load<AudioClip>("FootstepSound");
            if (realFootstep == null) realFootstep = Resources.Load<AudioClip>("Sounds/footstep");
            if (realFootstep == null) realFootstep = ProceduralAudioBuilder.CreateFootstepSound();
            footstepAudioSource.clip = realFootstep;
            footstepAudioSource.loop = true;
        }

        combatAudioSource = gameObject.AddComponent<AudioSource>();
        combatAudioSource.spatialBlend = 0.4f; // Plus 2D pour s'assurer que c'est audible de loin
        combatAudioSource.rolloffMode = AudioRolloffMode.Linear;
        combatAudioSource.minDistance = 20f;
        combatAudioSource.maxDistance = 150f;
        combatAudioSource.volume = 1f;
        
        // Charger le vrai son d'arme s'il existe, sinon utiliser le son procédural
        AudioClip realGunshot = null;
        if (isTank)
        {
            if (isCanonVehicle) realGunshot = Resources.Load<AudioClip>("Sounds/tir_vehicle");
            else realGunshot = Resources.Load<AudioClip>("Sounds/tir-tank");
        }
        else
        {
            realGunshot = Resources.Load<AudioClip>("GunshotSound");
        }
        
        combatAudioSource.clip = realGunshot != null ? realGunshot : ProceduralAudioBuilder.CreateGunshotSound();

        // Charger le son de mort
        deathSoundClip = Resources.Load<AudioClip>("DeathSound");

        if (isTank)
        {
            // Les chars utiliseront l'évitement dynamique de Unity (RVO) avec un grand rayon
            // ET agiront aussi comme des murs (NavMeshObstacle) quand ils sont à l'arrêt complet
            obstacle = GetComponent<UnityEngine.AI.NavMeshObstacle>();
            if (obstacle == null) obstacle = gameObject.AddComponent<UnityEngine.AI.NavMeshObstacle>();
            if (obstacle != null)
            {
                obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
                obstacle.carving = true;
                obstacle.enabled = false;
                obstacle.size = isCanonVehicle ? new Vector3(2f, 2f, 4f) : new Vector3(3.5f, 3f, 7.5f);
            }
        }

        // Réinitialisation de l'état de vie et nettoyage des particules
        isDead = false;
        Transform leftoverSmoke = transform.Find("BlackSmoke");
        if (leftoverSmoke != null) Destroy(leftoverSmoke.gameObject);

        // --- SETUP TANK OU FANTASSIN (Initialiser la vie AVANT la barre de vie) ---
        if (isTank)
        {
            if (isCanonVehicle)
            {
                health = 250;
                maxHealth = 250f;
            }
            else
            {
                health = 500;
                maxHealth = 500f;
            }
            porteeDetection = 45f; // Portée étendue pour les chars afin d'engager à longue distance
            
            // Si la tourelle n'est pas assignée, on essaie de la trouver
            if (turretBone == null)
            {
                foreach (Transform t in GetComponentsInChildren<Transform>())
                {
                    if (t.name.ToLower().Contains("turret")) turretBone = t;
                    if (t.name.ToLower().Contains("cannon") || t.name.ToLower().Contains("barrel")) cannonBone = t;
                }
            }

            // APPLICATION ROBUSTE DES MATÉRIAUX URP DU CHAR LEOPARD 2
            if (isLeopard)
            {
                Material bodyMat = Resources.Load<Material>("Kucher/Tank Leopard2/Materials/TankBodyMaterial");
                Material leftTrackMat = Resources.Load<Material>("Kucher/Tank Leopard2/Materials/LeftTrackMaterial");
                Material rightTrackMat = Resources.Load<Material>("Kucher/Tank Leopard2/Materials/RightTrackMaterial");

                if (bodyMat != null)
                {
                    foreach (Renderer r in GetComponentsInChildren<Renderer>())
                    {
                        if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue;

                        string rName = r.gameObject.name.ToLower();
                        if (rName.Contains("track_l") || rName.Contains("lefttrack") || rName.Contains("trackl"))
                            r.sharedMaterial = leftTrackMat ?? bodyMat;
                        else if (rName.Contains("track_r") || rName.Contains("righttrack") || rName.Contains("trackr"))
                            r.sharedMaterial = rightTrackMat ?? bodyMat;
                        else if (rName.Contains("track"))
                            r.sharedMaterial = leftTrackMat ?? bodyMat;
                        else
                            r.sharedMaterial = bodyMat;
                    }
                }
            }
            else if (isMortar)
            {
                health = 350;
                maxHealth = 350f;
                porteeDetection = 120f;

                Texture2D gradTex = Resources.Load<Texture2D>("gradientTexturelar");
                if (gradTex == null)
                {
                    string p = Application.dataPath + "/gradientTexturelar.png";
                    if (System.IO.File.Exists(p))
                    {
                        byte[] raw = System.IO.File.ReadAllBytes(p);
                        gradTex = new Texture2D(2, 2);
                        gradTex.LoadImage(raw);
                    }
                }

                Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (gradTex != null && litShader != null)
                {
                    Material mortarMat = new Material(litShader);
                    mortarMat.mainTexture = gradTex;
                    if (mortarMat.HasProperty("_BaseMap")) mortarMat.SetTexture("_BaseMap", gradTex);
                    if (mortarMat.HasProperty("_MainTex")) mortarMat.SetTexture("_MainTex", gradTex);

                    foreach (Renderer r in GetComponentsInChildren<Renderer>())
                    {
                        if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue;
                        r.sharedMaterial = mortarMat;
                    }
                }
            }
        }
        else
        {
            health = 100;
            maxHealth = 100f;
            
            // L'infanterie conserve sa couleur d'origine (verte) du modèle 3D.
            // Seule la barre de vie permet de distinguer les équipes.
            
            // --- EQUIPER L'ARME (Fantassin uniquement) ---
            EquipWeapon();
        }

        // Configuration propre de la barre de vie (100% pleine au départ)
        SetupHealthBar();

        // --- SETUP WARFX ---
        muzzleFlashPrefab = Resources.Load<GameObject>("WarFX/MuzzleFlash");
        bulletImpactPrefab = Resources.Load<GameObject>("WarFX/BulletImpact");

        // === COLLIDER CORRECT (TRIGGER POUR NE PAS BLOQUER LE NAVMESH) ===
        CapsuleCollider capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            capsule.center = new Vector3(0, 1f, 0);
            capsule.height = 2f;
            capsule.radius = 0.5f;
            capsule.isTrigger = true; // CRITIQUE : Empêche PhysX de repousser l'agent en arrière
        }

        stuckThreshold = Random.Range(1.0f, 2.5f); // Seuil de patience aléatoire pour le face-à-face
        shootCooldown = 0.3f; // Prêt à faire feu dès le début du combat sans avantage injuste

        // Détection automatique du NavMesh si déjà présent dans la scène
        StartCoroutine(AutoCheckNavMeshCoroutine());
    }

    private System.Collections.IEnumerator AutoCheckNavMeshCoroutine()
    {
        yield return new WaitForSeconds(0.2f);
        if (!navMeshReady)
        {
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out hit, 15f, UnityEngine.AI.NavMesh.AllAreas))
            {
                OnNavMeshReady();
            }
        }
    }

    [HideInInspector] public bool isPerformingCheckpointAction = false;

    /// <summary>
    /// Indique si l'unité est en train de bouger ou d'exécuter une action tactique (Attendre, Guetter, Escalader).
    /// </summary>
    public bool IsMovingOrActing()
    {
        return isExecuting || isPerformingCheckpointAction || isClimbing;
    }

    /// <summary>
    /// Indique si l'unité a une cible ennemie vivante dans sa portée de tir.
    /// </summary>
    public bool HasActiveTargetInRange()
    {
        return currentLookTarget != null && !currentLookTarget.isDead && Vector3.Distance(transform.position, currentLookTarget.transform.position) <= porteeDetection;
    }

    /// <summary>
    /// Indique si l'unité est actuellement en train de se déplacer physiquement sur le terrain.
    /// </summary>
    public bool IsMoving()
    {
        return isExecuting && agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && !agent.isStopped && agent.hasPath;
    }

    private void EquipWeapon()
    {
        if (animator == null) return;
        
        Transform rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (rightHand != null)
        {
            GameObject weaponPrefab = Resources.Load<GameObject>("VerzatileAK");
            if (weaponPrefab != null)
            {
                equippedWeapon = Instantiate(weaponPrefab, rightHand);
                
                // On applique nos valeurs modifiables depuis l'Inspecteur
                equippedWeapon.transform.localPosition = weaponPosOffset;
                equippedWeapon.transform.localEulerAngles = weaponRotOffset;
                equippedWeapon.transform.localScale = new Vector3(weaponScale, weaponScale, weaponScale);
            }
        }
    }

    /// <summary>
    /// Active ou masque complètement tous les visuels de l'unité (corps, arme AK-47, barre de vie, audio) pour le Fog of War.
    /// </summary>
    public void SetVisualsVisibility(bool isVisible)
    {
        this.isVisible = isVisible;
        Renderer[] allRenderers = GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < allRenderers.Length; i++)
        {
            Renderer r = allRenderers[i];
            if (r != null && !r.gameObject.name.Contains("Health") && !r.gameObject.name.Contains("selectionRing"))
            {
                r.enabled = isVisible;
            }
        }

        if (healthBarBg != null)
        {
            healthBarBg.gameObject.SetActive(isVisible && !isDead);
        }

        if (selectionRing != null)
        {
            selectionRing.SetActive(isVisible && isSelected);
        }

        if (!isVisible && footstepAudioSource != null && footstepAudioSource.isPlaying)
        {
            footstepAudioSource.Stop();
        }
    }

    public void SetSelected(bool isSelected)
    {
        this.isSelected = isSelected;
        if (selectionRing != null)
        {
            selectionRing.SetActive(isSelected);
        }
        
        // Mode Obstacle explicite : si le char est sélectionné, on le repasse en Agent pour pouvoir dessiner sa trajectoire
        if (isTank && !isExecuting)
        {
            SetObstacleMode(!isSelected);
        }
    }

    private void SetObstacleMode(bool enableObstacle)
    {
        if (!isTank || obstacle == null || agent == null) return;
        
        if (enableObstacle)
        {
            agent.enabled = false;
            obstacle.enabled = true;
        }
        else
        {
            obstacle.enabled = false;
            agent.enabled = true;
        }
    }

    /// <summary>
    /// Crée et configure proprement la barre de vie (Quad Billboard 3D).
    /// </summary>
    public void SetupHealthBar()
    {
        // Nettoyer les anciens quads dupliqués si l'objet est un clone
        foreach (Transform child in transform)
        {
            if (child.name == "HealthBarBG")
            {
                Destroy(child.gameObject);
            }
        }

        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "HealthBarBG";
        Destroy(bg.GetComponent<Collider>());
        bg.transform.SetParent(this.transform);
        
        bool isHeavy = isTank || gameObject.name.ToLower().Contains("leopard") || gameObject.name.ToLower().Contains("canon");
        float barHeight = isTank ? 2.8f : (gameObject.name.ToLower().Contains("mortier") ? 1.8f : 2.1f);
        bg.transform.localPosition = new Vector3(0, barHeight, 0); 
        bg.transform.localScale = isHeavy ? new Vector3(3.0f, 0.4f, 1f) : new Vector3(1.5f, 0.2f, 1f);
        
        Material bgMat = SafeMaterialFactory.CreateUnlit(new Color(0.08f, 0.08f, 0.08f, 0.95f));
        bg.GetComponent<Renderer>().material = bgMat;
        healthBarBg = bg.transform;

        GameObject fg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fg.name = "HealthBarFill";
        Destroy(fg.GetComponent<Collider>());
        fg.transform.SetParent(bg.transform);
        fg.transform.localPosition = new Vector3(0, 0, -0.02f);
        fg.transform.localScale = new Vector3(1f, 1f, 1f);
        
        Color teamCol = (teamID == 2) ? new Color(1f, 0.15f, 0.15f, 1f) : new Color(0.15f, 0.6f, 1f, 1f);
        Material fgMat = SafeMaterialFactory.CreateUnlit(teamCol);
        fg.GetComponent<Renderer>().material = fgMat;
        healthBarFill = fg.transform;

        UpdateHealthBar();
    }

    /// <summary>
    /// Met à jour visuellement le niveau et la couleur de la barre de vie.
    /// </summary>
    public void UpdateHealthBar()
    {
        if (healthBarFill == null && healthBarBg != null)
        {
            Transform fg = healthBarBg.Find("HealthBarFill");
            if (fg != null) healthBarFill = fg;
        }

        if (healthBarFill != null)
        {
            float safeMax = maxHealth > 0 ? maxHealth : (isTank ? 500f : 100f);
            if (health > safeMax) safeMax = health;
            maxHealth = safeMax;

            float pct = Mathf.Clamp01((float)health / safeMax);
            healthBarFill.localScale = new Vector3(pct, 1f, 1f);
            healthBarFill.localPosition = new Vector3((pct - 1f) * 0.5f, 0, -0.02f);

            Renderer r = healthBarFill.GetComponent<Renderer>();
            if (r != null && r.material != null)
            {
                Color teamCol = (teamID == 2) ? new Color(1f, 0.15f, 0.15f, 1f) : new Color(0.15f, 0.6f, 1f, 1f);
                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", teamCol);
                else r.material.color = teamCol;
            }
        }
    }

    private float lastHitAnimTime = -10f;

    /// <summary>
    /// Fonction centrale gérant la réception de dégâts et la mort.
    /// </summary>
    public void TakeDamage(float amount, Vector3 hitDirection)
    {
        // Protection Heavy Cover : 75% de réduction des dégâts derrière le mur de la fenêtre
        if (isGarrisoned)
        {
            amount = Mathf.Max(1f, amount * 0.25f);
            Debug.Log($"<color=cyan>[{gameObject.name}] 🛡️ Couverture Lourde à la fenêtre ! Dégâts réduits à {amount:F0}.</color>");
        }
        else if (RoadBarrier.IsUnitNearBarrier(transform.position, 2.2f))
        {
            amount = Mathf.Max(1f, amount * 0.4f);
            Debug.Log($"<color=cyan>[{gameObject.name}] 🚧 Couverture Barricade Routière (-60% dégâts) ! Dégâts réduits à {amount:F0}.</color>");
        }
        else if (isGuarding)
        {
            amount = Mathf.Max(1f, amount * 0.5f);
            Debug.Log($"<color=cyan>[{gameObject.name}] 🛡️ Posture Guet active (+50% défense) ! Dégâts réduits à {amount:F0}.</color>");
        }

        // Tout impact reçu rompt l'invisibilité/camouflage
        isCamouflaged = false;

        health -= (int)amount;
        Debug.Log($"[{gameObject.name}] a pris {amount} degats. Vie restante: {health}");
        
        // Mise à jour de la barre de vie
        UpdateHealthBar();

        if (hitDirection == default) hitDirection = -transform.forward;
        
        // Effets d'Impact Procéduraux selon la cible
        if (isTank)
        {
            SpawnSparksEffect(transform.position + Vector3.up * 1.5f, hitDirection);
        }
        else
        {
            SpawnBloodEffect(transform.position + Vector3.up * 1.2f, hitDirection);
        }

        // Impact de Balle WarFX
        if (bulletImpactPrefab != null)
        {
            GameObject impact = Instantiate(bulletImpactPrefab, transform.position + Vector3.up * 1.2f, Quaternion.LookRotation(-hitDirection));
            Destroy(impact, 2f);
        }

        if (health <= 0)
        {
            Die();
        }
        else
        {
            // Cooldown de 1.2s sur l'animation de Hit pour éviter le stun-lock permanent lors d'un duel
            if (animator != null && !isTank && Time.time - lastHitAnimTime > 1.2f)
            {
                lastHitAnimTime = Time.time;
                animator.SetTrigger("Hit");
            }
        }
    }

    /// <summary>
    /// Fait sortir le soldat de son poste de garnison/fenêtre.
    /// </summary>
    public void LeaveGarrison()
    {
        if (currentWindow != null)
        {
            currentWindow.isOccupied = false;
            currentWindow.occupant = null;
            currentWindow = null;
        }
        isGarrisoned = false;
    }

    /// <summary>
    /// Gère la destruction ou la mort de l'unité (Animation, Audio, Particules, Physique).
    /// </summary>
    private void Die()
    {
        isDead = true;
        AllLivingUnits.Remove(this);
        Debug.Log($"<color=black><b>[{gameObject.name}] EST MORT !</b></color>");
        
        // Quitter le bâtiment et libérer la garnison
        if (currentBuilding != null)
        {
            currentBuilding.UnregisterUnitInside(this);
            currentBuilding = null;
        }
        LeaveGarrison();

        // Cacher la barre de vie
        if (healthBarBg != null) healthBarBg.gameObject.SetActive(false);

        // Arrêter le son de l'arme si l'unité meurt en tirant
        if (combatAudioSource != null && combatAudioSource.isPlaying) combatAudioSource.Stop();

        // Signaler immédiatement au manager pour ne JAMAIS bloquer le tour
        if (isExecuting)
        {
            isExecuting = false;
            TacticalPathManager manager = FindAnyObjectByType<TacticalPathManager>();
            if (manager != null) manager.SignalerFinMouvement(this);
        }

        // Désactiver les collisions pour ne pas bloquer les routes avec les cadavres
        Collider col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        // AUDIO DE MORT : Séparation stricte Tank vs Infanterie
        if (!isTank && deathSoundClip != null && combatAudioSource != null)
        {
            // Cri humain
            combatAudioSource.pitch = 1f;
            combatAudioSource.PlayOneShot(deathSoundClip);
        }
        else if (isTank)
        {
            // Son d'explosion GÉANT
            AudioClip explosionSound = Resources.Load<AudioClip>("ExplosionSound");
            if (explosionSound != null) AudioSource.PlayClipAtPoint(explosionSound, transform.position, 5.0f);
            else AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateMortarExplosionSound(), transform.position, 5.0f);
            
            // Effet visuel massif de destruction
            if (muzzleFlashPrefab != null)
            {
                GameObject bigExplosion = Instantiate(muzzleFlashPrefab, transform.position + Vector3.up * 2f, Quaternion.identity);
                bigExplosion.transform.localScale = Vector3.one * 10f;
                Destroy(bigExplosion, 2f);
            }
            
            // Générer de la fumée noire continue depuis l'épave
            SpawnBlackSmoke();
        }

        if (agent != null)
        {
            if (agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
            agent.enabled = false;
        }

        if (animator != null && !isTank)
        {
            animator.SetBool("IsShooting", false);
            animator.SetFloat("Speed", 0f);
            animator.SetTrigger("Die");
            StartCoroutine(FixMixamoHoverBug());
        }
        else if (isTank)
        {
            // Destruction du tank : Il devient complètement noir (brûlé)
            foreach (Renderer r in GetComponentsInChildren<Renderer>())
            {
                r.material.color = Color.black;
            }
            
            // On peut aussi rajouter une fumée noire si on l'a dans WarFX
            if (muzzleFlashPrefab != null && turretBone != null)
            {
                GameObject explosion = Instantiate(muzzleFlashPrefab, transform.position + Vector3.up, Quaternion.identity);
                explosion.transform.localScale = Vector3.one * 5f;
                Destroy(explosion, 3f);
            }
        }
        
        if (footstepAudioSource != null && footstepAudioSource.isPlaying)
        {
            footstepAudioSource.Stop(); // S'assurer que le son de pas se coupe
        }
        
        SetSelected(false);
    }

    private System.Collections.IEnumerator FixMixamoHoverBug()
    {
        // 1. On laisse le personnage faire TOUTE son animation de chute en arrière
        yield return new WaitForSeconds(1.3f);

        // 2. Une fois qu'il est parfaitement couché (à plat), on le glisse au sol en 0.2s
        // Cela empêche le corps ou la tête de traverser le sol pendant la chute !
        Transform visual = animator.transform;
        float elapsed = 0f;
        while (elapsed < 0.2f)
        {
            elapsed += Time.deltaTime;
            visual.localPosition = Vector3.Lerp(Vector3.zero, new Vector3(0, -0.9f, 0), elapsed / 0.2f);
            yield return null;
        }
        visual.localPosition = new Vector3(0, -0.9f, 0); // Sécurité
    }

    /// <summary>
    /// Tire un obus d'artillerie parabolique vers la position cible avec effet visuel et sonore.
    /// </summary>
    public void FireMortarShell(Vector3 targetPos)
    {
        Vector3 muzzlePos = transform.position + Vector3.up * 1.8f;
        AudioClip launchClip = ProceduralAudioBuilder.CreateMortarLaunchSound();
        if (launchClip != null) AudioSource.PlayClipAtPoint(launchClip, muzzlePos, 1.0f);

        if (muzzleFlashPrefab != null)
        {
            GameObject mf = Instantiate(muzzleFlashPrefab, muzzlePos, Quaternion.LookRotation(Vector3.up));
            mf.transform.localScale = Vector3.one * 5f;
            Destroy(mf, 1.0f);
        }

        Novgov.Combat.MortarShell.Launch(muzzlePos, targetPos, teamID);
        Debug.Log($"<color=orange>[{gameObject.name}] 💥 Obus de mortier tiré vers ({targetPos.x:F1}, {targetPos.z:F1}) !</color>");
        
        FogOfWarEntity fow = GetComponent<FogOfWarEntity>();
        if (fow != null) fow.NotifyAttack();
    }

    /// <summary>
    /// Réinitialise l'état de l'unité à la fin forcée ou normale du tour.
    /// </summary>
    public void ResetOrderState()
    {
        isExecuting = false;
        isPerformingCheckpointAction = false;
        isClimbing = false;
        tacticalPath.Clear();
        currentNodeIndex = 0;
        if (tacticalLineRenderer != null) tacticalLineRenderer.positionCount = 0;

        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }
        SetObstacleMode(true);
        if (animator != null && !isTank)
        {
            animator.SetFloat("Speed", 0f);
            animator.SetBool("IsShooting", false);
        }
    }

    /// <summary>
    /// Efface l'ensemble de la trajectoire planifiée de l'unité.
    /// </summary>
    public void ClearTacticalPath()
    {
        tacticalPath.Clear();
        currentNodeIndex = 0;
        isPathDirty = true;
        cachedDrawPoints.Clear();
        if (tacticalLineRenderer != null) tacticalLineRenderer.positionCount = 0;
        TacticalPathManager.SetPathsDirty();
    }

    /// <summary>
    /// Multijoueur/serveur : reflète l'état "IsShooting" de l'Animator sans exposer le champ privé.
    /// Utilisé par MatchSessionManager pour construire les snapshots réseau (voir Assets/Scripts/Server/).
    /// </summary>
    public bool IsShootingNow => animator != null && animator.GetBool("IsShooting");

    /// <summary>
    /// Multijoueur/client : applique un point de vie reçu du serveur (snapshot de lecture), sans
    /// recalculer aucune réduction de dégâts locale (déjà appliquée côté serveur autoritaire).
    /// </summary>
    public void SetNetworkHealth(int newHealth)
    {
        health = newHealth;
        UpdateHealthBar();
    }

    /// <summary>
    /// Multijoueur/client : déclenche la séquence de mort existante quand le serveur signale
    /// qu'une unité est morte pendant la lecture d'un snapshot (voir MultiplayerMatchController).
    /// </summary>
    public void ApplyNetworkDeath()
    {
        if (!isDead) Die();
    }

    /// <summary>
    /// Multijoueur/client : pilote l'Animator pendant la lecture d'un snapshot (mouvement/tir),
    /// sans dupliquer la logique de combat/déplacement réelle qui reste strictement côté serveur.
    /// </summary>
    public void SetNetworkAnimState(bool shooting, float moveSpeed)
    {
        if (animator == null || isTank) return;
        animator.SetBool("IsShooting", shooting);
        animator.SetFloat("Speed", moveSpeed);
    }

    public void RemoveLastTacticalNode()
    {
        if (tacticalPath.Count > 0)
        {
            TacticalPathManager.NodeAction removedAction = tacticalPath[tacticalPath.Count - 1].action;
            tacticalPath.RemoveAt(tacticalPath.Count - 1);
            isPathDirty = true;
            cachedDrawPoints.Clear();

            // "Entrer bâtiment" pose currentBuilding immédiatement au moment du choix (pour ouvrir
            // l'aperçu du toit en direct), avant même que l'unité n'ait bougé — annuler ce nœud doit
            // donc aussi annuler cet état, sinon l'unité reste marquée "à l'intérieur" alors qu'elle
            // n'a jamais bougé, et tous les clics suivants pour cette unité sont mal interprétés.
            if (removedAction == TacticalPathManager.NodeAction.EntrerBatiment)
            {
                currentBuilding = null;
            }

            if (tacticalPath.Count == 0 && tacticalLineRenderer != null)
            {
                tacticalLineRenderer.positionCount = 0;
            }
            TacticalPathManager.SetPathsDirty();
        }
    }
}
