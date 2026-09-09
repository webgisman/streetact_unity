// Shim minimal de UnityEngine/UnityEditor pour compiler et EXECUTER Novgov.TacticalCore
// (+ Novgov.Core) et ses auto-tests hors de l'Editeur Unity, avec dotnet.
// Reproduit fidelement la SEMANTIQUE des membres utilises — en particulier l'operateur ==
// de Vector2/Vector3 (comparaison de sqrMagnitude a un epsilon, donc faux sur les infinis),
// qu'un test epingle explicitement.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public const float kEpsilon = 1E-05f;
        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 up => new Vector2(0f, 1f);
        public static Vector2 down => new Vector2(0f, -1f);
        public static Vector2 left => new Vector2(-1f, 0f);
        public static Vector2 right => new Vector2(1f, 0f);
        public static Vector2 positiveInfinity => new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        public static Vector2 negativeInfinity => new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public float sqrMagnitude => x * x + y * y;
        public Vector2 normalized { get { float m = magnitude; return m > 1E-05f ? new Vector2(x / m, y / m) : zero; } }

        public void Normalize() { var n = normalized; x = n.x; y = n.y; }
        public void Set(float nx, float ny) { x = nx; y = ny; }

        public float this[int i]
        {
            get => i == 0 ? x : i == 1 ? y : throw new IndexOutOfRangeException();
            set { if (i == 0) x = value; else if (i == 1) y = value; else throw new IndexOutOfRangeException(); }
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        // Semantique Unity : distance au carre comparee a un epsilon (NaN => false).
        public static bool operator ==(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < kEpsilon * kEpsilon;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);

        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
        public static float SqrMagnitude(Vector2 a) => a.sqrMagnitude;
        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { t = Mathf.Clamp01(t); return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t); }
        public static Vector2 LerpUnclamped(Vector2 a, Vector2 b, float t) => new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        public static Vector2 MoveTowards(Vector2 cur, Vector2 tgt, float maxDelta)
        {
            Vector2 d = tgt - cur; float m = d.magnitude;
            if (m <= maxDelta || m == 0f) return tgt;
            return cur + d / m * maxDelta;
        }
        public static Vector2 ClampMagnitude(Vector2 v, float max) => v.sqrMagnitude > max * max ? v.normalized * max : v;
        public static Vector2 Min(Vector2 a, Vector2 b) => new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
        public static Vector2 Max(Vector2 a, Vector2 b) => new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        public static Vector2 Perpendicular(Vector2 v) => new Vector2(-v.y, v.x);
        public static float Angle(Vector2 from, Vector2 to)
        {
            float denom = (float)Math.Sqrt(from.sqrMagnitude * (double)to.sqrMagnitude);
            if (denom < 1E-15f) return 0f;
            float c = Mathf.Clamp(Dot(from, to) / denom, -1f, 1f);
            return (float)Math.Acos(c) * 57.29578f;
        }

        public override bool Equals(object o) => o is Vector2 v && x.Equals(v.x) && y.Equals(v.y);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2})", x, y);
    }

    public struct Vector3
    {
        public float x, y, z;
        public const float kEpsilon = 1E-05f;
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0f; }
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);
        public static Vector3 positiveInfinity => new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        public static Vector3 negativeInfinity => new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized { get { float m = magnitude; return m > 1E-05f ? new Vector3(x / m, y / m, z / m) : zero; } }

        public void Set(float nx, float ny, float nz) { x = nx; y = ny; z = nz; }

        public float this[int i]
        {
            get => i == 0 ? x : i == 1 ? y : i == 2 ? z : throw new IndexOutOfRangeException();
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else throw new IndexOutOfRangeException(); }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < kEpsilon * kEpsilon;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 Min(Vector3 a, Vector3 b) => new Vector3(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z));
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));
        public static Vector3 ClampMagnitude(Vector3 v, float max) => v.sqrMagnitude > max * max ? v.normalized * max : v;
        public static Vector3 MoveTowards(Vector3 cur, Vector3 tgt, float maxDelta)
        {
            Vector3 d = tgt - cur; float m = d.magnitude;
            if (m <= maxDelta || m == 0f) return tgt;
            return cur + d / m * maxDelta;
        }

        public override bool Equals(object o) => o is Vector3 v && x.Equals(v.x) && y.Equals(v.y) && z.Equals(v.z);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})", x, y, z);
    }

    public static class Mathf
    {
        public const float PI = 3.14159274f;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        public const float Epsilon = 1.401298E-45f;

        public static float Abs(float f) => Math.Abs(f);
        public static int Abs(int f) => Math.Abs(f);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Tan(float f) => (float)Math.Tan(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Asin(float f) => (float)Math.Asin(f);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Exp(float f) => (float)Math.Exp(f);
        public static float Log(float f) => (float)Math.Log(f);
        public static float Floor(float f) => (float)Math.Floor(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Round(float f) => (float)Math.Round(f, MidpointRounding.ToEven);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int RoundToInt(float f) => (int)Math.Round(f, MidpointRounding.ToEven);
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float MoveTowards(float cur, float tgt, float maxDelta)
            => Math.Abs(tgt - cur) <= maxDelta ? tgt : cur + Math.Sign(tgt - cur) * maxDelta;
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static bool Approximately(float a, float b)
            => Math.Abs(b - a) < Math.Max(1E-06f * Math.Max(Math.Abs(a), Math.Abs(b)), Epsilon * 8f);
        public static float Repeat(float t, float length) => Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float cur, float tgt)
        {
            float d = Repeat(tgt - cur, 360f);
            if (d > 180f) d -= 360f;
            return d;
        }
        public static float InverseLerp(float a, float b, float v) => a == b ? 0f : Clamp01((v - a) / (b - a));
    }

    // JsonUtility n'est utilise que par le cache DISQUE de TacticalGridBuilder (jamais par la
    // resolution d'un tour). Le shim ecrit/relit un JSON reellement fonctionnel via
    // System.Text.Json afin que TOUT chemin de code compile ET reste testable.
    public static class JsonUtility
    {
        private static readonly System.Text.Json.JsonSerializerOptions Opts = new System.Text.Json.JsonSerializerOptions
        {
            IncludeFields = true,
        };
        public static string ToJson(object o) => System.Text.Json.JsonSerializer.Serialize(o, o?.GetType() ?? typeof(object), Opts);
        public static string ToJson(object o, bool pretty) => ToJson(o);
        public static T FromJson<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, Opts);
    }

    public static class Application
    {
        // Redirige le cache disque vers un dossier temporaire jetable propre au harnais.
        public static string persistentDataPath { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "novgov_harness_data");
        public static bool isEditor => false;
        public static bool isPlaying => false;
    }

    public static class Debug
    {
        public static readonly List<string> Errors = new List<string>();
        public static readonly List<string> Warnings = new List<string>();
        public static bool Quiet = false;

        private static string Strip(object m)
        {
            string s = m?.ToString() ?? "null";
            // Les tests emettent des balises <color=...> de la Console Unity.
            return System.Text.RegularExpressions.Regex.Replace(s, "</?color[^>]*>", "");
        }
        public static void Log(object m) { if (!Quiet) Console.WriteLine(Strip(m)); }
        public static void LogWarning(object m) { Warnings.Add(Strip(m)); if (!Quiet) Console.WriteLine("WARN  " + Strip(m)); }
        public static void LogError(object m) { Errors.Add(Strip(m)); Console.WriteLine("ERROR " + Strip(m)); }
        public static void LogException(Exception e) { LogError(e.ToString()); }
        public static void Assert(bool c, object m) { if (!c) LogError("Assert: " + Strip(m)); }
    }

    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeFieldAttribute : Attribute { }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MenuItemAttribute : Attribute
    {
        public MenuItemAttribute(string itemName) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction, int priority) { }
    }
}
