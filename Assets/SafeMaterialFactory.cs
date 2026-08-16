using UnityEngine;

public static class SafeMaterialFactory
{
    private static Shader _cachedUnlitShader;
    private static Shader _cachedLitShader;

    public static Shader GetUnlitShader()
    {
        if (_cachedUnlitShader != null) return _cachedUnlitShader;

        _cachedUnlitShader = Shader.Find("Sprites/Default")
                          ?? Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("UI/Default")
                          ?? Shader.Find("Unlit/Color");

        if (_cachedUnlitShader == null)
        {
            Material sample = Resources.Load<Material>("Kucher/Tank Leopard2/Materials/TankBodyMaterial");
            if (sample != null) _cachedUnlitShader = sample.shader;
        }

        return _cachedUnlitShader;
    }

    public static Shader GetLitShader()
    {
        if (_cachedLitShader != null) return _cachedLitShader;

        Material sample = Resources.Load<Material>("Kucher/Tank Leopard2/Materials/TankBodyMaterial");
        if (sample != null) _cachedLitShader = sample.shader;
        else _cachedLitShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? GetUnlitShader();

        return _cachedLitShader;
    }

    public static Material CreateUnlit(Color color)
    {
        Shader s = GetUnlitShader();
        Material mat = (s != null) ? new Material(s) : new Material(Shader.Find("Sprites/Default"));
        mat.enableInstancing = true;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.color = color;
        return mat;
    }

    public static Material CreateLit(Color color)
    {
        Shader s = GetLitShader();
        Material mat = (s != null) ? new Material(s) : CreateUnlit(color);
        mat.enableInstancing = true;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.color = color;
        return mat;
    }
}
