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

    [Header("Escalade & Toits")]
    public bool isClimbing = false;
    public bool isRooftopSniper => (!isTank && transform.position.y > 2.5f);

    [Header("Garnison & Fenêtres")]
    public bool isGarrisoned = false;
    public BuildingStructure.BuildingWindow currentWindow = null;

    // --- VARIABLES PRIVÉES COMMUNES ---
    private GameObject equippedWeapon;
    private AudioSource combatAudioSource;
    private float shootCooldown = 0f;
    private float stuckThreshold = 1.5f;
    private UnityEngine.AI.NavMeshObstacle obstacle;

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
    private int currentNodeIndex = 0;
    private bool isExecuting = false;

    private Transform hipsBone;
    private Vector3 initialHipsLocalPos;

    private GameObject selectionRing;
    private AudioSource footstepAudioSource;

    // UI
    private Transform healthBarBg;
    private Transform healthBarFill;

    private bool isCanonVehicle = false;

    /// <summary>
    /// Initialisation et configuration dynamique de l'unité (Physique, UI, Sons).
    /// </summary>
    void Start()
    {
        agent = GetComponent<NavMeshAgent>();

        // AUTO-DETECTION DU TANK ET DU CANON-VEHICLE :
        // Si l'utilisateur pose le prefab brut sans le configurer, on le détecte !
        string objName = gameObject.name.ToLower();
        bool isLeopard = objName.Contains("leopard");
        isCanonVehicle = objName.Contains("canon");

        if (isLeopard || isCanonVehicle)
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
        
        // Auto-configuration pour tester (Toutes les unités avec '_2' seront l'ennemi)
        if (gameObject.name.Contains("_2"))
        {
            teamID = 2;
            isPlayerControlled = false;
        }
        else
        {
            teamID = 1;
            isPlayerControlled = true;
        }

        // --- SETUP SELECTION RING (Effet Visuel) ---
        selectionRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(selectionRing.GetComponent<Collider>());
        selectionRing.transform.SetParent(this.transform);
        selectionRing.transform.localPosition = new Vector3(0, 0.05f, 0); // Au ras du sol
        selectionRing.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
        
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
        Material ringMat = new Material(unlitShader);
        ringMat.SetColor("_BaseColor", new Color(1f, 0.8f, 0f, 0.5f)); // Jaune/Or
        if (ringMat.HasProperty("_Color")) ringMat.SetColor("_Color", new Color(1f, 0.8f, 0f, 0.5f));
        ringMat.SetFloat("_Surface", 1);
        ringMat.SetFloat("_Blend", 0);
        selectionRing.GetComponent<MeshRenderer>().sharedMaterial = ringMat;
        selectionRing.SetActive(false); // Caché par défaut

        // --- SETUP AUDIO ---
        footstepAudioSource = gameObject.AddComponent<AudioSource>();
        footstepAudioSource.spatialBlend = 1f;
        footstepAudioSource.volume = 1f; // Volume MAX pour les bruits de pas
        
        if (isTank)
        {
            if (isCanonVehicle)
            {
                AudioClip canonWalk = Resources.Load<AudioClip>("Sounds/vehicle-walk");
                if (canonWalk != null) { footstepAudioSource.clip = canonWalk; footstepAudioSource.loop = true; }
                footstepAudioSource.volume = 1f;
            }
            else
            {
                AudioClip tankWalk = Resources.Load<AudioClip>("Sounds/tank-walk-sound");
                if (tankWalk != null) { footstepAudioSource.clip = tankWalk; footstepAudioSource.loop = true; }
                footstepAudioSource.volume = 1f;
            }
        }
        else
        {
            AudioClip realFootstep = Resources.Load<AudioClip>("FootstepSound");
            if (realFootstep != null)
            {
                footstepAudioSource.clip = realFootstep;
                footstepAudioSource.loop = true; // Le MP3 importé est une piste continue
            }
            else
            {
                footstepAudioSource.clip = ProceduralAudioBuilder.CreateFootstepSound();
                footstepAudioSource.loop = true; // Boucler le procédural aussi
            }
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
            obstacle = gameObject.AddComponent<UnityEngine.AI.NavMeshObstacle>();
            obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
            obstacle.carving = true;
            obstacle.enabled = false;
            obstacle.size = isCanonVehicle ? new Vector3(2f, 2f, 4f) : new Vector3(3.5f, 3f, 7.5f);
        }

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

            // COLORATION URP (Résout le tank rose) et SCALE (Résout les murs)
#if UNITY_EDITOR
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            
            if (isLeopard)
            {
                Texture2D bodyTex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Kucher/Tank Leopard2/Textures/Tank Body Textures/TankBodyDiffuseMap.png");
                Texture2D trackTex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Kucher/Tank Leopard2/Textures/Tank Track Textures/TankTrackDiffuseMap.png");
                
                foreach (Renderer r in GetComponentsInChildren<Renderer>())
                {
                    if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue; // Ne pas repeindre la barre de vie !

                    if (urpLit != null) r.material.shader = urpLit;
                    
                    string rName = r.gameObject.name.ToLower();
                    Texture2D texToUse = rName.Contains("track") ? trackTex : bodyTex;
                    
                    if (texToUse != null)
                    {
                        if (r.material.HasProperty("_BaseMap")) r.material.SetTexture("_BaseMap", texToUse);
                        else r.material.mainTexture = texToUse;
                    }
                }
            }
            else if (isCanonVehicle)
            {
                // Appliquer seulement le shader URP sans écraser les textures
                foreach (Renderer r in GetComponentsInChildren<Renderer>())
                {
                    if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue;
                    if (urpLit != null) r.material.shader = urpLit;
                }
            }
#endif
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
        return (isExecuting && agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && !agent.isStopped && agent.hasPath) 
               || isPerformingCheckpointAction 
               || isClimbing;
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
            else
            {
                Debug.LogError("Impossible de charger l'arme VerzatileAK. Assurez-vous qu'elle est dans un dossier Resources.");
            }
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
        float barHeight = isHeavy ? 4.8f : 2.5f;
        bg.transform.localPosition = new Vector3(0, barHeight, 0); 
        bg.transform.localScale = isHeavy ? new Vector3(3.0f, 0.4f, 1f) : new Vector3(1.5f, 0.2f, 1f);
        
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        Material bgMat = new Material(unlitShader);
        if (bgMat.HasProperty("_BaseColor")) bgMat.SetColor("_BaseColor", new Color(0.08f, 0.08f, 0.08f, 0.95f));
        else bgMat.color = Color.black;
        bg.GetComponent<Renderer>().material = bgMat;
        healthBarBg = bg.transform;

        GameObject fg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fg.name = "HealthBarFill";
        Destroy(fg.GetComponent<Collider>());
        fg.transform.SetParent(bg.transform);
        fg.transform.localPosition = new Vector3(0, 0, -0.02f);
        fg.transform.localScale = new Vector3(1f, 1f, 1f);
        
        Material fgMat = new Material(unlitShader);
        Color teamCol = (teamID == 2) ? new Color(1f, 0.15f, 0.15f, 1f) : new Color(0.15f, 0.6f, 1f, 1f);
        if (fgMat.HasProperty("_BaseColor")) fgMat.SetColor("_BaseColor", teamCol);
        else fgMat.color = teamCol;
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
        if (isDead) return;

        // Protection Heavy Cover : 75% de réduction des dégâts derrière le mur de la fenêtre
        if (isGarrisoned)
        {
            amount = Mathf.Max(1f, amount * 0.25f);
            Debug.Log($"<color=cyan>[{gameObject.name}] 🛡️ Couverture Lourde à la fenêtre ! Dégâts réduits à {amount:F0}.</color>");
        }

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
        Debug.Log($"<color=black><b>[{gameObject.name}] EST MORT !</b></color>");
        
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
            else AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateGunshotSound(), transform.position, 5.0f);
            
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
    /// Réinitialise l'état de l'unité à la fin forcée ou normale du tour.
    /// </summary>
    public void ResetOrderState()
    {
        isExecuting = false;
        isPerformingCheckpointAction = false;
        tacticalPath.Clear();
        currentNodeIndex = 0;
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
}
