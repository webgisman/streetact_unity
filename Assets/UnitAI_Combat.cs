using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class UnitAI
{
    // ==========================================
    // LOGIQUE DE COMBAT ET D'INTELLIGENCE
    // ==========================================

    private UnitAI currentLookTarget = null;
    private float lookUpdateTimer = 0f;

    /// <summary>
    /// Appelé à chaque frame.
    /// Gère la détection, la visée de la tourelle et les tirs équitables pour les deux camps.
    /// </summary>
    void Update()
    {
        if (!isDead)
        {
            // --- COMBAT LOGIC (TEMPS REEL) ---
            if (shootCooldown > 0) shootCooldown -= Time.deltaTime;

            lookUpdateTimer -= Time.deltaTime;
            if (lookUpdateTimer <= 0f)
            {
                currentLookTarget = GetVisibleEnemy();
                lookUpdateTimer = 0.25f; // Scan 4 fois par seconde pour une réactivité maximale
            }

            // Détection si nous sommes en phase d'exécution globale du tour
            TacticalPathManager pathManager = FindAnyObjectByType<TacticalPathManager>();
            bool isExecutionPhase = (pathManager != null && pathManager.phaseActuelle == TacticalPathManager.GamePhase.Execution) || isExecuting;

            if (currentLookTarget != null && !currentLookTarget.isDead)
            {
                Vector3 lookDir = currentLookTarget.transform.position - transform.position;
                lookDir.y = 0; // Rester bien droit

                if (lookDir.sqrMagnitude > 0.01f)
                {
                    // Rotation rapide et réactive pour éviter le désavantage du temps de visée
                    if (isTank && turretBone != null)
                    {
                        turretBone.rotation = Quaternion.RotateTowards(turretBone.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 180f);
                    }
                    else if (!isTank)
                    {
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 360f);
                    }
                }

                if (isExecutionPhase)
                {
                    float dist = Vector3.Distance(transform.position, currentLookTarget.transform.position);
                    float effectiveRange = isRooftopSniper ? (porteeDetection + 20f) : porteeDetection;
                    if (dist <= effectiveRange)
                    {
                        // Animation de tir pour l'infanterie
                        if (animator != null && !isTank)
                        {
                            animator.SetBool("IsShooting", true);
                        }

                        // Cadence d'action intense et nerveuse
                        float cooldownLimit = isTank ? (isCanonVehicle ? 1.4f : 1.8f) : 0.35f;
                        float aimAngle = Vector3.Angle(isTank && turretBone != null ? turretBone.forward : transform.forward, lookDir);

                        // Tir immédiat et dynamique sans bloquer le véhicule
                        if (shootCooldown <= 0f && aimAngle < 65f)
                        {
                            ShootAt(currentLookTarget);
                            shootCooldown = cooldownLimit; 
                        }
                    }
                }
            }
            else
            {
                // Pas de cible visible
                if (combatAudioSource != null && combatAudioSource.isPlaying) combatAudioSource.Stop();

                if (animator != null && !isTank)
                {
                    animator.SetBool("IsShooting", false);
                }
            }

            // --- ANIMATION ET AUDIO (Mouvement) ---
            float currentSpeed = 0f;
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                currentSpeed = agent.velocity.magnitude;
            }
            
            bool isAgentMoving = agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && !agent.isStopped;
            if (currentSpeed > 0.1f && isAgentMoving)
            {
                if (isTank)
                {
                    if (!footstepAudioSource.isPlaying)
                    {
                        footstepAudioSource.pitch = Random.Range(0.95f, 1.05f);
                        footstepAudioSource.Play();
                    }
                }
                else
                {
                    if (footstepAudioSource.isPlaying) footstepAudioSource.Stop();
                }
            }
            else
            {
                if (footstepAudioSource != null && footstepAudioSource.isPlaying) footstepAudioSource.Stop();
            }
            
            if (animator != null && !isTank)
            {
                animator.SetFloat("Speed", isClimbing ? 0f : currentSpeed);
            }
        }

        // Orientation de la barre de vie vers la caméra
        if (healthBarBg != null && Camera.main != null)
        {
            healthBarBg.LookAt(healthBarBg.position + Camera.main.transform.rotation * Vector3.forward,
                               Camera.main.transform.rotation * Vector3.up);
        }

        // Animation douce du cercle de sélection
        if (selectionRing != null && selectionRing.activeSelf)
        {
            float pulse = 1.1f + Mathf.Sin(Time.time * 4f) * 0.1f;
            selectionRing.transform.localScale = new Vector3(pulse, 0.02f, pulse);
            selectionRing.transform.Rotate(Vector3.up, 30f * Time.deltaTime);
        }
    }

    /// <summary>
    /// Appelé après toutes les fonctions Update.
    /// Utilisé pour les corrections cinématiques comme le blocage du bassin ou l'arme.
    /// </summary>
    void LateUpdate()
    {
        // --- REAL-TIME WEAPON TWEAKING ---
        // Applique les valeurs de l'Inspecteur à l'arme en temps réel pendant le jeu
        if (equippedWeapon != null)
        {
            equippedWeapon.transform.localPosition = weaponPosOffset;
            equippedWeapon.transform.localEulerAngles = weaponRotOffset;
            equippedWeapon.transform.localScale = new Vector3(weaponScale, weaponScale, weaponScale);
        }

        // SOLUTION PARE-BALLE : Annulation manuelle de la translation d'animation (Fantassins uniquement) !
        if (hipsBone != null && isExecuting && !isTank && !isClimbing)
        {
            Vector3 fixedPos = hipsBone.localPosition;
            fixedPos.x = initialHipsLocalPos.x;
            fixedPos.z = initialHipsLocalPos.z; // Empêche l'os d'avancer de 2.6 mètres et de saccader
            hipsBone.localPosition = fixedPos;
        }
    }

    /// <summary>
    /// Utilise un Raycast pour trouver l'ennemi visible le plus proche.
    /// </summary>
    private UnitAI GetVisibleEnemy()
    {
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        UnitAI bestTarget = null;
        float maxRange = isRooftopSniper ? (porteeDetection + 20f) : porteeDetection;
        float minDistance = maxRange;

        foreach (var unit in allUnits)
        {
            if (unit == this || unit.isDead || unit.teamID == this.teamID) continue; // Ignorer soi-même, les morts et ses alliés !

            float dist = Vector3.Distance(transform.position, unit.transform.position);
            if (dist < minDistance)
            {
                Vector3 toTarget = (unit.transform.position + Vector3.up * 1.5f) - transform.position;
                Vector3 toTargetNorm = toTarget.normalized;

                // Si en garnison à une fenêtre, vérifier que la cible est bien dans le champ de vision de la fenêtre (cône de 140°)
                if (isGarrisoned && currentWindow != null)
                {
                    if (Vector3.Dot(currentWindow.outwardNormal, toTargetNorm) < -0.1f) continue;
                }

                // Vérifier si un mur bloque la vue (Raycast)
                Vector3 rayStart = isGarrisoned ? (transform.position + Vector3.up * 1.3f + transform.forward * 0.5f) : (transform.position + Vector3.up * 1.5f);
                Vector3 rayDir = (unit.transform.position + Vector3.up * 1.5f) - rayStart;
                
                RaycastHit[] hits = Physics.RaycastAll(rayStart, rayDir.normalized, dist);
                bool hasLineOfSight = true;
                
                foreach (var hit in hits)
                {
                    UnitAI hitUnit = hit.collider.GetComponentInParent<UnitAI>();
                    if (hitUnit == this) continue; // Ignore son propre collider
                    if (hitUnit == unit) continue; // Atteint la cible
                    
                    // Si on tire depuis une fenêtre, ignorer le bâtiment dans lequel on est
                    if (isGarrisoned && hit.collider.GetComponentInParent<BuildingStructure>() != null && hit.distance < 1.0f) continue;

                    if (hit.collider.gameObject.name.Contains("Terrain") || hit.collider.gameObject.name.Contains("Building") || hit.collider.gameObject.name.Contains("City") || hit.collider.gameObject.name.Contains("Polygone") || hit.collider.gameObject.name.Contains("Mur") || hit.collider.gameObject.name.Contains("Wall") || hit.collider.gameObject.name.Contains("Batiment"))
                    {
                        hasLineOfSight = false; // Un mur extérieur bloque la vue
                        break;
                    }
                }
                
                if (hasLineOfSight)
                {
                    bestTarget = unit;
                    minDistance = dist;
                }
            }
        }
        return bestTarget;
    }

    /// <summary>
    /// Exécute un tir visuel, sonore et inflige des dégâts.
    /// </summary>
    private void ShootAt(UnitAI target)
    {
        Debug.Log($"<color=orange>[{gameObject.name}] Tire sur {target.gameObject.name} !</color>");
        
        // --- JOUER LE SON DU TIR SANS DÉCALAGE ---
        if (combatAudioSource != null && combatAudioSource.clip != null)
        {
            combatAudioSource.pitch = Random.Range(0.9f, 1.1f);
            combatAudioSource.Stop(); // Arrête le tir précédent
            // S'il s'agit du fichier WAV importé, on saute les 50 premières millisecondes (silence)
            if (combatAudioSource.clip.name.Contains("assault_rifle_gunshot")) 
            {
                combatAudioSource.time = 0.05f; 
            }
            combatAudioSource.Play();
        }

        // Position de départ du tir
        Vector3 startPos;
        if (isTank && cannonBone != null)
            startPos = cannonBone.position;
        else if (equippedWeapon != null)
            startPos = equippedWeapon.transform.position + transform.forward * 0.6f + Vector3.up * 0.05f;
        else
            startPos = transform.position + Vector3.up * 1.2f;

        Vector3 endPos = target.transform.position + Vector3.up * 1.2f; // Viser le torse
        
        // Effets visuels de tir
        if (isTank)
        {
            // Explosion de Tank
            if (muzzleFlashPrefab != null && cannonBone != null)
            {
                GameObject mf = Instantiate(muzzleFlashPrefab, startPos, cannonBone.rotation);
                mf.transform.localScale = Vector3.one * 5f; // 5x plus grand
                Destroy(mf, 1.0f); 
            }
        }
        else if (muzzleFlashPrefab != null && equippedWeapon != null)
        {
            GameObject mf = Instantiate(muzzleFlashPrefab, startPos, equippedWeapon.transform.rotation);
            mf.transform.SetParent(equippedWeapon.transform); // Le flash suit l'arme si elle bouge
            Destroy(mf, 0.1f); // Très court !
        }

        SpawnTracer(startPos, endPos, isTank);

        Vector3 hitDirection = (target.transform.position - transform.position).normalized;
        // Calcul des dégâts équilibrés
        float damageToDeal = isTank ? (isCanonVehicle ? 75f : 150f) : 15f; 
        target.TakeDamage(damageToDeal, hitDirection);
    }
}
