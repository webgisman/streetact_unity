using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace StreetAct.Combat
{
    /// <summary>
    /// Obus d'artillerie de mortier suivant une trajectoire balistique parabolique haute (survol des bâtiments)
    /// et provoquant une explosion massive avec dégâts de zone (AoE Blast).
    /// </summary>
    public class MortarShell : MonoBehaviour
    {
        private Vector3 startPosition;
        private Vector3 targetPosition;
        private float apexHeight = 30f;
        private float flightDuration = 2.2f;
        private float explosionRadius = 6.5f;
        private float maxDamage = 150f;
        private int attackerTeamID = 1;

        private TrailRenderer trailRenderer;

        public static void Launch(Vector3 start, Vector3 target, int teamID, float duration = 2.2f)
        {
            GameObject shellGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            shellGo.name = "MortarShell";
            shellGo.transform.position = start;
            shellGo.transform.localScale = new Vector3(0.4f, 0.6f, 0.4f);

            Destroy(shellGo.GetComponent<Collider>());

            // Matériau obus sombre
            Renderer rend = shellGo.GetComponent<Renderer>();
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (rend != null && unlitShader != null)
            {
                Material shellMat = new Material(unlitShader);
                if (shellMat.HasProperty("_BaseColor")) shellMat.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.2f));
                if (shellMat.HasProperty("_Color")) shellMat.SetColor("_Color", new Color(0.2f, 0.2f, 0.2f));
                rend.sharedMaterial = shellMat;
            }

            MortarShell shellComp = shellGo.AddComponent<MortarShell>();
            shellComp.Initialize(start, target, teamID, duration);
        }

        public void Initialize(Vector3 start, Vector3 target, int teamID, float duration)
        {
            this.startPosition = start;
            this.targetPosition = target;
            this.attackerTeamID = teamID;
            this.flightDuration = Mathf.Max(1.2f, duration);

            float horizontalDist = Vector3.Distance(new Vector3(start.x, 0, start.z), new Vector3(target.x, 0, target.z));
            this.apexHeight = Mathf.Clamp(horizontalDist * 0.35f, 22f, 50f);

            // Traînée de fumée incandescente
            trailRenderer = gameObject.AddComponent<TrailRenderer>();
            trailRenderer.time = 0.8f;
            trailRenderer.startWidth = 0.35f;
            trailRenderer.endWidth = 0.05f;
            
            Shader trailShader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Unlit/Color");
            if (trailShader != null)
            {
                Material trailMat = new Material(trailShader);
                trailMat.color = new Color(1f, 0.5f, 0.1f, 0.8f);
                trailRenderer.material = trailMat;
            }

            Gradient grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(1f, 0.6f, 0.1f), 0.0f), new GradientColorKey(new Color(0.4f, 0.4f, 0.4f), 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.9f, 0.0f), new GradientAlphaKey(0.0f, 1.0f) }
            );
            trailRenderer.colorGradient = grad;

            StartCoroutine(FlightCoroutine());
        }

        private IEnumerator FlightCoroutine()
        {
            float elapsed = 0f;
            Vector3 prevPos = startPosition;

            while (elapsed < flightDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / flightDuration);

                // Trajectoire parabolique : Lerp horizontal + courbe en cloche verticale
                Vector3 currentHorizontal = Vector3.Lerp(startPosition, targetPosition, t);
                float arcHeight = 4f * apexHeight * t * (1f - t);
                Vector3 newPos = currentHorizontal + Vector3.up * arcHeight;

                transform.position = newPos;

                // Orientation vers le sens du mouvement
                Vector3 velocity = newPos - prevPos;
                if (velocity.sqrMagnitude > 0.001f)
                {
                    transform.rotation = Quaternion.LookRotation(velocity);
                }

                prevPos = newPos;
                yield return null;
            }

            transform.position = targetPosition;
            Detonate();
        }

        private void Detonate()
        {
            // 1. Son d'explosion de mortier
            AudioClip boomClip = ProceduralAudioBuilder.CreateMortarExplosionSound();
            if (boomClip != null) AudioSource.PlayClipAtPoint(boomClip, targetPosition, 1.0f);

            // 2. Effet visuel d'explosion massive
            SpawnExplosionVFX(targetPosition);

            // 3. Dégâts de zone (AoE Splash) sur toutes les unités
            ApplySplashDamage(targetPosition);

            Destroy(gameObject, 0.05f);
        }

        private void SpawnExplosionVFX(Vector3 pos)
        {
            // Cratère / Flash lumineux au sol
            GameObject blastRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            blastRing.name = "MortarBlastRing";
            blastRing.transform.position = pos + Vector3.up * 0.05f;
            blastRing.transform.localScale = new Vector3(explosionRadius * 1.5f, 0.05f, explosionRadius * 1.5f);
            Destroy(blastRing.GetComponent<Collider>());

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
            if (unlit != null)
            {
                Material ringMat = new Material(unlit);
                if (ringMat.HasProperty("_BaseColor")) ringMat.SetColor("_BaseColor", new Color(1f, 0.35f, 0.05f, 0.8f));
                if (ringMat.HasProperty("_Color")) ringMat.SetColor("_Color", new Color(1f, 0.35f, 0.05f, 0.8f));
                blastRing.GetComponent<MeshRenderer>().sharedMaterial = ringMat;
            }

            // Flash WarFX si disponible
            GameObject muzzleFlash = Resources.Load<GameObject>("WarFX/MuzzleFlash");
            if (muzzleFlash != null)
            {
                GameObject fx = Instantiate(muzzleFlash, pos + Vector3.up * 1f, Quaternion.identity);
                fx.transform.localScale = Vector3.one * 8f; // Énorme explosion
                Destroy(fx, 2.0f);
            }

            Destroy(blastRing, 1.2f);
        }

        private void ApplySplashDamage(Vector3 center)
        {
            // Camera Shake !
            if (Camera.main != null)
            {
                TacticalCamera tCam = Camera.main.GetComponent<TacticalCamera>();
                if (tCam != null)
                {
                    // L'intensité dépend de la distance par rapport à l'épicentre
                    float distToCam = Vector3.Distance(center, Camera.main.transform.position);
                    float shakeForce = Mathf.Clamp01(1f - (distToCam / 150f)) * 0.8f;
                    tCam.ShakeCamera(shakeForce, 0.6f);
                }
            }

            List<UnitAI> potentialTargets = new List<UnitAI>(UnitAI.AllLivingUnits);
            for (int i = 0; i < potentialTargets.Count; i++)
            {
                UnitAI unit = potentialTargets[i];
                if (unit == null || unit.isDead) continue;

                float dist = Vector3.Distance(center, unit.transform.position);
                if (dist <= explosionRadius)
                {
                    // Chute des dégâts linéaire selon la distance à l'épicentre
                    float damagePercent = 1f - (dist / explosionRadius);
                    float damage = Mathf.Lerp(30f, maxDamage, damagePercent);

                    Vector3 blastDir = (unit.transform.position - center).normalized;
                    if (blastDir == Vector3.zero) blastDir = Vector3.up;

                    Debug.Log($"<color=red><b>💥 IMPACT MORTIER : {unit.gameObject.name} prend {damage:F0} dégâts de zone (Distance: {dist:F1}m) !</b></color>");
                    unit.TakeDamage(damage, blastDir);
                }
            }

            // Dégâts sur l'environnement (DestructibleEnvironment)
            Collider[] colliders = Physics.OverlapSphere(center, explosionRadius);
            HashSet<DestructibleEnvironment> damagedEnvs = new HashSet<DestructibleEnvironment>();
            foreach (var col in colliders)
            {
                DestructibleEnvironment env = col.GetComponentInParent<DestructibleEnvironment>();
                if (env != null && !damagedEnvs.Contains(env))
                {
                    damagedEnvs.Add(env);
                    Vector3 closestPt = col.ClosestPoint(center);
                    float dist = Vector3.Distance(center, closestPt);
                    float damagePercent = Mathf.Clamp01(1f - (dist / explosionRadius));
                    float damage = Mathf.Lerp(100f, maxDamage * 2.5f, damagePercent); // Dégâts lourds de démolition (100 à 500 dégâts par obus)
                    env.TakeDamage(damage);
                }
            }

            // Dégâts sur les barricades routières existantes
            RoadBarrier.ApplySplashDamageToBarriers(center, explosionRadius, maxDamage);
        }
    }
}
