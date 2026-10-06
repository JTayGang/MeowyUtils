using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The one lighting rig every "photographic" material shares, so a chain, a rope and a stone
/// statue drawn in the same frame all appear to sit under the same lights: a warm key from the
/// upper left and front, a cold fill from the lower right, and a cold rim from behind that
/// separates dark metal from a dark scene. Also the single source of truth for which way cast
/// shadows fall.
///
/// Coordinates are VIEW space as ImGui sees it: +X right, +Y DOWN the screen, +Z toward the viewer.
/// </summary>
public static class StudioLighting
{
    public static readonly Vector3 Key  = Vector3.Normalize(new Vector3(-0.52f, -0.70f, 0.50f));
    public static readonly Vector3 Fill = Vector3.Normalize(new Vector3( 0.70f,  0.45f, 0.55f));
    public static readonly Vector3 Rim  = Vector3.Normalize(new Vector3( 0.80f, -0.30f, -0.52f));

    public static readonly Vector3 KeyColor  = new(1.00f, 0.90f, 0.76f);
    public static readonly Vector3 FillColor = new(0.34f, 0.40f, 0.52f);
    public static readonly Vector3 RimColor  = new(0.62f, 0.78f, 1.00f);

    /// <summary>Screen-space unit vector pointing the way shadows fall (directly away from the key light).</summary>
    public static readonly Vector2 ShadowDirection = Vector2.Normalize(new Vector2(-Key.X, -Key.Y));
}

/// <summary>How a surface answers light. Authored in linear space; the matcap converts to display space.</summary>
public readonly record struct SurfaceSpec(
    Vector3 Albedo,        // diffuse colour of the non-metallic part
    Vector3 F0,            // reflectance at normal incidence: the tint of the metal
    float   Metalness,     // 0 = pure diffuse (rust, stone), 1 = pure metal
    float   Roughness,     // 0 = mirror, 1 = chalk
    float   EnvStrength,   // how much of the surrounding scene shows in the reflection
    float   Ambient);      // flat fill so no normal is ever pure black

public static class SurfacePresets
{
    /// <summary>Blackened forged iron: dark, cool, with a tight hard highlight and a bluish edge.</summary>
    public static readonly SurfaceSpec BlackIron = new(
        Albedo: new(0.030f, 0.030f, 0.034f), F0: new(0.25f, 0.255f, 0.275f),
        Metalness: 0.94f, Roughness: 0.44f, EnvStrength: 0.80f, Ambient: 0.006f);

    /// <summary>Worn steel: lighter, with a broader, brighter sheen where it has been polished by friction.</summary>
    public static readonly SurfaceSpec WornSteel = new(
        Albedo: new(0.050f, 0.050f, 0.054f), F0: new(0.40f, 0.41f, 0.43f),
        Metalness: 0.96f, Roughness: 0.38f, EnvStrength: 0.90f, Ambient: 0.008f);

    /// <summary>Iron gone warm and dull: a brown cast, rougher, less mirror.</summary>
    public static readonly SurfaceSpec AgedIron = new(
        Albedo: new(0.060f, 0.042f, 0.030f), F0: new(0.30f, 0.255f, 0.215f),
        Metalness: 0.82f, Roughness: 0.56f, EnvStrength: 0.62f, Ambient: 0.010f);

    /// <summary>Hemp: warm tan plant fibre. Fully diffuse, rough as felt, with only a faint broad sheen where it grazes the light.</summary>
    public static readonly SurfaceSpec Hemp = new(
        Albedo: new(0.40f, 0.27f, 0.13f), F0: new(0.05f, 0.045f, 0.04f),
        Metalness: 0.0f, Roughness: 0.93f, EnvStrength: 0.05f, Ambient: 0.014f);

    /// <summary>Manila: paler and yellower than hemp, a little silkier.</summary>
    public static readonly SurfaceSpec Manila = new(
        Albedo: new(0.52f, 0.40f, 0.21f), F0: new(0.06f, 0.055f, 0.045f),
        Metalness: 0.0f, Roughness: 0.88f, EnvStrength: 0.06f, Ambient: 0.016f);

    /// <summary>Tarred rope: near-black brown with a dull wet sheen. Reads as a dark rope against a dark scene.</summary>
    public static readonly SurfaceSpec Tarred = new(
        Albedo: new(0.085f, 0.060f, 0.040f), F0: new(0.07f, 0.065f, 0.060f),
        Metalness: 0.0f, Roughness: 0.62f, EnvStrength: 0.20f, Ambient: 0.008f);

    /// <summary>Rust scale: pure diffuse, orange-brown, nearly matte. Blended over any of the above as patina.</summary>
    public static readonly SurfaceSpec Rust = new(
        Albedo: new(0.110f, 0.044f, 0.020f), F0: new(0.05f, 0.03f, 0.02f),
        Metalness: 0.04f, Roughness: 0.92f, EnvStrength: 0.04f, Ambient: 0.012f);
}

/// <summary>
/// A "material capture": the lit colour of a surface as a function of the direction it faces,
/// baked into a small table. Shading a vertex then costs one bilinear lookup instead of a
/// lighting model, which is what makes a photographic look affordable when every vertex of a
/// 40-link chain is shaded on the CPU every frame.
///
/// The model is deliberately plain and physically motivated: Lambert diffuse from the three lights,
/// a Blinn-Phong highlight from the key with a Schlick Fresnel term, a reflection of a simple
/// environment (dark ground, cool sky, a warm horizon band, a cold backlight), and a cold rim.
/// Output is display-space RGB, because ImGui blends in display space.
///
/// Built once per spec and cached by the caller. Lookup is allocation-free.
/// </summary>
public sealed class Matcap
{
    private const int N = 48;
    private readonly Vector4[] _rgb = new Vector4[N * N];   // w unused: one 16-byte load per texel

    public Matcap(in SurfaceSpec spec)
    {
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float nx = x / (N - 1f) * 2f - 1f;
            float ny = y / (N - 1f) * 2f - 1f;
            float r2 = nx * nx + ny * ny;
            if (r2 > 1f) { float k = 1f / MathF.Sqrt(r2); nx *= k; ny *= k; r2 = 1f; }
            var n = new Vector3(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - r2)));

            Vector3 c = Shade(n, in spec);
            _rgb[y * N + x] = new Vector4(c, 0f);
        }
    }

    /// <summary>Display-space colour for a unit view-space normal (only X and Y are needed; Z is implied).</summary>
    public Vector3 Sample(float nx, float ny)
    {
        float fx = (nx * 0.5f + 0.5f) * (N - 1);
        float fy = (ny * 0.5f + 0.5f) * (N - 1);
        fx = fx < 0f ? 0f : (fx > N - 1.001f ? N - 1.001f : fx);
        fy = fy < 0f ? 0f : (fy > N - 1.001f ? N - 1.001f : fy);

        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        int i00 = y0 * N + x0;

        float w00 = (1f - tx) * (1f - ty), w10 = tx * (1f - ty), w01 = (1f - tx) * ty, w11 = tx * ty;
        Vector4 c = _rgb[i00] * w00 + _rgb[i00 + 1] * w10 + _rgb[i00 + N] * w01 + _rgb[i00 + N + 1] * w11;
        return new Vector3(c.X, c.Y, c.Z);
    }

    private static Vector3 Shade(Vector3 n, in SurfaceSpec s)
    {
        Vector3 v = Vector3.UnitZ;

        float ndlKey  = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Key));
        float ndlFill = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Fill));
        float ndlRim  = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Rim));

        // ---- diffuse ----
        Vector3 irradiance = StudioLighting.KeyColor * (1.30f * ndlKey)
                           + StudioLighting.FillColor * (0.22f * ndlFill)
                           + new Vector3(s.Ambient);
        Vector3 diffuse = s.Albedo * irradiance * (1f - s.Metalness);

        // ---- key highlight: tight lobe plus a broad sheen, Fresnel-weighted ----
        Vector3 h = Vector3.Normalize(StudioLighting.Key + v);
        float nh = MathF.Max(0f, Vector3.Dot(n, h));
        float vh = MathF.Max(0f, Vector3.Dot(v, h));
        float pTight = 12f + 520f * (1f - s.Roughness) * (1f - s.Roughness);
        float lobe = MathF.Pow(nh, pTight) * 1.7f + MathF.Pow(nh, MathF.Max(3f, pTight * 0.11f)) * 0.16f;
        float fres = MathF.Pow(1f - vh, 5f);
        Vector3 fSchlick = s.F0 + (Vector3.One - s.F0) * fres;
        Vector3 spec = fSchlick * StudioLighting.KeyColor * (lobe * ndlKey);

        // ---- environment reflection ----
        Vector3 r = 2f * n.Z * n - v;                       // view vector reflected about n
        // Rough metal reflects a blurred surround: blend the sharp environment toward its own average.
        float blur = s.Roughness * s.Roughness * 1.6f;
        blur = blur > 0.85f ? 0.85f : blur;
        Vector3 reflected = Vector3.Lerp(Environment(r), EnvAverage, blur) * s.EnvStrength;
        float grazing = MathF.Pow(1f - n.Z, 3f);            // reflections strengthen toward the silhouette
        Vector3 envF = s.F0 + (Vector3.One - s.F0) * (grazing * 0.55f);
        Vector3 refl = reflected * envF * (0.55f + 0.45f * s.Metalness);

        // ---- cold rim from behind: only where the surface turns away from the viewer ----
        float rim = MathF.Pow(1f - n.Z, 2.4f) * ndlRim;
        Vector3 rimLight = StudioLighting.RimColor * (0.30f * rim) * (0.25f + 0.75f * s.Metalness);

        Vector3 lin = diffuse + spec + refl + rimLight;
        return ToDisplay(lin);
    }

    private static readonly Vector3 EnvAverage = new(0.050f, 0.055f, 0.068f);

    /// <summary>
    /// A moody, mostly dark surround: black ground, a cool sky overhead, a faint warm horizon
    /// band, and a cold backlight. Dark on purpose: chain links reflect their surroundings, and
    /// in a dark scene mirror-like iron is mostly dark with a few bright accents.
    /// </summary>
    private static Vector3 Environment(Vector3 r)
    {
        float up = -r.Y;                                    // screen up is -Y
        float sky = Smooth(-0.35f, 0.85f, up);

        Vector3 ground = new(0.006f, 0.006f, 0.008f);
        Vector3 zenith = new(0.085f, 0.100f, 0.135f);
        Vector3 env = Vector3.Lerp(ground, zenith, sky);

        float band = MathF.Exp(-MathF.Pow((up + 0.02f) / 0.20f, 2f));
        env += new Vector3(0.38f, 0.25f, 0.15f) * (band * 0.30f * Smooth(-0.6f, 0.4f, r.Z));

        float back = MathF.Max(0f, Vector3.Dot(r, StudioLighting.Rim));
        env += StudioLighting.RimColor * (MathF.Pow(back, 7f) * 0.85f);

        float key = MathF.Max(0f, Vector3.Dot(r, StudioLighting.Key));
        env += StudioLighting.KeyColor * (MathF.Pow(key, 12f) * 0.55f);   // softbox glow in the reflection
        return env;
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Soft-shoulder tone map, then linear to display (sRGB-ish).</summary>
    private static Vector3 ToDisplay(Vector3 lin)
    {
        static float F(float x)
        {
            x = MathF.Max(0f, x);
            x = 1f - MathF.Exp(-1.55f * x);                // shoulders highlights instead of clipping them
            return MathF.Pow(x, 1f / 2.2f);
        }
        return new Vector3(F(lin.X), F(lin.Y), F(lin.Z));
    }
}
