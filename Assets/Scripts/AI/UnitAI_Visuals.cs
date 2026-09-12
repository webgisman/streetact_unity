using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class UnitAI
{
    // ==========================================
    // EFFETS VISUELS ET PARTICULES PROCÉDURALES
    // ==========================================

    private static Material cachedBulletTracerMat;
    private static Material cachedTankTracerMat;
    private static Material cachedBloodMat;
    private static Material cachedSparksMat;
    private static Material cachedSmokeMat;
    private static Material cachedAuraMat;

    /// <summary>
    /// Crée un traceur lumineux pour simuler le projectile (optimisé sans allocation de matériau).
    /// </summary>
    private void SpawnTracer(Vector3 start, Vector3 end, bool isTankShot)
    {
        GameObject lineObj = new GameObject("Tracer");
        LineRenderer line = lineObj.AddComponent<LineRenderer>();
        
        line.startWidth = isTankShot ? 0.4f : 0.05f;
        line.endWidth = isTankShot ? 0.1f : 0.01f;
        
        if (isTankShot)
        {
            if (cachedTankTracerMat == null)
            {
                Shader tracerShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (tracerShader != null)
                {
                    cachedTankTracerMat = new Material(tracerShader);
                    cachedTankTracerMat.color = new Color(1f, 0.5f, 0f);
                }
            }
            line.sharedMaterial = cachedTankTracerMat;
        }
        else
        {
            if (cachedBulletTracerMat == null)
            {
                Shader tracerShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (tracerShader != null)
                {
                    cachedBulletTracerMat = new Material(tracerShader);
                    cachedBulletTracerMat.color = Color.yellow;
                }
            }
            line.sharedMaterial = cachedBulletTracerMat;
        }
        
        line.SetPosition(0, start);
        line.SetPosition(1, end);
        
        Destroy(lineObj, isTankShot ? 0.2f : 0.06f);
    }

    /// <summary>
    /// Système de particules pour le sang de l'infanterie.
    /// </summary>
    private void SpawnBloodEffect(Vector3 position, Vector3 direction)
    {
        GameObject bloodObj = new GameObject("BloodEffect");
        bloodObj.transform.position = position;
        bloodObj.transform.rotation = Quaternion.LookRotation(direction);
        
        ParticleSystem ps = bloodObj.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 0.5f;
        main.startLifetime = 0.4f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
        // Léger jitter de teinte pour casser la répétitivité visuelle sur les tirs en rafale
        float bloodShade = Random.Range(-0.08f, 0.08f);
        main.startColor = new Color(Mathf.Clamp01(0.6f + bloodShade), 0f, 0f, 1f);
        main.maxParticles = 100;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 20, 40) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 25f;
        shape.radius = 0.1f;

        var gravity = ps.forceOverLifetime;
        gravity.enabled = true;
        gravity.y = new ParticleSystem.MinMaxCurve(-15f);

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (cachedBloodMat == null)
        {
            Shader bloodShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (bloodShader != null) cachedBloodMat = new Material(bloodShader);
        }
        renderer.sharedMaterial = cachedBloodMat;
        
        ps.Play();
        Destroy(bloodObj, 1.5f);
    }

    /// <summary>
    /// Système de particules métalliques (étincelles) pour les chars.
    /// </summary>
    private void SpawnSparksEffect(Vector3 position, Vector3 direction)
    {
        GameObject sparksObj = new GameObject("SparksEffect");
        sparksObj.transform.position = position;
        sparksObj.transform.rotation = Quaternion.LookRotation(direction);
        
        ParticleSystem ps = sparksObj.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        
        var main = ps.main;
        main.duration = 0.5f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 15f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
        float sparkShade = Random.Range(-0.1f, 0.1f);
        main.startColor = new Color(1f, Mathf.Clamp01(0.7f + sparkShade), 0.1f, 1f);
        main.maxParticles = 50;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 15, 30) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = 0.2f;
        
        var collision = ps.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.bounce = 0.5f;

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (cachedSparksMat == null)
        {
            Shader sparksShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (sparksShader != null) cachedSparksMat = new Material(sparksShader);
        }
        renderer.sharedMaterial = cachedSparksMat;

        ps.Play();
        Destroy(sparksObj, 1.5f);
    }

    private static Material cachedMuzzleSmokeMat;
    private static Material cachedCasingMat;

    /// <summary>
    /// Petit puff de fumée de tir qui se dissipe vite au niveau du canon (visuel manquant par rapport
    /// à la documentation d'architecture : gunshot sonore existait déjà, pas la fumée).
    /// </summary>
    private void SpawnMuzzleSmoke(Vector3 position)
    {
        GameObject smokeObj = new GameObject("MuzzleSmoke");
        smokeObj.transform.position = position;

        ParticleSystem ps = smokeObj.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.duration = 0.3f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.1f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
        main.startColor = new Color(0.75f, 0.75f, 0.75f, 0.5f);
        main.maxParticles = 12;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 4, 7) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 12f;
        shape.radius = 0.05f;

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.gray, 1f) },
            new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (cachedMuzzleSmokeMat == null) cachedMuzzleSmokeMat = SafeMaterialFactory.CreateUnlit(Color.white);
        renderer.sharedMaterial = cachedMuzzleSmokeMat;

        ps.Play();
        Destroy(smokeObj, 0.6f);
    }

    /// <summary>
    /// Éjecte une petite douille physique du côté de l'arme (visuel manquant par rapport à la
    /// documentation d'architecture). Simple capsule primitive avec rotation aléatoire, pas de
    /// dépendance à un asset externe.
    /// </summary>
    private void SpawnCasing(Vector3 position, Vector3 ejectDirection)
    {
        GameObject casing = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        casing.name = "Casing";
        casing.transform.position = position;
        casing.transform.localScale = new Vector3(0.02f, 0.045f, 0.02f);
        casing.transform.rotation = Random.rotation;

        Collider col = casing.GetComponent<Collider>();
        if (col != null) Destroy(col);

        if (cachedCasingMat == null) cachedCasingMat = SafeMaterialFactory.CreateLit(new Color(0.78f, 0.66f, 0.28f));
        casing.GetComponent<MeshRenderer>().sharedMaterial = cachedCasingMat;

        Rigidbody rb = casing.AddComponent<Rigidbody>();
        rb.mass = 0.02f;
        rb.linearVelocity = ejectDirection * Random.Range(1.5f, 3f);
        rb.angularVelocity = Random.insideUnitSphere * 12f;

        Destroy(casing, 2f);
    }

    // ==========================================
    // REJEU RÉSEAU (2026-09-11) : mêmes effets que ShootAt/TakeDamage, mais COSMÉTIQUE UNIQUEMENT.
    // ==========================================
    // Avant ce correctif, le rejeu multijoueur (MultiplayerMatchController.PlaySnapshotsBody)
    // n'appelait ni ShootAt ni TakeDamage — seulement SetNetworkHealth/SetNetworkAnimState, qui ne
    // pilotent que la barre de vie et le booléen "IsShooting" de l'Animator. Un combat entier se
    // déroulait donc sans le moindre flash, traceur, son de tir, impact ou animation de hit : les
    // unités mouraient en silence. Ces deux méthodes REPRODUISENT les effets de ShootAt/TakeDamage
    // à l'identique, sans jamais recalculer ni réappliquer de dégâts (déjà appliqués côté serveur
    // autoritaire, voir TacticalResolver puis SetNetworkHealth) — rejouer ShootAt/TakeDamage tels
    // quels aurait doublé les dégâts. Le mortier est délibérément exclu (voir MatchSessionManager_
    // CombatPure.ApplyAreaDamage : l'événement Shot associé porte un faux unitId "mortar" et un tick
    // toujours à 0, aucune unité tireuse réelle ni instant réel à rejouer fidèlement pour l'instant).

    /// <summary>Rejoue les effets cosmétiques d'un tir direct (infanterie/char/véhicule-canon)
    /// pendant la lecture d'un snapshot réseau. Ne touche jamais aux PV ni n'appelle TakeDamage.</summary>
    public void PlayNetworkShotEffects(Vector3 targetPos)
    {
        if (isMortar) return; // voir note ci-dessus : pas de rejeu fidèle possible pour l'instant

        // 2026-09-12 (retour joueur : "il faut que le joueur voie les unités ennemies pendant un
        // combat, et qu'elles soient aussi visibles sur le radar, pas seulement en mode
        // multijoueur") : ShootAt (le VRAI tir, solo/Conquête "vivante") appelle
        // FogOfWarEntity.NotifyAttack() ici, qui déclenche l'onde rouge de TacticalRadarUI —
        // seule alerte qui indique OÙ un combat a lieu sans que le joueur ait déjà l'œil dessus.
        // PlayNetworkShotEffects, le rejeu PUREMENT COSMÉTIQUE utilisé par le multijoueur
        // (Deathmatch/Zone de Contrôle), reproduit tous les autres effets (son, traceur, flash)
        // mais oubliait CELUI-CI : le radar restait muet pendant un échange de tirs multijoueur,
        // même si le tireur ET la cible étaient déjà visibles/existants côté client.
        FogOfWarEntity fow = GetComponent<FogOfWarEntity>();
        if (fow != null) fow.NotifyAttack();

        if (combatAudioSource != null && combatAudioSource.clip != null)
        {
            combatAudioSource.pitch = Random.Range(0.9f, 1.1f);
            combatAudioSource.Stop();
            if (combatAudioSource.clip.name.Contains("assault_rifle_gunshot"))
                combatAudioSource.time = 0.05f;
            combatAudioSource.Play();
        }

        Vector3 startPos;
        if (isTank && cannonBone != null)
            startPos = cannonBone.position;
        else if (equippedWeapon != null)
            startPos = equippedWeapon.transform.position + transform.forward * 0.6f + Vector3.up * 0.05f;
        else
            startPos = transform.position + Vector3.up * 1.2f;

        Vector3 endPos = targetPos + Vector3.up * 1.2f;

        if (isTank)
        {
            if (muzzleFlashPrefab != null && cannonBone != null)
            {
                GameObject mf = Instantiate(muzzleFlashPrefab, startPos, cannonBone.rotation);
                mf.transform.localScale = Vector3.one * 5f;
                Destroy(mf, 1.0f);
            }
        }
        else if (muzzleFlashPrefab != null && equippedWeapon != null)
        {
            GameObject mf = Instantiate(muzzleFlashPrefab, startPos, equippedWeapon.transform.rotation);
            mf.transform.SetParent(equippedWeapon.transform);
            Destroy(mf, 0.1f);
        }

        SpawnTracer(startPos, endPos, isTank);
        SpawnMuzzleSmoke(startPos);
        if (!isTank)
        {
            Vector3 casingEjectDir = (transform.right * 0.6f + Vector3.up * 0.9f + -transform.forward * 0.15f).normalized;
            SpawnCasing(startPos, casingEjectDir);
        }
    }

    /// <summary>Rejoue les effets cosmétiques d'un impact reçu (sang/étincelles + animation de hit)
    /// pendant la lecture d'un snapshot réseau. Ne touche jamais aux PV ni ne déclenche Die() — voir
    /// SetNetworkHealth/ApplyNetworkDeath, déjà appelés séparément par le rejeu pour cet effet.</summary>
    public void PlayNetworkHitReaction(Vector3 hitDirection)
    {
        if (hitDirection == default) hitDirection = -transform.forward;

        if (isTank)
        {
            SpawnSparksEffect(transform.position + Vector3.up * 1.5f, hitDirection);
        }
        else
        {
            SpawnBloodEffect(transform.position + Vector3.up * 1.2f, hitDirection);
        }

        if (bulletImpactPrefab != null)
        {
            GameObject impact = Instantiate(bulletImpactPrefab, transform.position + Vector3.up * 1.2f, Quaternion.LookRotation(-hitDirection));
            Destroy(impact, 2f);
        }

        // Comme TakeDamage : pas de déclencheur "Hit" si ce tir est le coup de grâce (ApplyNetworkDeath,
        // appelé séparément par le rejeu, a déjà sa propre séquence de mort — pas la peine de la cumuler).
        if (!isDead && animator != null && !isTank && Time.time - lastHitAnimTime > 1.2f)
        {
            lastHitAnimTime = Time.time;
            animator.SetTrigger("Hit");
        }
    }

    /// <summary>
    /// Fumée noire persistante lors de la destruction d'un char.
    /// </summary>
    private void SpawnBlackSmoke()
    {
        GameObject smokeObj = new GameObject("BlackSmoke");
        smokeObj.transform.SetParent(transform);
        smokeObj.transform.localPosition = new Vector3(0, 2f, 0);
        
        ParticleSystem ps = smokeObj.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        
        var main = ps.main;
        main.duration = 5f;
        main.loop = true;
        main.startLifetime = 4f;
        main.startSpeed = 2f;
        main.startSize = new ParticleSystem.MinMaxCurve(1f, 3f);
        main.startColor = new Color(0.1f, 0.1f, 0.1f, 0.8f);
        main.maxParticles = 100;
        main.playOnAwake = true;
        
        var emission = ps.emission;
        emission.rateOverTime = 15f;
        
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 20f;
        shape.radius = 1.5f;

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (cachedSmokeMat == null)
        {
            Shader smokeShader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                              ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply")
                              ?? Shader.Find("Sprites/Default");
            if (smokeShader != null) cachedSmokeMat = new Material(smokeShader);
        }
        renderer.sharedMaterial = cachedSmokeMat;
        
        ps.Play();
    }

    private GameObject coverAura;

    public void UpdateCoverAura()
    {
        bool inCover = isPlayerControlled && (isGarrisoned || isRooftopSniper || isCamouflaged || isGuarding);

        if (inCover && coverAura == null && !isTank)
        {
            coverAura = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(coverAura.GetComponent<Collider>());
            coverAura.transform.SetParent(this.transform);
            coverAura.transform.localPosition = new Vector3(0, 0.02f, 0); 
            coverAura.transform.localScale = new Vector3(1.3f, 0.05f, 1.3f);
            
            if (cachedAuraMat == null)
            {
                cachedAuraMat = SafeMaterialFactory.CreateUnlit(new Color(0f, 0.7f, 1f, 0.5f));
            }
            coverAura.GetComponent<MeshRenderer>().sharedMaterial = cachedAuraMat;
        }

        if (coverAura != null)
        {
            coverAura.SetActive(inCover && !isDead);
            
            if (inCover && !isDead)
            {
                float pulse = 1.25f + Mathf.Sin(Time.time * 3f) * 0.1f;
                coverAura.transform.localScale = new Vector3(pulse, 0.05f, pulse);
            }
        }
    }
}
