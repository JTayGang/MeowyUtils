using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Cinder: a glowing fleck thrown off a fire. Three things separate it from a generic spark:
///
///  - it COOLS: color comes off the same black-body ramp as the flames, indexed by age, so a cinder
///    leaves the fire white-yellow, turns orange, then dull red before it winks out;
///  - it FLICKERS: brightness is modulated by fast per-particle noise, the way a real ember
///    pulses as air moves across it;
///  - it SMEARS: a streak trails behind it, sized from its current velocity (stateless - no
///    position history needed) and fading to nothing at the tail, which is what motion blur on a
///    fast, bright point looks like on camera.
///
/// A small radial halo (center alpha, transparent rim) gives the bloom around the hot core.
/// </summary>
public sealed class ParticleCinder : IParticleMaterial
{
    public string Name => "particle.cinder";
    public string[] NaturalLanguageWords { get; } = { "cinder", "cinders", "ash", "ashes" };

    private static readonly uint White = FireColor.Pack(1.00f, 0.97f, 0.85f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.2f) return;

        float t = ctx.Time;
        float age = p.AgeRatio;

        float flick = 0.62f + 0.38f * FireNoise.Perlin(p.Seed * 0.0131f, t * 10.5f);
        // Cools along the ramp; the last stretch of life is the dull-red "going out" phase.
        float temp = 0.88f - 0.72f * FireNoise.Smooth(age * 1.05f);
        uint hot = FireColor.Heat(temp);

        float r = p.Size * (1f - 0.35f * age);
        Vector2 pos = p.Position + new Vector2(p.Sway, 0f);
        Vector2 uv = MeshDraw.WhiteUv(t);
        float a = k * flick;

        // ---- streak ----
        float speed = p.Velocity.Length();
        if (speed > 30f)
        {
            float len = MathF.Min(speed * 0.045f, 42f * ctx.ScreenScale);
            Vector2 dir = p.Velocity / speed;
            Vector2 nrm = new Vector2(-dir.Y, dir.X) * (r * 0.9f);
            Vector2 tail = pos - dir * len;

            uint head = DrawHelpers.WithAlpha(hot, a * 0.80f);
            uint none = DrawHelpers.WithAlpha(hot, 0f);
            MeshDraw.Quad(dl, uv,
                          pos + nrm, head, pos - nrm, head,
                          tail - nrm * 0.15f, none, tail + nrm * 0.15f, none);
        }

        // ---- halo ----
        Span<float> rr = stackalloc float[1] { r * 4.2f };
        Span<uint>  cc = stackalloc uint[1] { DrawHelpers.WithAlpha(hot, 0f) };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(hot, a * 0.32f),
                        8, rr, cc, rotation: 0f, squashY: 1f, seed: p.Seed, irregular: 0f);

        // ---- core: a hot dot, shifted toward white while young ----
        uint core = DrawHelpers.LerpColor(hot, White, MathF.Max(0f, 1f - age * 2.2f) * 0.75f);
        dl.AddCircleFilled(pos, r, DrawHelpers.WithAlpha(core, a * 0.95f));
    }
}
