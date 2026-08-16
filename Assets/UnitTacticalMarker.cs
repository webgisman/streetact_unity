using UnityEngine;

/// <summary>
/// Marqueur Tactique Militaire Haute Définition.
/// Génère procéduralement une icône 2D vectorielle avec insigne OTAN/Tactique,
/// bordure lumineuse (Bleu/Rouge) et flèche de direction d'orientation de l'unité.
/// Visible uniquement en vue Commandement (2D) sur le layer 'Units_UI_Markers'.
/// </summary>
public class UnitTacticalMarker : MonoBehaviour
{
    public Color markerColor = Color.blue;
    public float markerSize = 4.8f;

    private GameObject markerObject;
    private MeshRenderer markerRenderer;
    private UnitAI unitAI;

    private static Texture2D cachedAllyInfantryTex;
    private static Texture2D cachedAllyTankTex;
    private static Texture2D cachedEnemyInfantryTex;
    private static Texture2D cachedEnemyTankTex;

    void Start()
    {
        unitAI = GetComponent<UnitAI>();
        CreateMarker();
    }

    private void CreateMarker()
    {
        markerObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(markerObject.GetComponent<Collider>());

        markerObject.name = "TacticalMarker_" + gameObject.name;
        markerObject.transform.SetParent(this.transform);
        markerObject.transform.localPosition = new Vector3(0, 0.35f, 0); // Proche du sol
        markerObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        markerObject.transform.localScale = new Vector3(markerSize, markerSize, 1f);

        int uiLayer = LayerMask.NameToLayer("Units_UI_Markers");
        if (uiLayer == -1) uiLayer = 0;
        markerObject.layer = uiLayer;

        markerRenderer = markerObject.GetComponent<MeshRenderer>();
        
        bool isPlayer = unitAI != null ? unitAI.isPlayerControlled : (markerColor == Color.blue);
        bool isTank = unitAI != null && unitAI.isTank;

        Texture2D iconTex = GetOrCreateIconTexture(isPlayer, isTank);

        Material mat = SafeMaterialFactory.CreateUnlit(Color.white);
        mat.mainTexture = iconTex;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", iconTex);
        
        // Configuration de la transparence
        mat.SetFloat("_Surface", 1); // Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;

        markerRenderer.sharedMaterial = mat;
    }

    void Update()
    {
        if (markerObject != null)
        {
            // Oriente l'icône dans la direction où regarde l'unité (flèche de visée)
            markerObject.transform.rotation = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
            
            // Masquer si l'unité est morte
            if (unitAI != null && unitAI.isDead && markerRenderer.enabled)
            {
                markerRenderer.enabled = false;
            }
        }
    }

    private Texture2D GetOrCreateIconTexture(bool isPlayer, bool isTank)
    {
        if (isPlayer && !isTank && cachedAllyInfantryTex != null) return cachedAllyInfantryTex;
        if (isPlayer && isTank && cachedAllyTankTex != null) return cachedAllyTankTex;
        if (!isPlayer && !isTank && cachedEnemyInfantryTex != null) return cachedEnemyInfantryTex;
        if (!isPlayer && isTank && cachedEnemyTankTex != null) return cachedEnemyTankTex;

        Color mainCol = isPlayer ? new Color(0.1f, 0.65f, 1f, 1f) : new Color(1f, 0.2f, 0.25f, 1f);
        Color bgCol = new Color(0.04f, 0.08f, 0.12f, 0.88f);

        int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Vector2 center = new Vector2(31.5f, 31.5f);
        float radius = 28f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center);

                // 1. Fond du badge circulaire
                if (dist < radius)
                {
                    // Anneau extérieur lumineux
                    if (dist >= radius - 3.5f)
                    {
                        tex.SetPixel(x, y, mainCol);
                    }
                    else if (dist >= radius - 5.5f)
                    {
                        tex.SetPixel(x, y, Color.Lerp(mainCol, bgCol, 0.5f));
                    }
                    else
                    {
                        tex.SetPixel(x, y, bgCol);
                    }
                }
                else
                {
                    tex.SetPixel(x, y, Color.clear);
                }

                // 2. Flèche d'orientation directionnelle (Pointe vers le haut / Y positif)
                if (y > 48 && Mathf.Abs(x - 31.5f) < (60 - y) * 0.9f)
                {
                    tex.SetPixel(x, y, Color.white);
                }

                // 3. Symbole Tactique au centre
                if (isTank)
                {
                    // Symbole Char : Chenilles / Boîtier blindé et canon
                    bool isTread = (y >= 20 && y <= 40) && ((x >= 18 && x <= 22) || (x >= 41 && x <= 45));
                    bool isHull = (y >= 22 && y <= 38 && x >= 24 && x <= 39);
                    bool isCannon = (y >= 36 && y <= 46 && Mathf.Abs(x - 31.5f) <= 1.5f);

                    if (isCannon) tex.SetPixel(x, y, Color.white);
                    else if (isTread) tex.SetPixel(x, y, mainCol);
                    else if (isHull) tex.SetPixel(x, y, Color.Lerp(mainCol, Color.white, 0.4f));
                }
                else
                {
                    // Symbole Fantassin : Croix OTAN / Réticule d'infanterie
                    bool isLine1 = Mathf.Abs((x - 31.5f) - (y - 31.5f)) <= 1.5f && (dist < 18f);
                    bool isLine2 = Mathf.Abs((x - 31.5f) + (y - 31.5f)) <= 1.5f && (dist < 18f);
                    bool isCenterDot = dist < 4.5f;

                    if (isCenterDot) tex.SetPixel(x, y, Color.white);
                    else if (isLine1 || isLine2) tex.SetPixel(x, y, mainCol);
                }
            }
        }

        tex.Apply();

        if (isPlayer && !isTank) cachedAllyInfantryTex = tex;
        else if (isPlayer && isTank) cachedAllyTankTex = tex;
        else if (!isPlayer && !isTank) cachedEnemyInfantryTex = tex;
        else if (!isPlayer && isTank) cachedEnemyTankTex = tex;

        return tex;
    }
}
