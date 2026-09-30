using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Fire: one flame tongue, drawn as a small vertex-colored mesh that is "shaded" on the CPU.
///
/// WHY A MESH: a flame is a luminous volume that fades smoothly to nothing at its edges. Flat
/// translucent circles can't do that - each one leaves a visible rim, and a field of them reads as
/// bokeh. Here every vertex carries its own color and alpha, the GPU interpolates between them, and
/// the edges of the mesh are alpha 0, so overlapping tongues merge into one continuous fire.
///
/// THE SHAPE (per tongue): a tapered, pointed silhouette anchored at Position and growing up. Its
/// centerline sways on scrolling noise (so the tip wags and licks), leans with a slow shared breeze,
/// and its width wobbles. Grid rows run base -> tip, columns run across.
///
/// THE SHADING (per vertex): a density value from a parabolic cross-section (bright down the middle)
/// times a lengthwise falloff, then eroded by two octaves of noise that scrolls upward and grows
/// stronger toward the tip - which is what tears the tip into wisps. Density drives BOTH alpha and a
/// temperature; temperature indexes a black-body-style ramp (white-yellow core, orange body, red
/// fringe). Cooler, thinner regions are also more transparent, so the fringe reads as a glow over the
/// scene rather than an opaque paint stroke.
///
/// LAYERS (ParticlePrimitive.Variant): the renderer only has straight alpha blending, no additive, so
/// "hot" can't simply add up. Instead the effect draws back (1) -> body (0) -> front (2) and each
/// layer is tuned hotter and denser than the last, so brighter cores always end up on top.
///
/// Also used as a stroke emitter by other effects ("made of fire"), where the particles are small:
/// LOD drops rows and columns with size so a 6px lick costs a handful of vertices.
/// </summary>
public sealed class ParticleEmber : IParticleMaterial
{
    public string Name => "particle.ember";
    public string[] NaturalLanguageWords { get; } = { "fire", "flame", "flames", "ember", "embers" };

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.5f) return;

        float t = ctx.Time;
        int seed = p.Seed;
        float sf = (seed & 0x3FFF) * 0.0173f;           // this tongue's private offset into the noise field
        float age = p.AgeRatio;

        // ---- layer tuning ----
        // heat:   scales temperature (front layer runs hotter, back layer cooler)
        // alphaK: layer opacity
        // tall:   aspect multiplier (back flames stretch, front flames stay compact)
        // rough:  how hard the noise erodes the silhouette
        float heat, alphaK, tall, rough;
        switch (p.Variant)
        {
            case 1:  heat = 0.78f; alphaK = 0.44f; tall = 1.02f; rough = 1.95f; break;   // back
            case 2:  heat = 1.22f; alphaK = 0.92f; tall = 0.86f; rough = 0.90f; break;   // front
            case 3:  heat = 1.08f; alphaK = 0.90f; tall = 0.42f; rough = 0.80f; break;   // bed: wide, low, continuous
            default: heat = 1.00f; alphaK = 0.82f; tall = 1.00f; rough = 1.00f; break;   // body
        }

        // ---- life envelope: catch fast, hold, then shrink and cool ----
        float grow   = FireNoise.Smooth(age / 0.20f);
        float decay  = FireNoise.Smooth((age - 0.50f) / 0.50f);
        float widthK  = (0.70f + 0.30f * grow) * (1f - 0.35f * decay);
        float heightK = (0.30f + 0.70f * grow) * (1f - 0.45f * decay);
        heat *= 1f - 0.22f * decay;

        float w = p.Size * 2f * widthK;      // Size is radius-like (half-width), like every other particle material
        float aspect = (2.3f + 1.4f * DrawHelpers.Hash01(seed + 3)) * tall;
        float h = w * aspect * heightK;
        if (h < 1.5f) return;

        // ---- LOD ----
        // Vertex count is the whole cost of this material (every vertex is a couple of noise samples
        // plus native calls to ImGui), so spend it where the eye looks: the soft back and bed layers
        // are smooth by design and get fewer rows, narrow tongues get fewer columns.
        float px = ctx.ScreenScale;
        int rows = Math.Clamp((int)(h / 17f) + 3, 5, 11);
        if (p.Variant == 1 || p.Variant == 3) rows = Math.Max(5, rows - 3);
        int cols = w > 42f * px ? 4 : (w > 12f * px ? 3 : 2);

        // ---- lean ----
        // Three sources so the fire never lines up like a row of candles:
        //  - a per-tongue lean, usually mild but occasionally strong (squared hash),
        //  - a breeze that varies with screen X as well as time, so gusts travel across the fire,
        //  - the tongue's own horizontal motion (a moving flame trails behind it).
        float lr = DrawHelpers.Hash01(seed + 5) * 2f - 1f;
        float hk = DrawHelpers.Hash01(seed + 6);
        float own = lr * (0.22f + 0.42f * hk * hk);
        float breeze = FireNoise.Value(t * 0.42f + p.Position.X * 0.0035f, 3.7f) * 0.34f;
        float lean = Math.Clamp(own + breeze - p.Velocity.X * 0.0016f, -0.46f, 0.46f);

        Vector2 b = p.Position + new Vector2(p.Sway, 0f);
        float flickerPhase = sf * 2.3f;

        // Vertex 0..cols is the SKIRT row (filled after the loop); the flame proper starts at 'stride'.
        int stride = cols + 1;
        int i = stride;
        for (int r = 0; r <= rows; r++)
        {
            float v = r / (float)rows;
            float vPow = v * MathF.Sqrt(v);              // v^1.5: bends grow toward the tip

            // centerline: lean + two octaves of sway that scroll up the flame over time
            float sway = FireNoise.Value(sf * 3.1f + v * 1.25f, flickerPhase - t * 1.55f) * 0.60f
                       + FireNoise.Value(sf * 1.7f + v * 3.20f, flickerPhase * 1.7f - t * 3.60f) * 0.22f;
            float cx = b.X + lean * h * 0.50f * vPow + sway * w * 1.05f * v;
            float cy = b.Y - v * h;

            // width profile: widest low on the flame, tapering to a point, with a little wobble
            float taper = MathF.Pow(1f - v, 0.85f);
            float belly = 0.50f + 0.50f * FireNoise.Smooth(v / 0.16f);
            float wobble = 1f + 0.18f * MathF.Sin(v * 5.1f + t * 1.9f + flickerPhase);
            float half = 0.5f * w * taper * belly * wobble;

            float longi = MathF.Pow(1f - v, 0.55f);                               // fades toward the tip
            float erodeGain = (0.14f + 0.86f * FireNoise.Smooth(v / 0.75f)) * rough;
            float tipFade = FireNoise.Smooth((1f - v) / 0.10f);                   // exactly 0 at the tip vertex
            bool fine = v > 0.22f;                                                // no fine octave near the base, erosion is tiny there

            for (int c = 0; c <= cols; c++)
            {
                float u = -1f + 2f * c / cols;
                float au = MathF.Abs(u);

                float cross = 1f - u * u;
                cross *= MathF.Sqrt(cross);                                       // (1-u²)^1.5

                float n1 = 0.5f + 0.5f * FireNoise.Value(u * 1.5f + sf * 5.3f, v * 2.7f - t * 2.6f + flickerPhase);
                float n = n1;
                if (fine)
                {
                    float n2 = 0.5f + 0.5f * FireNoise.Value(u * 3.4f + sf * 2.1f, v * 5.6f - t * 4.4f);
                    n = n1 * 0.62f + n2 * 0.38f;
                }
                float erode = (n - 0.5f) * erodeGain * 1.05f;

                float d = cross * longi + erode;
                d *= 1f - FireNoise.Smooth((au - 0.62f) / 0.38f);                // silhouette edge is exactly 0
                d *= tipFade;
                d = d < 0f ? 0f : (d > 1f ? 1f : d);

                float temp = d * (1.30f - 0.62f * v) * heat;
                float a = d * 1.7f; a = a > 1f ? 1f : a;
                a = a * a * (3f - 2f * a);

                MeshDraw.P[i] = new Vector2(cx + u * half, cy);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(FireColor.Heat(temp), a * k * alphaK);
                i++;
            }
        }

        // ---- skirt: a soft root instead of a hard cut ----
        // The mesh's base row is fully opaque, so its bottom edge is a straight horizontal line. That
        // is invisible while the base is anchored just off the screen edge - but the moment a flame
        // lifts off, or sits in mid-screen on a side edge, the flat base shows. The skirt copies the
        // base row a little lower and fades it to zero alpha, so the base dissolves instead.
        float skirt = h * 0.18f;
        for (int c = 0; c <= cols; c++)
        {
            MeshDraw.P[c] = MeshDraw.P[stride + c] + new Vector2(0f, skirt);
            MeshDraw.C[c] = MeshDraw.C[stride + c] & 0x00FFFFFFu;     // same color, alpha 0
        }

        MeshDraw.Grid(dl, cols, rows + 1, MeshDraw.WhiteUv(t));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    // Flames shed along another effect's strokes: small licks that rise and die. Size is radius-like
    // (half the flame's width, see Draw), so these come out ~7-15px wide.
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(
        Role: PrimitiveRole.Ember,
        DensityPer100px: 5f,
        SpeedMin: 18f, SpeedMax: 50f,
        LifespanMin: 0.65f, LifespanMax: 1.30f,
        SizeMin: 3.5f, SizeMax: 7.5f,
        SpreadRadians: 0.55f,
        BiasVelocity: new Vector2(0f, -14f),
        PrimaryDirection: new Vector2(0f, -1f)),
    };
}
