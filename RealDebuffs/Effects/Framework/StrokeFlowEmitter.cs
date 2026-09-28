using System;
using System.Collections.Generic;
using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Spawns and advances path-following particles along strokes. Each drip remembers the stroke it
/// belongs to (identified by owner + role + stroke seed), its current arc position along that
/// stroke, and its base speed. Every frame the drip's arc is advanced, the strand is re-sampled
/// at the new position, and a lateral wobble is applied. Speed is modulated by arc position so
/// the drip slows as it passes each "obstacle" (typically a sucker on the strand) and speeds up
/// between them, which reads as the drip catching and releasing.
///
/// Direction is chosen at spawn: whichever endpoint of the strand is lower on screen is
/// downstream. A tendril whose base is up off the top of the screen flows toward its tip; one
/// whose tip is up flows toward its base.
///
/// Drips whose parent stroke is no longer in the scene (the tendril unlatched and rebuilt, or the
/// effect ended) are culled. Drips whose arc reaches the end of their path fade out via the
/// material's normal AgeRatio envelope.
/// </summary>
public static class StrokeFlowEmitter
{
    private const int PoolCapacity = 600;

    private struct FlowingDrip
    {
        public DebuffKind Owner;
        public PrimitiveRole Role;
        public int   StrokeSeed;
        public string? MaterialName;

        public float Born;
        public float Lifespan;
        public float Size;
        public float Brightness;
        public Vector4? ColorOverride;

        public float Arc;               // current arc position along the parent stroke
        public float BaseSpeed;         // px/s, unsigned
        public bool  FlowToTip;         // direction along the path
        public float WobblePhase;
        public float WobbleFreq;
        public float WobbleAmp;
        public float ObstacleSpacing;

        public float BaseLateralFrac;   // fraction of the strand's half-width
        public int   SideSign;          // -1 or +1, which side of the strand it runs on
    }

    private static readonly FlowingDrip[] Pool = new FlowingDrip[PoolCapacity];
    private static int _count;

    public static void Emit(EffectScene scene, float time, float dt,
                            IReadOnlyDictionary<string, string>? overrides)
    {
        // 1. Cull expired.
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - Pool[i].Born < Pool[i].Lifespan)
                Pool[w++] = Pool[i];
        }
        _count = w;

        // 2. Spawn new drips from each stroke's flow emissions.
        int strokeCount = scene.Strokes.Count;
        for (int si = 0; si < strokeCount; si++)
        {
            var s = scene.Strokes[si];
            if (s.Path.Count < 2 || s.Reveal <= 0.001f) continue;

            var flows = GetFlowsFor(in s, overrides);
            for (int fi = 0; fi < flows.Length; fi++)
                SpawnFromStroke(in s, flows[fi], time, dt);
        }

        // 3. Advance every drip along its parent stroke and push it into the scene.
        for (int i = 0; i < _count; i++)
        {
            var d = Pool[i];

            // At the call site inside the advance loop, change:
            //     var stroke = FindStroke(scene, d.Owner, d.Role, d.StrokeSeed);
            // to:
            var stroke = FindStroke(scene, d.Owner, d.StrokeSeed);
            if (stroke is null) continue; // parent strand gone this frame; freeze it

            var s = stroke.Value;
            var path = s.Path;
            float totalLen = path.Length * s.Reveal;
            if (totalLen < 1f) continue;

            // Advance arc: base speed modulated by obstacle sync.
            float obstaclePhase = d.Arc / MathF.Max(1f, d.ObstacleSpacing) * MathF.Tau;
            // 0.2 near an obstacle, 1.0 between. cos is +1 at arc=0 (between) and -1 at
            // arc=spacing*0.5 (on an obstacle).
            float speedFactor = 0.6f + 0.4f * MathF.Cos(obstaclePhase);
            float currentSpeed = d.BaseSpeed * speedFactor;
            d.Arc += (d.FlowToTip ? 1f : -1f) * currentSpeed * dt;

            // Reached an endpoint: keep it pinned and let the lifespan envelope fade it out.
            if (d.Arc < 0f) d.Arc = 0f;
            if (d.Arc > totalLen) d.Arc = totalLen;

            Pool[i] = d;

            // Sample the strand at the drip's position.
            path.SampleAtArc(d.Arc, out Vector2 pos, out Vector2 tan);

            // Perpendicular offset: base lateral placement + slow wobble.
            Vector2 perp = new(-tan.Y, tan.X);
            float halfWidth = MathF.Max(1f, s.WidthHint * 0.5f);
            float wobble = MathF.Sin(time * d.WobbleFreq * MathF.Tau + d.WobblePhase) * d.WobbleAmp;
            float lateral = (d.BaseLateralFrac * halfWidth + wobble) * d.SideSign;

            Vector2 finalPos = pos + perp * lateral;

            float age = time - d.Born;
            float ageT = Math.Clamp(age / d.Lifespan, 0f, 1f);

            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Position = finalPos,
                Velocity = tan * (d.FlowToTip ? currentSpeed : -currentSpeed),
                AgeRatio = ageT,
                Size = d.Size,
                Brightness = d.Brightness * ParticleEmitter.FadeFor(ageT),
                Seed = d.StrokeSeed + i * 7919,
                Role = d.Role,
                MaterialName = d.MaterialName,
                ColorOverride = d.ColorOverride,
            }, d.Owner);
        }
    }

    private static void SpawnFromStroke(in StrokePrimitive s, in StrokeFlowEmission f, float time, float dt)
    {
        float visibleLen = s.Path.Length * s.Reveal;
        if (visibleLen < 8f) return;

        float expected = f.DensityPer100px * visibleLen / 100f * dt;
        int toSpawn = (int)expected;
        if (DrawHelpers.Hash01(unchecked(s.Seed + (int)(time * 90f) + (int)f.Role * 7919)) < expected - toSpawn)
            toSpawn++;
        if (toSpawn > 6) toSpawn = 6;
        if (toSpawn <= 0) return;

        // Direction: whichever endpoint is lower on screen.
        s.Path.SampleAtArc(0f, out Vector2 basePos, out _);
        s.Path.SampleAtArc(s.Path.Length, out Vector2 tipPos, out _);
        bool flowToTip = tipPos.Y > basePos.Y;

        for (int i = 0; i < toSpawn && _count < PoolCapacity; i++)
        {
            int seed = unchecked(s.Seed + (int)(time * 977f) + i * 131 + (int)f.Role * 7919);

            // Spawn somewhere along the strand.
            float arc = visibleLen * DrawHelpers.Hash01(seed);

            Pool[_count++] = new FlowingDrip
            {
                Owner = s.Owner,
                Role = f.Role,
                StrokeSeed = s.Seed,
                MaterialName = null,

                Born = time,
                Lifespan = DrawHelpers.HashRange(seed + 1, f.LifespanMin, f.LifespanMax),
                Size = DrawHelpers.HashRange(seed + 2, f.SizeMin, f.SizeMax),
                Brightness = s.Brightness,
                ColorOverride = s.ColorOverride,

                Arc = arc,
                BaseSpeed = DrawHelpers.HashRange(seed + 3, f.SpeedMin, f.SpeedMax),
                FlowToTip = flowToTip,
                WobblePhase = DrawHelpers.HashRange(seed + 4, 0f, MathF.Tau),
                WobbleFreq = f.WobbleFrequencyHz,
                WobbleAmp = f.WobbleAmplitude,
                ObstacleSpacing = f.ObstacleSpacingPx,
                BaseLateralFrac = f.LateralOffsetFrac,
                SideSign = DrawHelpers.Hash01(seed + 5) < 0.5f ? -1 : 1,
            };
        }
    }

    private static ReadOnlySpan<StrokeFlowEmission> GetFlowsFor(
        in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        // Emit-axis override applies to flow too: a "made of snow" phrase on a stroke would give
        // us a particle material with a flow spec... but particle materials don't declare flows.
        // So for now, flow emissions come only from the stroke material itself.
        string materialName = ResolveStrokeMaterial(in s, overrides);
        IStrokeMaterial mat;
        try { mat = MaterialRegistry.GetStroke(materialName); }
        catch { return ReadOnlySpan<StrokeFlowEmission>.Empty; }
        return mat.Flows;
    }

    /// <summary>
    /// Locates the parent stroke a drip belongs to. Matches by (Owner, Seed) only: Seed is the
    /// parent stroke's seed, which is unique per-stroke within an effect's scene, and Owner scopes
    /// it to the right effect. Deliberately NOT matching on Role, because the drip's Role is its
    /// RENDER role (Drip) while the parent stroke's role is its SHAPE role (MainStroke) — they
    /// legitimately differ, and comparing them means the lookup always fails.
    /// </summary>
    private static StrokePrimitive? FindStroke(EffectScene scene, DebuffKind owner, int seed)
    {
        for (int i = 0; i < scene.Strokes.Count; i++)
        {
            var s = scene.Strokes[i];
            if (s.Owner == owner && s.Seed == seed)
                return s;
        }
        return null;
    }

    private static string ResolveStrokeMaterial(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(s.Owner, "Stroke", s.Role), out var name))
            return name;
        return BuiltInDefaults.Get(s.Owner, "Stroke", s.Role.ToString())
            ?? BuiltInDefaults.FallbackStroke();
    }
}