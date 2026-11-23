using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>What a coat is made of: how it arrives, clings, runs and is drawn. A particle material that implements ICoatMatter hands one out.</summary>
public abstract class CoatMatter
{
    public string Material { get; }
    protected CoatMatter(string material) => Material = material;

    // px at 1080p, seconds; the solver in Coat reads these, the subclass's Tick does the rest
    internal float Supply;  // px² per second per 100 px of strand
    internal float Hold = 1f;  // film thinner than this clings
    internal float MaxThick = 4f;  // most it piles up
    internal float Run;  // px/s down a vertical strand at twice Hold; 0 = stays put
    internal float Dry;  // 1/s
    internal float Level;  // how fast a lump spreads along the strand (1/s per cell); 0 = it stays where it is
    internal float PoolDepth;  // how much thicker the film settles, in hold thicknesses, per unit of the host's pooling potential (groove against ridge is two units); 0 = it does not pool
    internal float Index = 1f;  // power-law index n (see LiquidPhysics.Index)
    internal bool Settles;  // arrives from above and lies on the upper side

    internal virtual void Seed(Coat c) { }
    internal virtual void Tick(Coat c, float h) { }
    internal virtual void Emit(EffectScene scene, Coat c, float time) { }
    internal abstract void Draw(ImDrawListPtr dl, Coat c, float alpha, in MaterialContext ctx);
}

/// <summary>Water, blood, slime: a film that clings, runs, pools, hangs as a drop on a thinning neck and lets go.</summary>
public sealed class LiquidMatter : CoatMatter
{
    private readonly Liquid _liq;
    internal LiquidPhysics Physics => _liq.Phys;

    /// <summary>The colour of this liquid's film where the surface faces n, th px thick: lit by the host's own surface frame, darker where thick.</summary>
    internal uint FilmShade(Vector3 n, float th, float px, float alpha, float soft)
    {
        Vector4 lit = _liq.Matcap.Sample(n.X, n.Y);
        float show = Smooth(th / (_liq.Phys.Hold * px * 0.9f));
        float dark = 0.5f + 0.5f * MathF.Exp(-_liq.Phys.Absorb * th / px);
        float spec = MathF.Max(0f, lit.X + lit.Y + lit.Z - 1.9f) * 1.4f;  // the matcap's highlights read as a wet sheen
        float a = Math.Clamp(lit.W * (0.45f + 0.55f * show) * 0.85f + spec * 0.35f, 0f, 1f) * alpha * soft * soft;
        return CoatDraw.Pack(Vector3.Min(new Vector3(lit.X, lit.Y, lit.Z) * dark + new Vector3(spec * 0.35f), Vector3.One), a);
    }

    internal LiquidMatter(string material, Liquid liquid, float? index = null) : base(material)
    {
        _liq = liquid;
        var p = liquid.Phys;
        Supply = p.Supply; Hold = p.Hold; MaxThick = p.MaxThick; Run = p.Run; Dry = 0.01f; Level = p.Level; PoolDepth = 0.5f; Index = index ?? p.Index;
    }

    internal override void Seed(Coat c)
    {
        for (int i = 0, n = c.N * c.V; i < n; i++) c.M[i] = c.A[i] * c.Px * _liq.Phys.Hold * (0.8f + 0.5f * c.Patch[i]);
    }

    internal override void Tick(Coat c, float h)
    {
        var ph = _liq.Phys;
        float px = c.Px, ds = c.Ds;
        float mcrit = ph.DropArea * px * px;
        int gap = Math.Max(1, (int)MathF.Round(ph.Spacing * 0.5f * px / ds)), nv = c.V;

        for (int k = 0; k < Coat.MaxPendants; k++)
        {
            ref var p = ref c.Pend[k];
            if (!p.Live) continue;
            float face = p.Cell < c.Live * nv ? c.AnchorOf(p.Cell, out _) : 0f;
            if (face < ph.MinSlope * 0.6f) { c.Retire(k); continue; }  // the strand swung level: it draws back in

            // the strand around it (its catchment, nearer cells giving more) hands matter to the drop; with none left it eventually gives up
            float got = 0f;
            int prow = p.Cell / nv, psec = p.Cell - prow * nv;
            for (int jr = Math.Max(0, prow - gap); jr <= Math.Min(c.Live - 1, prow + gap); jr++)
                for (int s2 = 0; s2 < nv; s2++)  // all the way round, as a strand's catchment is; nearer sectors give more
                {
                    int j = jr * nv + s2;
                    float avail = c.M[j] - ph.Hold * px * c.A[j] * 0.85f;
                    if (avail <= 0f) continue;
                    int sd = Math.Abs(s2 - psec); if (c.SeamV) sd = Math.Min(sd, nv - sd);  // round a ring the short way
                    float w = (1f - MathF.Abs(jr - prow) / (gap + 1f)) * (nv == 1 ? 1f : 1f - (float)sd / nv);
                    float q = MathF.Min(avail, c.M[j] * ph.Feed * (0.4f + face) * w * h);
                    c.Debit(j, q); got += q;
                }
            p.M += got;
            if (got > 0f) p.Idle = 0f;
            else if ((p.Idle += h) > 2f && p.M < mcrit * 0.5f) { c.Retire(k); continue; }

            // Until the weight passes what surface tension holds the bead just swells; past it the neck thins and lengthens.
            float load = p.M / mcrit;
            if (load < 1f)
            {
                p.Neck = Lerp(0.66f, 0.42f, MathF.Pow(load, 1.4f));
                p.L = MathF.Sqrt(p.M / MathF.PI) * 0.92f * (0.5f + 0.9f * load * load);
            }
            else
            {
                // strain rate falls as the string lengthens; a shear-thinning liquid then thickens, so its neck lasts longer (n = 1: no change)
                float tau = MathF.Max(0.05f, ph.Thread) * MathF.Pow(MathF.Max(1f, p.L / MathF.Max(1e-3f, 2f * MathF.Sqrt(p.M / MathF.PI) * 0.92f)), 1f - Index);
                p.Neck -= h * 0.42f * load / tau;
                p.L += h * ph.Stretch * px * load;
                if (ph.Bridge > 0f)
                {
                    c.AnchorOf(p.Cell, out Vector2 anchor);
                    if (c.Set.TryBridge(c, k, anchor, Hang(c, in p, anchor), MathF.Sqrt(p.M / MathF.PI) * 0.92f, ph.Bridge, ph.Hold * px)) continue;
                }
            }
            if (p.Neck <= 0.09f || load >= 2.4f) Release(c, k);  // refused when the drop pool is full: it stays hanging and tries again
        }

        // New drops where the underside faces down and the film is thick enough, never crowding another
        for (int i = 0, cells = c.Live * nv; i < cells; i++)
        {
            if (c.PendAt[i] >= 0 || !c.Vis[i] || !c.Holds[i] || c.AnchorOf(i, out _) <= ph.MinSlope || c.M[i] / c.A[i] < ph.Hold * px * 1.15f) continue;
            bool crowded = false;
            if (nv == 1)
            {
                for (int d = Math.Max(0, i - gap); d <= Math.Min(c.N - 1, i + gap) && !crowded; d++) crowded = c.PendAt[d] >= 0;
            }
            else crowded = c.M[i] / c.A[i] < 1.5f * ph.MaxThick * px && CrowdedOnChart(c, c.Anch[i], 0.5f * ph.Spacing * px);  // drops also keep their distance from neighbouring charts' drops, unless the film is overloaded and has nowhere else to go
            if (crowded) continue;
            int slot = -1;
            for (int s = 0; s < Coat.MaxPendants; s++) if (!c.Pend[s].Live) { slot = s; break; }
            if (slot < 0) break;
            c.Pend[slot] = new Coat.Pendant { Live = true, Cell = i, Neck = 0.66f, Salt = c.Salt * 131 + c.Serial++ * 17 + i };
            c.PendAt[i] = (sbyte)slot;
        }
    }

    /// <summary>Lets pendant k go as a drop (and maybe a satellite); false, with nothing changed, if the pool has no room.</summary>
    internal bool Release(Coat c, int k)
    {
        if (c.Set.Drops.Items.Length - c.Set.Drops.Count < 2) return false;
        var ph = _liq.Phys;
        ref var p = ref c.Pend[k];
        c.AnchorOf(p.Cell, out Vector2 anchor);
        Vector2 bead = Hang(c, in p, anchor);

        // The string that stays behind recoils into the film; a satellite splits off the drop.
        float rem = p.M * (0.10f + 0.25f * ph.Stringiness), md = p.M - rem;
        c.Credit(p.Cell, rem);
        float sat = 0f;
        if (Hash01(p.Salt + 5) < ph.Satellites) { sat = md * 0.14f; md -= sat; }
        c.Out += md + sat; c.Shed += md + sat;

        Spawn(c, in ph, bead, anchor, p.Salt, md, p.Neck, main: true);
        if (sat > 0f) Spawn(c, in ph, bead + new Vector2(HashRange(p.Salt + 6, -5f, 5f), 9f) * c.Px, anchor, p.Salt + 1, sat, 0f, main: false);

        c.PendAt[p.Cell] = -1;
        p.Live = false;
        c.Released++;
        return true;
    }

    /// <summary>A drop of this liquid from the coat; false if the pool is full (the caller keeps the liquid).</summary>
    internal bool TryDrop(Coat c, Vector2 pos, Vector2 anchor, int salt, float area, float neck) => Spawn(c, _liq.Phys, pos, anchor, salt, area, neck, main: true);

    private bool Spawn(Coat c, in LiquidPhysics ph, Vector2 pos, Vector2 anchor, int salt, float area, float neck, bool main)
    {
        float px = c.Px, r = MathF.Sqrt(area / MathF.PI) * 0.95f;
        return c.Set.Drops.Add(new DropPool.Drop
        {
            Owner = c.Owner, Pos = pos, Amount = area,
            Vel = new Vector2(HashRange(salt + 7, -14f, 14f), HashRange(salt + 8, ph.ReleaseMin, ph.ReleaseMax) * 0.8f) * px,
            Gravity = new Vector2(0f, ph.Gravity * px),
            Anchor = anchor, Pinch = pos,
            Born = c.Time, Life = HashRange(salt + 9, ph.LifeMin, ph.LifeMax),
            Size = r, Brightness = c.Bright,
            Tether = main ? MathF.Max(neck, 0.12f) * (0.5f + ph.Stringiness) : 0f,
            RecoilTime = main ? 0.18f + 0.35f * ph.Stringiness : 0f,
            Drag = ph.Drag, Seed = salt, HostSeed = c.Seed,
            Variant = r < 1.7f * px ? GoopDraw.Speck : GoopDraw.Free,
            Released = c.Time, Material = Material, ColorOverride = c.Color,
        });
    }

    private static Vector2 Hang(Coat c, in Coat.Pendant p, Vector2 anchor)
    {
        float swing = 0.12f * MathF.Sin(c.Time * 1.9f + p.Salt * 0.37f);
        return anchor + Vector2.Normalize(new Vector2(swing, 1f)) * p.L;
    }

    internal override void Emit(EffectScene scene, Coat c, float time)
    {
        Beads(scene, c);
        var ph = _liq.Phys;
        float mcrit = ph.DropArea * c.Px * c.Px;
        for (int k = 0; k < Coat.MaxPendants; k++)
        {
            ref readonly var p = ref c.Pend[k];
            if (!p.Live) continue;
            float rb = MathF.Sqrt(p.M / MathF.PI) * 0.92f;
            if (rb < 0.6f * c.Px) continue;
            float face = c.AnchorOf(p.Cell, out Vector2 anchor);
            Vector2 bead = Hang(c, in p, anchor);
            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Owner = c.Owner, Position = bead, Anchor = anchor, ThreadEnd = bead, Tether = p.Neck,
                Size = rb, AgeRatio = Saturate(p.M / mcrit),
                Brightness = c.Bright * Smooth((face - ph.MinSlope * 0.6f) / 0.25f),
                Seed = p.Salt, HasHost = true, HostSeed = c.Seed, Role = PrimitiveRole.Goop,
                MaterialName = Material, ColorOverride = c.Color,
            }, c.Owner);
        }
    }

    /// <summary>A string between two hosts: a lit tube along a drooping curve, thinnest in the middle where it is stretched.</summary>
    internal void DrawFilament(ImDrawListPtr dl, in CoatSet.Bridge br, Vector2[] pts, float alpha, in MaterialContext ctx)
    {
        const int N = 14, Cols = 5;
        Vector2 mid = (br.Pa + br.Pb) * 0.5f + new Vector2(0f, br.Sag * 2f);  // a quadratic curve peaks halfway to its control point
        for (int i = 0; i < N; i++)
        {
            float t = i / (N - 1f), u = 1f - t;
            pts[i] = u * u * br.Pa + 2f * u * t * mid + t * t * br.Pb;
        }
        float r0 = Math.Clamp(0.5f * br.Thick, 0.7f, 3.4f * (br.A.Px));
        var mb = CoatDraw.Mb;
        mb.Reserve(N * Cols, N * Cols * 6);
        int first = mb.Vertices;
        for (int i = 0; i < N; i++)
        {
            Vector2 d = pts[Math.Min(i + 1, N - 1)] - pts[Math.Max(i - 1, 0)];
            Vector2 n = d.LengthSquared() > 1e-6f ? new Vector2(-d.Y, d.X) / d.Length() : new Vector2(1f, 0f);
            float r = r0 * (0.65f + 0.35f * MathF.Abs(2f * i / (N - 1f) - 1f));
            for (int j = 0; j < Cols; j++)
            {
                float x = -1f + 2f * j / (Cols - 1);
                Vector4 lit = _liq.Matcap.Sample(x * n.X, x * n.Y);
                mb.VertexReserved(pts[i] + n * (x * r), CoatDraw.Pack(new Vector3(lit.X, lit.Y, lit.Z), lit.W * alpha * CoatDraw.Soft(x, 0.4f)));
            }
        }
        mb.StitchGrid(first, N, Cols);
        mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>True if a pendant of this chart or of the charts a few places either side of it in the stroke hangs within r of the point.</summary>
    private static bool CrowdedOnChart(Coat c, Vector2 at, float r)
    {
        float r2 = r * r;
        var sibs = c.Siblings;
        int lo = sibs == null ? 0 : Math.Max(0, c.SibIndex - 8), hi = sibs == null ? 0 : Math.Min(sibs.Count - 1, c.SibIndex + 8);
        int mate = ((c.ChartId - 1) ^ 1) + 1;  // the other half of the same link: they share their tips and are one bar, so they do not crowd each other
        for (int q = lo; q <= hi; q++)
        {
            var o = sibs == null ? c : sibs[q];
            if (o.ChartId == mate && sibs != null) continue;
            for (int k = 0; k < Coat.MaxPendants; k++)
            {
                if (!o.Pend[k].Live) continue;
                o.AnchorOf(o.Pend[k].Cell, out Vector2 pa);
                if (Vector2.DistanceSquared(pa, at) < r2) return true;
            }
        }
        return false;
    }

    // Beads sitting on the face where the film is thick: stateless, a few per cell, thicker film more and bigger.
    private void Beads(EffectScene scene, Coat c)
    {
        var ph = _liq.Phys;
        float px = c.Px, hold = ph.Hold * px;
        for (int i = 0, cells = c.Live * c.V; i < cells; i++)
        {
            if (!c.Vis[i]) continue;
            float th = c.M[i] / c.A[i], k = Smooth((th - 0.5f * hold) / hold);
            int seed = c.Salt * 53 + i * 11;
            float dens = c.V == 1 ? 1f : MathF.Min(1f, c.A[i] / (10f * px));  // a strand cell is ~10 px of film; a chart's cells are smaller, so each takes its share
            if (k <= 0.02f || Hash01(seed) > k * 0.6f * dens) continue;
            c.Set.BeadsDrawn++;
            float size = HashRange(seed + 3, 1.3f, 2.7f) * px * (0.7f + 0.5f * Saturate(th / (ph.MaxThick * px)));
            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Owner = c.Owner, AgeRatio = 0.5f, Size = size, Brightness = c.Bright * k, Seed = seed,
                Position = c.P[i] + c.Sd[i] * (HashRange(seed + 1, -0.65f, 0.65f) * (c.V == 1 ? c.R[i] : 0.5f * c.VStep[i % c.V])) + c.T[i] * (HashRange(seed + 2, -0.5f, 0.5f) * c.Dc[i]),
                HasHost = true, HostSeed = c.Seed, Role = PrimitiveRole.Goop, MaterialName = Material, ColorOverride = c.Color,
            }, c.Owner);
        }
    }

    internal override void Draw(ImDrawListPtr dl, Coat c, float alpha, in MaterialContext ctx)
    {
        if (c.V > 1) return;  // a chart is drawn by its host, in the host's own passes
        float a = MathF.Min(1f, alpha * ctx.Alpha);
        var skin = new Skin { L = _liq, Alpha = a * c.Opt.Skin };
        CoatDraw.Band(dl, c, ref skin, in ctx);
        var film = new Film { L = _liq, Alpha = a };
        CoatDraw.Band(dl, c, ref film, in ctx);
    }

    // A thin glossy layer over the whole strand, whichever way it faces: what makes it read as wet.
    private struct Skin : IBand
    {
        public Liquid L;
        public float Alpha;
        public bool Continuous => true;

        public float Extent(Coat c, int i, out float uIn, out float uOut, out float sign)
        {
            float th = c.M[i] / c.Ds;
            uIn = -c.R[i]; uOut = c.R[i] + MathF.Min(th * 0.3f, c.R[i] * 0.3f);
            sign = 1f;
            return Smooth(th / (L.Phys.Hold * c.Px * 0.6f));
        }

        public uint Color(Coat c, int i, float x, Vector2 dir, float show)
        {
            Vector4 lit = L.Matcap.Sample(x * dir.X, x * dir.Y);
            return CoatDraw.Pack(new Vector3(lit.X, lit.Y, lit.Z), lit.W * show * Alpha * 0.45f * CoatDraw.Soft(x, 0.2f));
        }
    }

    // The matter hanging off the lower edge: thick where it has pooled, fading as the surface turns from facing down.
    private struct Film : IBand
    {
        public Liquid L;
        public float Alpha;

        public float Extent(Coat c, int i, out float uIn, out float uOut, out float sign)
        {
            float r = c.R[i], th = MathF.Min(c.M[i] / c.Ds, 0.7f * r + 2f * c.Px);  // never wider than the limb it hangs from
            uOut = r + th * 1.1f;
            uIn = r - MathF.Min(th * 1.2f + c.Px, r * 0.8f);
            sign = 1f;
            return Smooth(th / (L.Phys.Hold * c.Px * 0.9f)) * Smooth(c.Face[i] / 0.3f);
        }

        public uint Color(Coat c, int i, float x, Vector2 dir, float show)
        {
            // a half-cylinder across the band: the normal leans toward the side's edge
            Vector4 lit = L.Matcap.Sample(x * dir.X, x * dir.Y);
            float dark = 0.5f + 0.5f * MathF.Exp(-L.Phys.Absorb * MathF.Min(c.M[i] / c.Ds, c.R[i]) / c.Px);  // Beer-Lambert: a thicker film is darker
            return CoatDraw.Pack(new Vector3(lit.X, lit.Y, lit.Z) * dark, lit.W * show * Alpha * CoatDraw.Soft(x, 0.3f) * (x < 0f ? 0.7f : 1f));
        }
    }
}

/// <summary>Fire: fuel clings to the strand; heat spreads between neighbours and burns it, throwing flames, cinders and smoke and leaving glowing char.</summary>
public sealed class FlameMatter : CoatMatter
{
    private const float BurnRate = 0.30f, CharRate = 0.06f, CharMax = 0.85f;  // px/s of fuel at full heat; 1/s
    private static readonly FlameLayer Back = new(FlameLayer.Back, 1.15f, 1, 20f, 34f);
    private static readonly FlameLayer Body = new(FlameLayer.Body, 0.85f, 2, 18f, 32f);
    private static readonly FlameLayer Front = new(FlameLayer.Front, 0.60f, 1, 12f, 22f);

    private readonly Palette _ramp;

    internal FlameMatter(string material, Palette ramp) : base(material)
    {
        _ramp = ramp;
        Supply = 28f; Hold = 0.6f; MaxThick = 1.4f;
    }

    internal override void Seed(Coat c)
    {
        for (int i = 0; i < c.N; i++) c.M[i] = c.Ds * c.Px * 0.8f * (0.6f + 0.4f * c.Patch[i]);
        int sites = Math.Max(2, c.N / 14);
        for (int k = 0, lit = 0; k < sites * 8 && lit < sites; k++)
        {
            int i = (int)(Hash01(c.Seed * 977 + k * 131) * c.Live) % Math.Max(1, c.Live);
            if (c.Live > 0 && c.Vis[i] && c.Heat[i] == 0f) { c.Heat[i] = 1f; lit++; }
        }
    }

    internal override void Tick(Coat c, float h)
    {
        float px = c.Px, ds = c.Ds;
        int n = c.Live;
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            total += c.Heat[i];
            float fuel = Saturate(c.M[i] / ds / (0.45f * px));
            float nb = MathF.Max(i > 0 ? c.Heat[i - 1] : (c.Closed ? c.Heat[n - 1] : 0f), i < n - 1 ? c.Heat[i + 1] : (c.Closed ? c.Heat[0] : 0f));
            float ign = (nb > 0.4f ? 1.8f : 0f) + (c.Char[i] > 0.35f ? 0.7f * c.Char[i] : 0f);  // a neighbour's flame, or embers still glowing in the char
            c.Heat[i] = Saturate(c.Heat[i] + (ign * fuel * (1f - c.Heat[i]) - 1.5f * (1f - fuel) * c.Heat[i]) * h);

            float heat = c.Heat[i];
            if (heat < 0.02f) continue;
            float used = MathF.Min(c.M[i], BurnRate * px * ds * heat * fuel * h);
            c.Debit(i, used); c.Out += used; c.Set.Sunk[(int)CoatSet.Sink.Burned] += used;
            c.Char[i] = MathF.Min(CharMax, c.Char[i] + CharRate * heat * h * (1f - c.Char[i]));

            int salt = c.Seed + i * 977 + (int)(c.Time * 997f);
            Vector2 top = c.P[i] - c.Nm[i] * (c.R[i] * 0.6f);
            if (Hash01(salt) < 0.12f * heat * h)
                c.Set.Cinders.Add(new ParticleSystem.Particle
                {
                    Pos = top, Vel = new Vector2(HashRange(salt + 1, -55f, 55f), -HashRange(salt + 2, 90f, 300f)) * px,
                    Born = c.Time, Life = HashRange(salt + 3, 1.3f, 2.8f), Size = HashRange(salt + 4, 1.7f, 3.6f) * px,
                    Brightness = c.Bright, Seed = salt, Role = PrimitiveRole.Cinder, Owner = c.Owner, Color = c.Color,
                });
            if (Hash01(salt + 50) < 0.07f * heat * h)
                c.Set.Smoke.Add(new ParticleSystem.Particle
                {
                    Pos = top, Vel = new Vector2(HashRange(salt + 5, -14f, 14f), -HashRange(salt + 6, 30f, 70f)) * px,
                    Born = c.Time, Life = HashRange(salt + 7, 3f, 5f), Size = HashRange(salt + 8, 30f, 60f) * px,
                    Brightness = 0.5f * c.Bright, Seed = salt, Role = PrimitiveRole.Smoke, Owner = c.Owner, Color = c.Color,
                });
        }

        // A coat with no fire and fuel to burn catches again from a stray ember, so it never goes dark for good.
        if (total < 0.05f && n > 0)
        {
            int i = (int)(Hash01(c.Seed * 3 + (int)(c.Time * 10f)) * n) % n;
            if (c.Vis[i] && c.M[i] / ds > 0.3f * px) c.Heat[i] = 1f;
        }
    }

    internal override void Emit(EffectScene scene, Coat c, float time)
    {
        float px = c.Px;
        int stride = Math.Max(1, (int)MathF.Round(44f * px / c.Ds)), half = stride / 2;
        for (int i = half; i < c.Live; i += stride)
        {
            float heat = 0f;
            for (int j = Math.Max(0, i - half); j <= Math.Min(c.Live - 1, i + half); j++) heat = MathF.Max(heat, c.Heat[j]);
            if (heat < 0.12f || !c.Vis[i]) continue;
            Vector2 at = c.P[i] - c.Nm[i] * (c.R[i] * 0.55f);
            int site = c.Seed * 61 + i * 17;
            float fuel = Saturate(c.M[i] / c.Ds / (0.8f * px));
            float size = px * (0.3f + 0.8f * heat) * (0.55f + 0.45f * fuel), b = c.Bright * heat;
            var spread = new Vector2(c.Ds * 0.4f, 3f * px);
            Flames.Tongues(scene, Back, at, site, time, size, spread, 0f, 0.3f, b, c.Color, Material);
            Flames.Tongues(scene, Body, at, site + 1, time, size, spread, 0f, 0.3f, b, c.Color, Material);
            Flames.Tongues(scene, Front, at, site + 2, time, size, spread, 0f, 0.3f, b, c.Color, Material);
        }
    }

    internal override void Draw(ImDrawListPtr dl, Coat c, float alpha, in MaterialContext ctx)
    {
        float a = MathF.Min(1f, alpha * ctx.Alpha);
        var soot = new Soot { Alpha = a };
        CoatDraw.Band(dl, c, ref soot, in ctx);
        var glow = new Glow { Alpha = a, Ramp = _ramp };
        CoatDraw.Band(dl, c, ref glow, in ctx);
    }

    private struct Soot : IBand
    {
        public float Alpha;
        public bool Continuous => true;
        public float Extent(Coat c, int i, out float uIn, out float uOut, out float sign) { uIn = -c.R[i] * 0.9f; uOut = c.R[i] * 0.9f; sign = 1f; return c.Char[i]; }
        public uint Color(Coat c, int i, float x, Vector2 dir, float show) =>
            Tint(Pack(0.05f, 0.045f, 0.04f), show * 0.55f * Alpha * CoatDraw.Soft(x, 0f), false);
    }

    private struct Glow : IBand
    {
        public float Alpha;
        public Palette Ramp;
        public bool Continuous => true;
        public float Extent(Coat c, int i, out float uIn, out float uOut, out float sign) { uIn = -c.R[i] * 0.8f; uOut = c.R[i] * 0.8f; sign = 1f; return c.Heat[i]; }
        public uint Color(Coat c, int i, float x, Vector2 dir, float show)
        {
            float flicker = 0.85f + 0.15f * MathF.Sin(c.Time * 9f + i * 1.7f);
            return WithAlpha(Ramp.Sample(0.30f + 0.55f * show * (1f - x * x * 0.6f)), show * 0.42f * Alpha * flicker * CoatDraw.Soft(x, 0f));
        }
    }
}

/// <summary>Snow and the like: settles on whatever faces up, banks into drifts, creeps off steep slopes, and drops lumps when overloaded.</summary>
public sealed class GranularMatter : CoatMatter
{
    private static readonly Matcap Powder = new(new SurfaceSpec(new Vector3(0.80f, 0.86f, 0.95f), new Vector3(0.04f), 0f, 0.92f, 0.22f, 0.28f, 0.45f, new Vector3(0.35f, 0.50f, 0.80f)));

    internal GranularMatter(string material) : base(material)
    {
        Supply = 40f; Hold = 3.0f; MaxThick = 9f; Run = 6f; Settles = true;
    }

    internal override void Seed(Coat c)
    {
        for (int i = 0; i < c.N; i++) c.M[i] = c.Ds * c.Px * (1.5f + 2.5f * c.Patch[i]) * Saturate(c.Face[i] * 1.5f);
    }

    internal override void Tick(Coat c, float h)
    {
        float px = c.Px, ds = c.Ds, full = MaxThick * px * 0.9f * ds;
        for (int i = 0; i < c.Live; i++)
        {
            if (c.M[i] < full) continue;
            float m = c.M[i] * 0.5f;
            c.Debit(i, m); c.Out += m; c.Set.Sunk[(int)CoatSet.Sink.Lumped] += m;
            int salt = c.Seed * 7 + i * 131 + (int)(c.Time * 97f);
            c.Set.Lumps.Add(new ParticleSystem.Particle
            {
                Pos = c.P[i] + c.Nm[i] * c.R[i], Vel = new Vector2(HashRange(salt, -30f, 30f), 20f) * px, Gravity = new Vector2(0f, 900f * px),
                Born = c.Time, Life = HashRange(salt + 1, 1.4f, 2.0f), Size = MathF.Sqrt(m / MathF.PI) * 0.8f,
                Brightness = c.Bright, Seed = salt, Role = PrimitiveRole.Snow, Owner = c.Owner, Color = c.Color,
            });
        }
    }

    internal override void Draw(ImDrawListPtr dl, Coat c, float alpha, in MaterialContext ctx)
    {
        var band = new Bank { Alpha = MathF.Min(1f, alpha * ctx.Alpha) };
        CoatDraw.Band(dl, c, ref band, in ctx);
    }

    private struct Bank : IBand
    {
        public float Alpha;

        public float Extent(Coat c, int i, out float uIn, out float uOut, out float sign)
        {
            float th = c.M[i] / c.Ds * (0.7f + 0.6f * Hash01(c.Seed * 29 + i * 7)), r = c.R[i];  // drifts are lumpy
            uOut = r + th * 1.3f;
            uIn = r - MathF.Min(th * 0.8f, r * 0.6f);
            sign = -1f;  // the upper side
            return Smooth(th / (0.8f * c.Px)) * Smooth(c.Face[i] / 0.3f);
        }

        public uint Color(Coat c, int i, float x, Vector2 dir, float show) =>
            CoatDraw.Pack(Powder.Sample(x * dir.X, x * dir.Y), show * 0.96f * Alpha * CoatDraw.Soft(x, 0.25f));
    }
}
