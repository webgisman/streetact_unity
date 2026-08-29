using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Radar Tactique Moderne & Esthétique (Military HUD Style).
/// Rendu direct OnGUI avec cercle sonar, faisceau rotatif, réticule militaire,
/// affichage en temps réel des alliés (bleu), ennemis détectés (rouge) et ondes de tirs sonars.
/// </summary>
public class TacticalRadarUI : MonoBehaviour
{
    private static TacticalRadarUI _instance;
    public static TacticalRadarUI Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindAnyObjectByType<TacticalRadarUI>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("TacticalRadarUI");
                    _instance = go.AddComponent<TacticalRadarUI>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
        private set => _instance = value;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInitialize()
    {
        if (Instance != null)
        {
            Debug.Log("<color=green>[TacticalRadarUI] 🛰️ Radar HUD Militaire initialisé avec succès !</color>");
        }
    }

    public class RadarPing
    {
        public Vector3 worldPosition;
        public float remainingTime;
    }

    private readonly List<RadarPing> activePings = new List<RadarPing>();

    [Header("Radar Settings")]
    public float radarRange = 90f;  // Portée de détection en mètres
    public float radarSize = 120f;  // Diamètre optimisé pour s'intégrer au tableau de bord sans empiéter sur Fin de Tour

    private Texture2D radarBgTex;
    private Texture2D borderTex;
    private Texture2D gridLineTex;
    private Texture2D circleRingTex;
    private Texture2D sweepBeamTex;
    private Texture2D allyBlipTex;
    private Texture2D enemyBlipTex;
    private Texture2D pingTex;
    private Texture2D centerDotTex;
    private Texture2D selectedBlipRingTex;

    private float scanAngle = 0f;

    void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (_instance != this)
        {
            Destroy(gameObject);
            return;
        }

        CreateRadarTextures();
    }

    private void CreateRadarTextures()
    {
        // Chrome du radar recoloré en kaki/laiton pour matcher le thème "commando" du reste de
        // l'UI (Assets/UI/Theme.tss) — auparavant un néon vert/cyan cyberpunk sans rapport avec
        // le reste du HUD. Les couleurs des contacts (allié/ennemi) reprennent volontairement les
        // mêmes teintes bleu/rouge que --color-team1/--color-team2 dans Theme.tss : c'est la même
        // logique d'identité joueur/ennemi qu'ailleurs dans l'UI, pas une palette radar à part.

        // 1. Fond semi-transparent sombre kaki (proche de --color-bg-dark)
        radarBgTex = new Texture2D(1, 1);
        radarBgTex.SetPixel(0, 0, new Color(0.078f, 0.071f, 0.047f, 0.9f));
        radarBgTex.Apply();

        // 2. Bordure laiton (--color-accent)
        borderTex = new Texture2D(1, 1);
        borderTex.SetPixel(0, 0, new Color(0.776f, 0.588f, 0.180f, 1f));
        borderTex.Apply();

        // 3. Réticule et axes
        gridLineTex = new Texture2D(1, 1);
        gridLineTex.SetPixel(0, 0, new Color(0.776f, 0.588f, 0.180f, 0.3f));
        gridLineTex.Apply();

        // 4. Cercles concentriques de distance (Texture procédurale circulaire 128x128)
        circleRingTex = new Texture2D(128, 128);
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 128; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(64, 64));
                // Cercles à 33% et 66% et 98%
                bool onRing1 = Mathf.Abs(dist - 21) < 1.0f;
                bool onRing2 = Mathf.Abs(dist - 42) < 1.0f;
                bool onOuter = Mathf.Abs(dist - 62) < 1.2f;

                if (onOuter)
                    circleRingTex.SetPixel(x, y, new Color(0.776f, 0.588f, 0.180f, 0.6f));
                else if (onRing1 || onRing2)
                    circleRingTex.SetPixel(x, y, new Color(0.776f, 0.588f, 0.180f, 0.22f));
                else
                    circleRingTex.SetPixel(x, y, Color.clear);
            }
        }
        circleRingTex.Apply();

        // 5. Faisceau de balayage sonar (laiton clair)
        sweepBeamTex = new Texture2D(1, 1);
        sweepBeamTex.SetPixel(0, 0, new Color(0.871f, 0.682f, 0.282f, 0.75f));
        sweepBeamTex.Apply();

        // 6. Blip Allié Tactique (bleu --color-team1, même identité que les badges d'équipe)
        allyBlipTex = new Texture2D(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f));
                if (dist < 4.5f)
                {
                    allyBlipTex.SetPixel(x, y, Color.white);
                }
                else if (dist < 9.5f)
                {
                    float alpha = Mathf.SmoothStep(1f, 0f, (dist - 4.5f) / 5f);
                    allyBlipTex.SetPixel(x, y, new Color(0.282f, 0.502f, 0.659f, alpha));
                }
                else if (dist < 15.5f)
                {
                    float glow = Mathf.SmoothStep(0.5f, 0f, (dist - 9.5f) / 6f);
                    allyBlipTex.SetPixel(x, y, new Color(0.282f, 0.502f, 0.659f, glow * 0.4f));
                }
                else
                {
                    allyBlipTex.SetPixel(x, y, Color.clear);
                }
            }
        }
        allyBlipTex.Apply();

        // 7. Blip Ennemi Tactique (rouge --color-team2, même identité que les badges d'équipe)
        enemyBlipTex = new Texture2D(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f));
                if (dist < 4.5f)
                {
                    enemyBlipTex.SetPixel(x, y, new Color(1f, 0.9f, 0.8f, 1f));
                }
                else if (dist < 10.0f)
                {
                    float alpha = Mathf.SmoothStep(1f, 0f, (dist - 4.5f) / 5.5f);
                    enemyBlipTex.SetPixel(x, y, new Color(0.737f, 0.243f, 0.188f, alpha));
                }
                else if (dist < 15.5f)
                {
                    float glow = Mathf.SmoothStep(0.6f, 0f, (dist - 10f) / 5.5f);
                    enemyBlipTex.SetPixel(x, y, new Color(0.737f, 0.243f, 0.188f, glow * 0.4f));
                }
                else
                {
                    enemyBlipTex.SetPixel(x, y, Color.clear);
                }
            }
        }
        enemyBlipTex.Apply();

        // 8. Onde sonore d'attaque (laiton clair, --color-accent-hover)
        pingTex = new Texture2D(1, 1);
        pingTex.SetPixel(0, 0, new Color(0.871f, 0.682f, 0.282f, 0.9f));
        pingTex.Apply();

        // 9. Point central (laiton clair)
        centerDotTex = new Texture2D(1, 1);
        centerDotTex.SetPixel(0, 0, new Color(0.871f, 0.682f, 0.282f, 1f));
        centerDotTex.Apply();

        // 10. Réticule et anneau de sélection d'unité (laiton, --color-accent)
        selectedBlipRingTex = new Texture2D(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f));
                bool onRing = Mathf.Abs(dist - 11.5f) < 1.4f;
                bool isCrosshair = (dist > 7.5f && dist < 14.5f) && (Mathf.Abs(x - 15.5f) < 1.2f || Mathf.Abs(y - 15.5f) < 1.2f);

                if (onRing || isCrosshair)
                {
                    selectedBlipRingTex.SetPixel(x, y, new Color(0.871f, 0.682f, 0.282f, 0.95f));
                }
                else if (Mathf.Abs(dist - 11.5f) < 2.5f)
                {
                    selectedBlipRingTex.SetPixel(x, y, new Color(0.776f, 0.588f, 0.180f, 0.4f));
                }
                else
                {
                    selectedBlipRingTex.SetPixel(x, y, Color.clear);
                }
            }
        }
        selectedBlipRingTex.Apply();
    }

    void Update()
    {
        // Rotation fluide et calme du balayage sonar (1 tour toutes les 10 secondes)
        scanAngle = (scanAngle + Time.deltaTime * 36f) % 360f;

        // Mise à jour des pings d'attaque
        for (int i = activePings.Count - 1; i >= 0; i--)
        {
            activePings[i].remainingTime -= Time.deltaTime;
            if (activePings[i].remainingTime <= 0)
            {
                activePings.RemoveAt(i);
            }
        }
    }

    public void PingAttack(Vector3 worldPos)
    {
        activePings.Add(new RadarPing
        {
            worldPosition = worldPos,
            remainingTime = 3.2f
        });
    }

    /// <summary>
    /// Position Y (en pixels écran absolus) du bord bas du panneau radar.
    /// Utilisé par TacticalBottomBarScreen pour empiler les boutons sous le radar sans chevauchement.
    /// </summary>
    public static float BottomEdgeScreenY { get; private set; } = 0f;

    void OnGUI()
    {
        if (GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive) { BottomEdgeScreenY = 0f; return; }

        // En vue 3D Action, masquer le radar pour garder un écran 100% épuré et immersif
        if (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Action) { BottomEdgeScreenY = 0f; return; }

        GUI.depth = -100;
        if (radarBgTex == null) CreateRadarTextures();

        Matrix4x4 origMat = GUI.matrix;
        float uiScale = 1.0f;
        if (UIScreenManager.Instance != null && UIScreenManager.Instance.RootVisualElement != null)
        {
            float layoutHeight = UIScreenManager.Instance.RootVisualElement.layout.height;
            // On calcule l'échelle exacte utilisée par UI Toolkit pour s'aligner au pixel près.
            if (layoutHeight > 0)
                uiScale = Screen.height / layoutHeight;
        }
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

        float virtualW = Screen.width / uiScale;
        // Position : Encastré en haut à droite dans le tableau de bord
        float pad = 8f;
        float rX = virtualW - radarSize - pad;
        float rY = pad;
        float totalH = radarSize + 16f;
        Rect radarRect = new Rect(rX, rY, radarSize, totalH);
        BottomEdgeScreenY = (rY + totalH) * uiScale;

        // 1. Panneau de fond
        GUI.DrawTexture(radarRect, radarBgTex);

        // 2. Coins tactiques biseautés (Style HUD Militaire)
        float cornerLen = 8f;
        float bThick = 2f;
        // Haut Gauche
        GUI.DrawTexture(new Rect(rX, rY, cornerLen, bThick), borderTex);
        GUI.DrawTexture(new Rect(rX, rY, bThick, cornerLen), borderTex);
        // Haut Droite
        GUI.DrawTexture(new Rect(rX + radarSize - cornerLen, rY, cornerLen, bThick), borderTex);
        GUI.DrawTexture(new Rect(rX + radarSize - bThick, rY, bThick, cornerLen), borderTex);
        // Bas Gauche
        GUI.DrawTexture(new Rect(rX, rY + totalH - bThick, cornerLen, bThick), borderTex);
        GUI.DrawTexture(new Rect(rX, rY + totalH - cornerLen, bThick, cornerLen), borderTex);
        // Bas Droite
        GUI.DrawTexture(new Rect(rX + radarSize - cornerLen, rY + totalH - bThick, cornerLen, bThick), borderTex);
        GUI.DrawTexture(new Rect(rX + radarSize - bThick, rY + totalH - cornerLen, bThick, cornerLen), borderTex);

        // 3. En-tête HUD
        GUIStyle headerStyle = new GUIStyle(GUI.skin.label);
        headerStyle.fontSize = 9;
        headerStyle.fontStyle = FontStyle.Bold;
        headerStyle.alignment = TextAnchor.UpperCenter;
        headerStyle.normal.textColor = new Color(0.871f, 0.682f, 0.282f, 1f);
        GUI.Label(new Rect(rX, rY + 2, radarSize, 14), "RADAR 90M", headerStyle);

        Vector2 center = new Vector2(rX + radarSize * 0.5f, rY + radarSize * 0.5f + 2f);
        float radius = (radarSize * 0.5f) - 12f;

        // 4. Cercles de portée concentriques
        if (circleRingTex != null)
        {
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), circleRingTex);
        }

        // 5. Réticule central
        GUI.DrawTexture(new Rect(center.x - radius, center.y, radius * 2f, 1f), gridLineTex);
        GUI.DrawTexture(new Rect(center.x, center.y - radius, 1f, radius * 2f), gridLineTex);

        // 6. Ligne de balayage sonar rotative
        float rad = scanAngle * Mathf.Deg2Rad;
        Vector2 sweepEnd = center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
        DrawLine(center, sweepEnd, sweepBeamTex, 1.5f);

        // 7. Orientation par rapport à la caméra
        Camera cam = Camera.main;
        Vector3 camCenter = Vector3.zero;
        Vector3 camForward = Vector3.forward;
        Vector3 camRight = Vector3.right;

        if (cam != null)
        {
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (groundPlane.Raycast(ray, out float enter))
            {
                camCenter = ray.GetPoint(enter);
            }
            else
            {
                camCenter = cam.transform.position;
            }

            camForward = cam.transform.forward;
            camForward.y = 0;
            if (camForward.sqrMagnitude > 0.001f) camForward.Normalize();
            else camForward = Vector3.forward;

            camRight = cam.transform.right;
            camRight.y = 0;
            if (camRight.sqrMagnitude > 0.001f) camRight.Normalize();
            else camRight = Vector3.right;
        }

        // Point central (Caméra)
        GUI.DrawTexture(new Rect(center.x - 2.5f, center.y - 2.5f, 5f, 5f), centerDotTex);

        int allyCount = 0;
        int enemyDetectedCount = 0;

        // 8. DESSIN DES UNITÉS EN VIE
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI unit = UnitAI.AllLivingUnits[i];
            if (unit == null || !unit || unit.gameObject == null || unit.isDead) continue;

            if (unit.isPlayerControlled) allyCount++;
            else enemyDetectedCount++;

            Vector3 worldDelta = unit.transform.position - camCenter;
            worldDelta.y = 0;

            float localX = Vector3.Dot(worldDelta, camRight);
            float localY = Vector3.Dot(worldDelta, camForward);

            Vector2 screenOffset = new Vector2(
                (localX / radarRange) * radius,
                (-localY / radarRange) * radius
            );

            if (screenOffset.magnitude > radius)
            {
                screenOffset = screenOffset.normalized * radius;
            }

            TacticalPathManager pathManager = TacticalPathManager.Instance;
            bool isSelectedUnit = (pathManager != null && pathManager.uniteSelectionnee == unit.gameObject);

            Vector2 blipPos = center + screenOffset;
            float blipSize = isSelectedUnit ? 13f : (unit.isPlayerControlled ? 9f : 11f);
            Texture2D tex = unit.isPlayerControlled ? allyBlipTex : enemyBlipTex;

            // Halo et réticule de sélection holographique pulsant sur le radar
            if (isSelectedUnit && selectedBlipRingTex != null)
            {
                float pulseRingSize = 22f + Mathf.Sin(Time.time * 7f) * 4f;
                GUI.DrawTexture(new Rect(blipPos.x - pulseRingSize * 0.5f, blipPos.y - pulseRingSize * 0.5f, pulseRingSize, pulseRingSize), selectedBlipRingTex);
            }

            GUI.DrawTexture(new Rect(blipPos.x - blipSize * 0.5f, blipPos.y - blipSize * 0.5f, blipSize, blipSize), tex);
        }

        // 9. DESSIN DES ONDES DE TIRS SONARS
        for (int i = 0; i < activePings.Count; i++)
        {
            var p = activePings[i];
            Vector3 worldDelta = p.worldPosition - camCenter;
            worldDelta.y = 0;

            float localX = Vector3.Dot(worldDelta, camRight);
            float localY = Vector3.Dot(worldDelta, camForward);

            Vector2 screenOffset = new Vector2(
                (localX / radarRange) * radius,
                (-localY / radarRange) * radius
            );

            if (screenOffset.magnitude > radius) screenOffset = screenOffset.normalized * radius;

            Vector2 pingCenter = center + screenOffset;
            float pulseScale = (3.2f - p.remainingTime) * 8f + 8f;

            GUI.DrawTexture(new Rect(pingCenter.x - pulseScale * 0.5f, pingCenter.y - pulseScale * 0.5f, pulseScale, pulseScale), pingTex);
        }

        // 10. Pied de page avec compteur d'unités HUD
        GUIStyle footerStyle = new GUIStyle(GUI.skin.label);
        footerStyle.fontSize = 10;
        footerStyle.fontStyle = FontStyle.Bold;
        footerStyle.alignment = TextAnchor.MiddleCenter;
        footerStyle.normal.textColor = Color.white;
        string statusText = $"ALLIÉS: {allyCount}  |  CONTACTS: {enemyDetectedCount}";
        GUI.Label(new Rect(rX, rY + radarSize + 2f, radarSize, 18), statusText, footerStyle);

        GUI.matrix = origMat;
    }

    private void DrawLine(Vector2 pointA, Vector2 pointB, Texture2D lineTex, float width)
    {
        Matrix4x4 savedMatrix = GUI.matrix;
        float angle = Mathf.Atan2(pointB.y - pointA.y, pointB.x - pointA.x) * Mathf.Rad2Deg;
        float length = Vector2.Distance(pointA, pointB);

        // Appliquer la translation et la rotation dans l'espace virtuel local pour conserver le scaling UI
        GUI.matrix = savedMatrix * Matrix4x4.TRS(pointA, Quaternion.Euler(0, 0, angle), Vector3.one);
        GUI.DrawTexture(new Rect(0, -width * 0.5f, length, width), lineTex);
        GUI.matrix = savedMatrix;
    }
}
