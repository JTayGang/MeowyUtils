using System;
using System.Numerics;
using ControlStruct = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace FPSBooster;

// Four side planes through the eye, rebuilt from the angles so the FOV margin is exact.
public readonly struct ViewFrustum
{
    public readonly Vector3 Pos;
    private readonly Vector3 _nl, _nr, _nb, _nt;

    private ViewFrustum(Vector3 pos, Vector3 fwd, Vector3 r, Vector3 u, float hh, float hv, float marginRad)
    {
        Pos = pos;
        float maxA = 89f * (MathF.PI / 180f);
        float h = MathF.Min(hh + marginRad, maxA), v = MathF.Min(hv + marginRad, maxA);
        float ch = MathF.Cos(h), sh = MathF.Sin(h), cv = MathF.Cos(v), sv = MathF.Sin(v);
        _nl = r * ch + fwd * sh;
        _nr = -r * ch + fwd * sh;
        _nb = u * cv + fwd * sv;
        _nt = -u * cv + fwd * sv;
    }

    // Compares against the last full pass (not the last frame) so slow camera drift accumulates.
    public bool IsPositionMeaningfullyDifferent(in ViewFrustum other)
    {
        const float positionThresholdSquared = 0.0025f; // 0.05 yalms
        return Vector3.DistanceSquared(Pos, other.Pos) > positionThresholdSquared;
    }

    public bool IsMeaningfullyDifferent(in ViewFrustum other)
    {
        const float minimumPlaneDot = 0.99999845f; // approximately 0.1 degrees

        if (IsPositionMeaningfullyDifferent(other))
            return true;

        return Vector3.Dot(_nl, other._nl) < minimumPlaneDot
            || Vector3.Dot(_nr, other._nr) < minimumPlaneDot
            || Vector3.Dot(_nb, other._nb) < minimumPlaneDot
            || Vector3.Dot(_nt, other._nt) < minimumPlaneDot;
    }

    // True if any part of the sphere is inside all four planes.
    public bool SphereVisible(Vector3 center, float radius)
    {
        var d = center - Pos;
        return Vector3.Dot(_nl, d) >= -radius && Vector3.Dot(_nr, d) >= -radius
            && Vector3.Dot(_nb, d) >= -radius && Vector3.Dot(_nt, d) >= -radius;
    }

    // Uses the game's final view-projection matrix, the same one Dalamud's WorldToScreen uses.
    public static unsafe bool TryCreate(float marginDeg, out ViewFrustum frustum, out string failure)
    {
        frustum = default;
        try
        {
            var ctl = ControlStruct.Instance();
            if (ctl == null)
            {
                failure = "Control is null";
                return false;
            }
            Matrix4x4 vp = ctl->ViewProjectionMatrix;
            return FromViewProjection(vp, marginDeg, out frustum, out failure);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    // Row-vector convention (clip = p * VP, in front when w > 0), as the game and Dalamud use.
    public static bool FromViewProjection(Matrix4x4 vp, float marginDeg, out ViewFrustum frustum, out string failure)
    {
        frustum = default;
        failure = "";

        if (!IsFinite(vp)) { failure = "view-projection matrix is not finite"; return false; }

        // Columns of VP as plane normals: clip.x = c1.p + M41, clip.y = c2.p + M42, clip.w = c4.p + M44.
        var c1 = new Vector3(vp.M11, vp.M21, vp.M31);
        var c2 = new Vector3(vp.M12, vp.M22, vp.M32);
        var c4 = new Vector3(vp.M14, vp.M24, vp.M34);

        // The eye is where x = y = w = 0 (Cramer's rule).
        float det = Vector3.Dot(c1, Vector3.Cross(c2, c4));
        if (!(MathF.Abs(det) > 1e-9f)) { failure = "matrix is degenerate (all zero or orthographic)"; return false; }
        var pos = (-vp.M41 * Vector3.Cross(c2, c4) - vp.M42 * Vector3.Cross(c4, c1) - vp.M44 * Vector3.Cross(c1, c2)) / det;
        if (!IsFinite(pos)) { failure = "could not locate the camera in the matrix"; return false; }

        // Side planes (-w <= x,y <= w); they don't depend on the depth convention.
        if (!Norm(c4 + c1, out var nl) || !Norm(c4 - c1, out var nr) || !Norm(c4 + c2, out var nb) || !Norm(c4 - c2, out var nt))
        {
            failure = "frustum planes are degenerate";
            return false;
        }

        var fwdRaw = nl + nr + nb + nt;
        if (fwdRaw.LengthSquared() < 1e-6f) { failure = "frustum has no forward direction"; return false; }
        var f = Vector3.Normalize(fwdRaw);
        if (!Ortho(nl - nr, f, out var r) || !Ortho(nb - nt, f, out var u)) { failure = "frustum axes are degenerate"; return false; }

        float hh = MathF.Atan2(Vector3.Dot(nl, f), Vector3.Dot(nl, r));
        float hv = MathF.Atan2(Vector3.Dot(nb, f), Vector3.Dot(nb, u));
        if (!(hh > 0.01f && hh < 1.55f && hv > 0.01f && hv < 1.55f))
        {
            failure = $"frustum angles out of range (half H {hh * 57.2958f:0.#} deg, half V {hv * 57.2958f:0.#} deg)";
            return false;
        }

        frustum = new ViewFrustum(pos, f, r, u, hh, hv, marginDeg * (MathF.PI / 180f));
        return true;
    }

    private static bool Norm(Vector3 v, out Vector3 n)
    {
        float l = v.Length();
        n = v;
        if (!(l > 1e-6f) || !float.IsFinite(l)) return false;
        n /= l;
        return true;
    }

    private static bool Ortho(Vector3 v, Vector3 f, out Vector3 o)
    {
        o = v - f * Vector3.Dot(v, f);
        float l = o.Length();
        if (!(l > 1e-6f) || !float.IsFinite(l)) return false;
        o /= l;
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
