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
            UpdateCoverAura();
            
            // --- COMBAT LOGIC (TEMPS REEL) ---
            if (shootCooldown > 0) shootCooldown -= Time.deltaTime;

            // Le mortier du joueur obéit strictement aux coordonnées ciblées et n'engage pas de cibles aléatoires
            if (!isMortar || !isPlayerControlled)
            {
                lookUpdateTimer -= Time.deltaTime;
                if (lookUpdateTimer <= 0f)
                {
                    currentLookTarget = GetVisibleEnemy();
                    lookUpdateTimer = 0.25f; // Scan 4 fois par seconde pour une réactivité maximale
                }
            }
            else
            {
                currentLookTarget = null;
            }

            // Détection si nous sommes en phase d'exécution globale du tour (Zéro allocation)
            TacticalPathManager pathManager = TacticalPathManager.Instance;
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

            // --- ANIMATION ET AUDIO (Mouvement & Moteurs) ---
            float currentSpeed = 0f;
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                currentSpeed = agent.velocity.magnitude;
            }
            else if (animator != null)
            {
                currentSpeed = animator.GetFloat("Speed");
            }
            
            bool isActuallyMoving = currentSpeed > 0.15f && !isDead && (isPlayerControlled || isVisible);

            if (isActuallyMoving)
            {
                if (footstepAudioSource != null && !footstepAudioSource.isPlaying && footstepAudioSource.clip != null)
                {
                    footstepAudioSource.pitch = isTank ? Random.Range(0.95f, 1.05f) : 1.0f;
                    footstepAudioSource.Play();
                }
            }
            else
            {
                if (footstepAudioSource != null && footstepAudioSource.isPlaying)
                {
                    footstepAudioSource.Stop();
                }
            }
            
            if (animator != null && !isTank)
            {
                animator.SetFloat("Speed", isClimbing ? 0f : currentSpeed);
            }
        }

        // Orientation de la barre de vie vers la caméra (Billboard propre)
        if (healthBarBg != null)
        {
            Camera cam = Camera.main ?? TacticalCamera.Instance?.GetComponent<Camera>();
            if (cam != null)
            {
                healthBarBg.rotation = cam.transform.rotation;
            }
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
    /// Vérifie si une unité ennemie est réellement repérée par un éclaireur avec ligne de vue dégagée.
    /// </summary>
    public static bool IsUnitSpottedByTeam(UnitAI target, int observingTeam)
    {
        if (target == null || target.isDead) return false;
        if (target.isCamouflaged) return false;

        for (int i = 0; i < AllLivingUnits.Count; i++)
        {
            UnitAI observer = AllLivingUnits[i];
            if (observer == null || observer.isDead || observer.teamID != observingTeam) continue;

            float maxSight = observer.isRooftopSniper ? 55f : (observer.isMortar ? 25f : 35f);
            float dist = Vector3.Distance(observer.transform.position, target.transform.position);

            if (dist <= maxSight)
            {
                Vector3 start = observer.transform.position + Vector3.up * 1.5f;
                Vector3 end = target.transform.position + Vector3.up * 1.5f;
                Vector3 dir = (end - start);

                RaycastHit[] hits = Physics.RaycastAll(start, dir.normalized, dist, ~0, QueryTriggerInteraction.Ignore);
                bool blocked = false;

                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(observer.transform) || h.collider.transform.IsChildOf(target.transform)) continue;

                    string colName = h.collider.gameObject.name.ToLower();
                    if (colName.Contains("wall") || colName.Contains("mur") || colName.Contains("building") || colName.Contains("batiment"))
                    {
                        if (target.isRooftopSniper && h.distance > dist - 2f) continue;
                        blocked = true;
                        break;
                    }
                }

                if (!blocked) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recherche l'ennemi le plus proche visible dans la ligne de mire ou à portée de mortier.
    /// </summary>
    public UnitAI GetVisibleEnemy()
    {
        UnitAI bestTarget = null;
        float maxRange = isMortar ? 120f : (isRooftopSniper ? (porteeDetection + 20f) : porteeDetection);
        float minDistance = maxRange;

        if (isMortar)
        {
            // Artillerie Mortier : Tir indirect parabolique (15m à 120m)
            for (int i = 0; i < AllLivingUnits.Count; i++)
            {
                UnitAI unit = AllLivingUnits[i];
                if (unit == null || unit == this || unit.isDead || unit.teamID == this.teamID || unit.isCamouflaged) continue;

                float dist = Vector3.Distance(transform.position, unit.transform.position);
                if (dist <= 120f && dist < minDistance)
                {
                    bestTarget = unit;
                    minDistance = dist;
                }
            }
            return bestTarget;
        }

        for (int i = 0; i < AllLivingUnits.Count; i++)
        {
            UnitAI unit = AllLivingUnits[i];
            if (unit == null || unit == this || unit.isDead || unit.teamID == this.teamID || unit.isCamouflaged) continue; // Ignorer soi-même, les morts, ses alliés et les ennemis camouflés !

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

                    // CAS 1 : L'attaquant (this) tire depuis son propre bâtiment (fenêtre, porte ou toit)
                    if ((isGarrisoned || isRooftopSniper || currentBuilding != null) && (hit.collider.GetComponentInParent<BuildingStructure>() == currentBuilding || hit.distance < 2.5f)) continue;

                    // CAS 2 : La cible (unit) est retranchée à une fenêtre ou une porte et l'attaquant (this) lui tire dessus depuis la rue
                    if (unit.isGarrisoned || unit.currentBuilding != null)
                    {
                        BuildingStructure targetBuilding = unit.currentBuilding ?? hit.collider.GetComponentInParent<BuildingStructure>();
                        if (targetBuilding != null && hit.collider.GetComponentInParent<BuildingStructure>() == targetBuilding)
                        {
                            float distToTarget = Vector3.Distance(hit.point, unit.transform.position + Vector3.up * 1.0f);
                            if (distToTarget < 2.5f)
                            {
                                // La balle passe par l'ouverture (fenêtre/porte) où est posté le défenseur
                                continue;
                            }
                        }
                    }

                    // CAS 3 : La cible (unit) est sur un toit (Sniper de toit)
                    if (unit.isRooftopSniper || unit.transform.position.y > 2.5f)
                    {
                        float distToTarget = Vector3.Distance(hit.point, unit.transform.position + Vector3.up * 1.0f);
                        if (distToTarget < 2.5f)
                        {
                            continue;
                        }
                    }

                    if (hit.collider.gameObject.name.Contains("Terrain") || hit.collider.gameObject.name.Contains("Building") || hit.collider.gameObject.name.Contains("City") || hit.collider.gameObject.name.Contains("Polygone") || hit.collider.gameObject.name.Contains("Mur") || hit.collider.gameObject.name.Contains("Wall") || hit.collider.gameObject.name.Contains("Batiment"))
                    {
                        hasLineOfSight = false; // Un mur extérieur opaque bloque la vue
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
        if (isMortar)
        {
            FireMortarShell(target.transform.position);
            return;
        }

        Debug.Log($"<color=orange>[{gameObject.name}] Tire sur {target.gameObject.name} !</color>");
        
        FogOfWarEntity fow = GetComponent<FogOfWarEntity>();
        if (fow != null) fow.NotifyAttack();
        
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
        SpawnMuzzleSmoke(startPos);
        if (!isTank)
        {
            Vector3 casingEjectDir = (transform.right * 0.6f + Vector3.up * 0.9f + -transform.forward * 0.15f).normalized;
            SpawnCasing(startPos, casingEjectDir);
        }

        Vector3 hitDirection = (target.transform.position - transform.position).normalized;
        // Calcul des dégâts équilibrés
        float damageToDeal = isTank ? (isCanonVehicle ? 75f : 150f) : 15f; 

        if (isCamouflaged)
        {
            damageToDeal *= 1.75f;
            isCamouflaged = false;
            Debug.Log($"<color=red><b>[{gameObject.name}] 💥 CRITIQUE D'EMBUSCADE ! Dégâts augmentés (+75%) et rupture du camouflage.</b></color>");
        }

        target.TakeDamage(damageToDeal, hitDirection);
    }
}
