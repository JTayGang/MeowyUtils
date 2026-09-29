# Bind/Heavy reskinning system

## What this is

A new path/skin split for screen effects, applied to two effects so far: **Bind** (tentacles)
and **Heavy** (chains). Each one now has a **Visual style** dropdown in Settings → Effects, and
they can swap materials with each other - Bind can render as chains, Heavy can render as
tentacles - because both are now built the same way underneath: an effect owns *where its
strands go* (the "shape"), and a separate, pluggable **skin** owns *what a strand looks like
when drawn* (the "material"). A future third effect just implements the same tiny interface to
join the pool.

## How to apply

Copy this `RealDebuffs/` folder over your existing one (paths match exactly):

- `RealDebuffs/Effects/StrandPath.cs` - **new**
- `RealDebuffs/Effects/IStrandSkin.cs` - **new**
- `RealDebuffs/Effects/StrandSkinKind.cs` - **new**
- `RealDebuffs/Effects/IReskinnableEffect.cs` - **new**
- `RealDebuffs/Effects/TentacleSkin.cs` - **new** (Bind's original look, extracted)
- `RealDebuffs/Effects/ChainSkin.cs` - **new** (Heavy's original look, extracted)
- `RealDebuffs/Effects/BindEffect.cs` - modified (shape/latch logic unchanged, now delegates drawing to a skin)
- `RealDebuffs/Effects/HeavyEffect.cs` - modified (shape/sway logic unchanged, now delegates drawing to a skin)
- `RealDebuffs/Effects/DrawHelpers.cs` - modified (one doc-comment updated, no logic change)
- `RealDebuffs/Configuration.cs` - modified (added `BindSkin`/`HeavySkin` + the two "Visual style" dropdowns)
- `RealDebuffs/EffectManager.cs` - modified (one `is IReskinnableEffect` check before `Draw`)

Everything else in your project is untouched. No constructors changed, so `Plugin.cs` needs no
edits, and nothing else references `BindEffect`/`HeavyEffect` directly.

## The architecture, briefly

- **`StrandPath`** - a reusable polyline + arc-length table. Whatever an effect's own shape logic
  already computed each frame (Bind's curl-and-wave tendril curve, Heavy's sagging bezier chain)
  gets written into one of these instead of a raw array, so any skin can walk it generically.
- **`IStrandSkin`** - one material. `DrawStrand(path, visual, reveal, tipFlare, ...)` draws one
  strand from its base up to `reveal` (0..1) of its length, with `tipFlare` (0..1) as a generic
  "how strongly should my growing/settling tip flourish show" knob. `TentacleSkin` and
  `ChainSkin` are the two materials today - stateless singletons, so the same instance safely
  renders strands for either effect, or several strands in one frame.
- **`StrandVisual`** - the small, skin-agnostic per-strand knobs (`Thickness`, `Seed`, `Phase`,
  `FlushStart`) an effect fills in fresh every frame.
- **`IReskinnableEffect`** - the opt-in `{ StrandSkinKind SkinKind { set; } }` that lets
  `EffectManager` push the user's chosen skin in right before `Draw`, via a plain `is` check.
  Nothing about `IScreenEffect` itself changed, so the ~20 other effects are untouched.

`BindEffect` and `HeavyEffect` kept **all** of their original behavior - layout, timing, latch
state machine, sway, sag, the works - they just hand a strand off to
`StrandSkins.Get(_skinKind).DrawStrand(...)` at the point where they used to draw it themselves.

## A note on scale

Bind's tendrils and Heavy's chains were each hand-tuned for their *own* material at their
*own* size (many thin tendrils vs. a few thick chains). Swapping the skin does **not** rescale
that - Bind-as-chains gets several fairly delicate chains, Heavy-as-tentacles gets a few very
thick tentacles. That's deliberate (shape and skin stay fully independent, which is the point
of the whole system), but it means the two swapped combinations will read as quite different
in weight from their native ones. If either one looks off once you see it in-game, it's a
one-line tuning knob:

- Bind's per-tendril thickness: the `BaseWidth` ranges in `BindEffect.BuildTendrils` (e.g.
  `0.010f, 0.019f` for the bottom edge).
- Heavy's per-chain thickness: `linkBase` in `HeavyEffect.Draw` (`minDim * 0.044f`).

Happy to tune these together once you've seen it running, or add a per-skin scale multiplier
if you'd rather the same strand read as a consistent size regardless of material.

## Verification

I don't have a way to run Dalamud/render the game here, so I couldn't watch this in-game
before handing it back. Instead I built an offline harness: a stand-in for the ImGui drawing
calls that validates every coordinate/radius/thickness is finite as it's drawn, then:

- Ran both native combinations (Bind+Tentacle, Heavy+Chain) and both swapped combinations
  (Bind+Chain, Heavy+Tentacle) across ~24 simulated seconds, 6 screen sizes/aspect ratios,
  and several fade-in/fade-out/re-cast cycles - over 15 million draw calls, zero crashes,
  zero non-finite values.
- Ran Bind and Heavy for 120 seconds flipping the skin choice every 17 frames, to make sure
  switching the dropdown mid-effect (which you can do live in Settings) never breaks anything.
- The important one: ran my refactored Bind/Heavy **against an untouched copy of your original
  code**, feeding both the identical time sequence (so they roll the identical random layout),
  and compared every single draw call between them. Both native combinations came back
  **byte-for-byte identical** - so the default, as-shipped look for both effects is provably
  unchanged; only the new swapped combinations are new code paths.

That's a strong signal the logic is sound, but it's still not the same as seeing pixels on
screen - please give both swapped combinations a look in-game before you call this done, and
let me know if anything reads wrong.
