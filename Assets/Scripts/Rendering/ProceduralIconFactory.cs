using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Génère à la volée de petites icônes vectorielles (Texture2D 28x28, dessinées pixel par pixel)
/// pour remplacer les préfixes emoji des menus IMGUI. Même philosophie que TacticalRadarUI.cs
/// (synthèse procédurale de Texture2D) : aucune image externe, tout est calculé au premier accès
/// puis mis en cache en statique.
/// </summary>
public static class ProceduralIconFactory
{
    private const int SIZE = 28;
    private static readonly Vector2 Center = new Vector2(SIZE * 0.5f, SIZE * 0.5f);

    private static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

    private static Texture2D Get(string key, Func<Vector2, Color> colorAt)
    {
        if (cache.TryGetValue(key, out Texture2D existing) && existing != null) return existing;

        Texture2D tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < SIZE; y++)
        {
            for (int x = 0; x < SIZE; x++)
            {
                tex.SetPixel(x, y, colorAt(new Vector2(x + 0.5f, y + 0.5f)));
            }
        }
        tex.Apply();
        cache[key] = tex;
        return tex;
    }

    private static readonly Color Ink = new Color(0.92f, 0.95f, 1f, 0.95f);

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float sqLen = Vector2.Dot(ab, ab);
        float t = sqLen > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqLen) : 0f;
        Vector2 proj = a + ab * t;
        return Vector2.Distance(p, proj);
    }

    private static Color Line(Vector2 p, Vector2 a, Vector2 b, float thickness, Color col)
    {
        return DistToSegment(p, a, b) < thickness ? col : Color.clear;
    }

    private static Color Blend(Color a, Color b) => b.a > 0.01f ? b : a;

    // --- Icônes ---

    public static Texture2D House() => Get("house", p =>
    {
        // Corps carré + toit triangulaire
        bool body = p.x > 8 && p.x < 20 && p.y > 4 && p.y < 15;
        bool roofTri = p.y >= 15 && p.y <= 24 && Mathf.Abs(p.x - 14) < (24f - p.y) * 0.72f;
        return (body || roofTri) ? Ink : Color.clear;
    });

    public static Texture2D Ladder() => Get("ladder", p =>
    {
        Color c = Color.clear;
        c = Blend(c, Line(p, new Vector2(9, 3), new Vector2(9, 25), 1.3f, Ink));
        c = Blend(c, Line(p, new Vector2(19, 3), new Vector2(19, 25), 1.3f, Ink));
        for (int r = 0; r < 4; r++)
        {
            float y = 6 + r * 6;
            c = Blend(c, Line(p, new Vector2(9, y), new Vector2(19, y), 1.1f, Ink));
        }
        return c;
    });

    public static Texture2D Door() => Get("door", p =>
    {
        bool onFrame = p.x > 8 && p.x < 20 && p.y > 3 && p.y < 25 &&
                       (p.x < 9.4f || p.x > 18.6f || p.y > 23.6f || p.y < 4.4f);
        bool handle = Vector2.Distance(p, new Vector2(17, 13)) < 1.4f;
        return (onFrame || handle) ? Ink : Color.clear;
    });

    public static Texture2D Window() => Get("window", p =>
    {
        bool onFrame = p.x > 5 && p.x < 23 && p.y > 5 && p.y < 23 &&
                       (p.x < 6.4f || p.x > 21.6f || p.y < 6.4f || p.y > 21.6f);
        bool cross = Mathf.Abs(p.x - 14) < 0.7f || Mathf.Abs(p.y - 14) < 0.7f;
        return (onFrame || (cross && p.x > 5 && p.x < 23 && p.y > 5 && p.y < 23)) ? Ink : Color.clear;
    });

    public static Texture2D Shield() => Get("shield", p =>
    {
        Vector2 d = p - Center;
        bool inCircle = d.magnitude < 9.5f;
        bool cross = (Mathf.Abs(d.x) < 1.4f && Mathf.Abs(d.y) < 6f) || (Mathf.Abs(d.y) < 1.4f && Mathf.Abs(d.x) < 6f);
        return (inCircle && !cross) ? new Color(Ink.r, Ink.g, Ink.b, 0.85f) : (cross ? Ink : Color.clear);
    });

    public static Texture2D Mortar() => Get("mortar", p =>
    {
        Color c = Color.clear;
        // Plaque de base (baseplate) au sol
        bool basePlate = p.x >= 5 && p.x <= 13 && p.y >= 4 && p.y <= 7;
        if (basePlate) c = Ink;
        // Bipied de support
        c = Blend(c, Line(p, new Vector2(10, 6), new Vector2(16, 14), 1.8f, Ink));
        c = Blend(c, Line(p, new Vector2(14, 6), new Vector2(16, 14), 1.8f, Ink));
        // Tube du mortier incliné à ~55°
        c = Blend(c, Line(p, new Vector2(7, 6), new Vector2(21, 22), 3.2f, Ink));
        // Obus / projectile au sommet
        bool shell = Vector2.Distance(p, new Vector2(23, 24)) < 2.2f;
        if (shell) c = Ink;
        return c;
    });

    public static Texture2D Eye() => Get("eye", p =>
    {
        Vector2 d = (p - Center);
        float lens = (d.x * d.x) / (10.5f * 10.5f) + (d.y * d.y) / (5.5f * 5.5f);
        bool onLens = lens < 1f && lens > 0.55f;
        bool pupil = d.magnitude < 2.6f;
        return (onLens || pupil) ? Ink : Color.clear;
    });

    public static Texture2D Clock() => Get("clock", p =>
    {
        float dist = Vector2.Distance(p, Center);
        bool ring = Mathf.Abs(dist - 9.5f) < 1.3f;
        Color c = ring ? Ink : Color.clear;
        c = Blend(c, Line(p, Center, Center + new Vector2(0, 6.5f), 1.1f, Ink));
        c = Blend(c, Line(p, Center, Center + new Vector2(4.5f, 0), 1.1f, Ink));
        return c;
    });

    public static Texture2D Cross() => Get("cross", p =>
    {
        Vector2 d = p - Center;
        Vector2 r = new Vector2(d.x + d.y, d.y - d.x) * 0.7071f; // rotation 45°
        bool onX = Mathf.Abs(r.x) < 1.4f || Mathf.Abs(r.y) < 1.4f;
        return (onX && d.magnitude < 9.5f) ? Ink : Color.clear;
    });

    public static Texture2D Check() => Get("check", p =>
    {
        Color c = Line(p, new Vector2(6, 15), new Vector2(11, 21), 1.6f, Ink);
        c = Blend(c, Line(p, new Vector2(11, 21), new Vector2(22, 7), 1.6f, Ink));
        return c;
    });

    public static Texture2D Soldier() => Get("soldier", p =>
    {
        bool head = Vector2.Distance(p, new Vector2(14, 21)) < 3.2f;
        bool body = p.x > 9 && p.x < 19 && p.y > 4 && p.y < 16 &&
                    Mathf.Abs(p.x - 14) < 5f - (p.y - 4) * 0.08f;
        return (head || body) ? Ink : Color.clear;
    });

    public static Texture2D Tank() => Get("tank", p =>
    {
        bool hull = p.x > 4 && p.x < 24 && p.y > 4 && p.y < 11;
        bool turret = p.x > 10 && p.x < 20 && p.y >= 11 && p.y < 16;
        bool barrel = p.y > 12 && p.y < 14.5f && p.x >= 18 && p.x < 26;
        return (hull || turret || barrel) ? Ink : Color.clear;
    });

    public static Texture2D GunVehicle() => Get("gunvehicle", p =>
    {
        bool hull = p.x > 4 && p.x < 22 && p.y > 6 && p.y < 14;
        bool barrel = p.y > 12.5f && p.y < 15f && p.x >= 18 && p.x < 26;
        bool wheel1 = Vector2.Distance(p, new Vector2(9, 5)) < 3f;
        bool wheel2 = Vector2.Distance(p, new Vector2(19, 5)) < 3f;
        return (hull || barrel || wheel1 || wheel2) ? Ink : Color.clear;
    });

    public static Texture2D Barrier() => Get("barrier", p =>
    {
        bool plank = p.x > 3 && p.x < 25 && p.y > 10 && p.y < 18;
        bool stripe1 = Mathf.Abs((p.x - p.y) - 0f) < 2f && plank;
        bool stripe2 = Mathf.Abs((p.x - p.y) - 10f) < 2f && plank;
        bool stripe3 = Mathf.Abs((p.x - p.y) + 10f) < 2f && plank;
        bool border = plank && (p.y < 11.4f || p.y > 16.6f);
        return (stripe1 || stripe2 || stripe3 || border) ? Ink : Color.clear;
    });

    public static Texture2D Bolt() => Get("bolt", p =>
    {
        Color c = Line(p, new Vector2(17, 25), new Vector2(10, 14), 1.8f, Ink);
        c = Blend(c, Line(p, new Vector2(10, 14), new Vector2(16, 14), 1.8f, Ink));
        c = Blend(c, Line(p, new Vector2(16, 14), new Vector2(9, 3), 1.8f, Ink));
        return c;
    });

    public static Texture2D Reset() => Get("reset", p =>
    {
        float dist = Vector2.Distance(p, Center);
        bool arc = Mathf.Abs(dist - 9f) < 1.4f && !(p.x > 14 && p.y > 14 && p.x < 22 && p.y < 22);
        bool arrowHead = Vector2.Distance(p, new Vector2(21, 17)) < 2.4f;
        return (arc || arrowHead) ? Ink : Color.clear;
    });

    public static Texture2D Squad() => Get("squad", p =>
    {
        bool h1 = Vector2.Distance(p, new Vector2(10, 20)) < 2.6f;
        bool h2 = Vector2.Distance(p, new Vector2(18, 20)) < 2.6f;
        bool b1 = p.x > 6 && p.x < 14 && p.y > 5 && p.y < 15;
        bool b2 = p.x > 14 && p.x < 22 && p.y > 7 && p.y < 15;
        return (h1 || h2 || b1 || b2) ? Ink : Color.clear;
    });

    public static Texture2D Cube3D() => Get("cube3d", p =>
    {
        Color c = Color.clear;
        Vector2 a = new Vector2(9, 8), b = new Vector2(19, 8), cc = new Vector2(19, 18), d = new Vector2(9, 18);
        Vector2 a2 = a + new Vector2(4, 4), b2 = b + new Vector2(4, 4), c2 = cc + new Vector2(4, 4);
        c = Blend(c, Line(p, a, b, 1.1f, Ink)); c = Blend(c, Line(p, b, cc, 1.1f, Ink));
        c = Blend(c, Line(p, cc, d, 1.1f, Ink)); c = Blend(c, Line(p, d, a, 1.1f, Ink));
        c = Blend(c, Line(p, b, b2, 1.1f, Ink)); c = Blend(c, Line(p, cc, c2, 1.1f, Ink));
        c = Blend(c, Line(p, b2, c2, 1.1f, Ink));
        return c;
    });

    /// <summary>
    /// Bouton IMGUI avec icône procédurale à gauche du texte. Le label garde son emoji d'origine en
    /// secours visuel (aucun risque de casser la logique des call-sites existants).
    /// </summary>
    public static bool IconButton(Rect rect, Texture2D icon, string label, GUIStyle style)
    {
        bool clicked = GUI.Button(rect, label, style);
        if (icon != null)
        {
            float iconSize = Mathf.Min(rect.height - 8f, 26f);
            Rect iconRect = new Rect(rect.x + 8f, rect.y + (rect.height - iconSize) * 0.5f, iconSize, iconSize);
            GUI.DrawTexture(iconRect, icon);
        }
        return clicked;
    }

    public static bool IconButton(Rect rect, Texture2D icon, string label)
    {
        return IconButton(rect, icon, label, GUI.skin.button);
    }
}
