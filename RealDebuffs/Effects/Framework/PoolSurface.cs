using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A pool surface held as standing-wave modes: gravity-wave dispersion, damping that grows with k^2, reflecting walls, fixed volume.</summary>
internal sealed class PoolSurface
{
    private const float RootTau = 2.5066283f;  // sqrt(2 pi)

    public float[] H = [];  // surface height per column, px, up is positive

    private int _n, _stride, _modes;
    private float _len;
    private float[] _k = [], _cos = [], _a = [], _v = [], _t = [], _acc = [];

    // per-mode step terms, rebuilt only when depth, dt or the liquid change
    private float[] _blow = [], _w2 = [], _damp = [], _decay = [], _cosw = [], _sinw = [];
    private float _cDepth = -1f, _cDt, _cG, _cNu, _cDrag;

    /// <summary>Sizes the surface to n columns across len px, all at rest. Modes run down to a 4-column wavelength.</summary>
    public void Begin(int n, float len)
    {
        if (n != _n || MathF.Abs(len - _len) > 0.5f)
        {
            _n = n; _len = len;
            _modes = Math.Max(1, (n - 1) / 2);
            _stride = (n + 15) & ~15;
            H = new float[n];
            _acc = new float[_stride];
            int m1 = _modes + 1;
            _a = new float[m1]; _v = new float[m1]; _t = new float[m1]; _k = new float[m1];
            _blow = new float[m1]; _w2 = new float[m1]; _damp = new float[m1]; _decay = new float[m1]; _cosw = new float[m1]; _sinw = new float[m1];
            _cos = new float[m1 * _stride];
            float dx = len / (n - 1);
            for (int m = 1; m <= _modes; m++)
            {
                _k[m] = MathF.PI * m / len;
                for (int i = 0; i < n; i++) _cos[m * _stride + i] = MathF.Cos(_k[m] * i * dx);
            }
        }
        Array.Clear(_a); Array.Clear(_v); Array.Clear(_t); Array.Clear(H);
        _cDepth = -1f;
    }

    /// <summary>Strikes the surface under a Gaussian of width reach at x; positive push (px/s) is up. A blow is a pressure impulse, so long swells barely feel it.</summary>
    public void Kick(float x, float reach, float push) => Spread(_v, x, reach, push, reach);

    /// <summary>Presses the surface down under a Gaussian of width reach at x for this step only (negative lifts it); it settles to depth px, as water under a steady pressure does.</summary>
    public void Press(float x, float reach, float depth) => Spread(_t, x, reach, -depth, 0f);

    // adds amount * Gaussian to a per-mode array as cosine-series coefficients; with a nonzero blow, mode m is weighted by blow * k tanh(k depth)
    private void Spread(float[] into, float x, float reach, float amount, float blow)
    {
        if (_modes == 0 || reach <= 0f || amount == 0f) return;
        float c1 = MathF.Cos(MathF.PI * x / _len), q = MathF.PI * reach / _len;
        float r = MathF.Exp(-0.5f * q * q), r2 = r * r;
        float amp = 2f * amount * reach * RootTau / _len;
        float prev = 1f, cur = c1, g = r, t = r;  // cos(m theta) by recurrence; g is the Gaussian's share of mode m
        for (int m = 1; m <= _modes && g > 1e-4f; m++)
        {
            into[m] += amp * g * cur * (blow > 0f ? blow * _blow[m] : 1f);
            (prev, cur) = (cur, 2f * c1 * cur - prev);
            t *= r2; g *= t;
        }
    }

    /// <summary>Advances every awake mode exactly, then rebuilds H. g in px/s^2, depth in px, nu in px^2/s, drag in 1/s.</summary>
    public void Step(float dt, float depth, float g, float nu, float drag)
    {
        if (MathF.Abs(depth - _cDepth) > 0.4f || MathF.Abs(dt - _cDt) > 0.01f * dt || g != _cG || nu != _cNu || drag != _cDrag)
            Rebuild(dt, depth, g, nu, drag);

        Array.Clear(_acc);
        for (int m = 1; m <= _modes; m++)
        {
            float a = _a[m], v = _v[m], eq = _t[m];
            if (a == 0f && v == 0f && eq == 0f) continue;
            _t[m] = 0f;
            a -= eq;  // the mode swings about where the load wants it

            float w2 = _w2[m], d = _damp[m], e = _decay[m], c = _cosw[m], s = _sinw[m];
            float an = e * (a * c + (v + d * a) * s), vn = e * (v * c - (w2 * a + d * v) * s);
            if (an * an * w2 + vn * vn < 1e-9f && eq == 0f) { _a[m] = _v[m] = 0f; continue; }
            an += eq;
            _a[m] = an; _v[m] = vn;

            var amp = new Vector<float>(an);
            int row = m * _stride;
            for (int i = 0; i < _stride; i += Vector<float>.Count)
                (new Vector<float>(_acc, i) + amp * new Vector<float>(_cos, row + i)).CopyTo(_acc, i);
        }
        Array.Copy(_acc, H, _n);
    }

    // each mode is a damped oscillator, w^2 = g k tanh(k depth), damping d; over dt it moves by exactly e * (cos, sin / wd) (cosh, sinh when overdamped)
    private void Rebuild(float dt, float depth, float g, float nu, float drag)
    {
        (_cDepth, _cDt, _cG, _cNu, _cDrag) = (depth, dt, g, nu, drag);
        for (int m = 1; m <= _modes; m++)
        {
            float k = _k[m], th = MathF.Tanh(k * depth), w2 = g * k * th, d = drag + nu * k * k, disc = w2 - d * d, c, s;
            if (disc > 1e-6f)
            {
                float wd = MathF.Sqrt(disc);
                (s, c) = MathF.SinCos(wd * dt);
                s /= wd;
            }
            else if (disc < -1e-6f)
            {
                float wd = MathF.Sqrt(-disc), x = MathF.Min(wd * dt, 60f);
                c = MathF.Cosh(x); s = MathF.Sinh(x) / wd;
            }
            else { c = 1f; s = dt; }
            (_blow[m], _w2[m], _damp[m], _decay[m], _cosw[m], _sinw[m]) = (k * th, w2, d, MathF.Exp(-d * dt), c, s);
        }
    }
}
