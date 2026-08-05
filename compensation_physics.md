# Compensated Profile — Physics Verification

## Concept (as stated by Stefano)

A compensated profile always derives from a base profile that has the
**same theoretical density along its entire length** (uniform ρ).

Because the fly line is tapered, the cross-sectional area — and therefore
the mass per unit length — varies along the X axis even when ρ is uniform:

```
m(x) per unit length  =  ρ · (π/4) · d(x)²
```

A thicker section has more mass per cm and sinks faster; a thinner section
has less mass and sinks slower. The result is a **non-uniform sink speed
along the NC (non-compensated) profile** even at constant density.

### Goal of the compensated profile

Produce a new profile C such that:

| Constraint      | Formula |
|-----------------|---------|
| Mass per slice preserved | ρ_orig · d_orig² = ρ_new · d_new² |
| Uniform sink speed | V(x) = V_target for every slice |

Where **V_target = max sink speed** found anywhere in the NC profile.

The new diameter d_new(x) is found by bisection on the force balance;
the required density ρ_new(x) follows from mass conservation.

---

## Implementation Verification (`SinkingSpeedCalc.CompensateProfile`)

### What the code does (slice by slice, 1 cm resolution)

```
for each 1 cm slice i:
    1. d_orig(i)  ← linear interpolation of segment taper at slice centre
    2. bisect d_new such that:
         (π/4)·g·(ρ_orig·d_orig² − ρ_W·d_new²)
         − 0.5·Cd(d_new)·|V_target|·V_target·d_new·ρ_W = 0
    3. ρ_new(i) = ρ_orig · (d_orig / d_new)²      ← mass conservation
```

### Force balance (residual solved by bisection)

```
Net gravity − Drag = 0
(π/4)·g·(ρ_orig·d_orig² − ρ_W·d_new²)  −  0.5·Cd·|V|·V·d_new·ρ_W  =  0
```

Where:
- `Cd = 1 + 10 / Re^(2/3)` — drag coefficient for a cylinder
- `Re = |V| · d_new / ν` — Reynolds number
- `ρ_W` — water density (fresh/salt, temperature-corrected)
- `ν` — kinematic viscosity (temperature-corrected)

### Mass conservation (line 93 in SinkingSpeedCalc.cs)

```csharp
dens[i] = (rhoOrig * (dOrig * dOrig) / (dNew * dNew)) / 1000.0; // g/cm³
```

✅ Correct: ρ_new = ρ_orig × (d_orig/d_new)²

### Target speed selection (`ComputeCompensation` in MainWindow.xaml.cs)

```csharp
double targetMs = ProjectSegments
    .Where(s => s.SpecWeightGCm3 > 0 && !double.IsNaN(s.SinkSpeedMs) && s.SinkSpeedMs > 0)
    .Select(s => s.SinkSpeedMs)
    .DefaultIfEmpty(0)
    .Max();
```

✅ Correct: target = max sink speed across all NC segments.

`SinkSpeedMs` for each segment uses `TaperedSegmentSinkSpeed` — the rigid-body
model where all slices of a segment move at the same equilibrium speed (physically
correct for a segment as a whole unit).

---

## What matches the concept

| Concept | Implementation | Status |
|---------|----------------|--------|
| Mass preserved slice by slice | ρ_orig·d_orig² = ρ_new·d_new² | ✅ |
| Sink speed uniform at V_target | bisection per slice | ✅ |
| V_target = max of NC profile | `Max(SinkSpeedMs)` | ✅ |
| 1 cm slice resolution | `sliceLenCm = 1.0` | ✅ |
| Water properties (fresh/salt, temp) | `WaterProps()` | ✅ |
| Drag model (cylinder, Cd formula) | `Cd = 1 + 10/Re^(2/3)` | ✅ |

---

## Design constraint: uniform material density

**The NC profile assumes a single material density ρ along the entire line.**

This is a physical reality: a manufacturer extrudes the NC line from one
material. The taper geometry varies, but ρ is constant. Compensation then
finds the per-slice density variation needed to equalise sink speed.

### Impact on the current implementation

| Area | Current state | Required change |
|------|--------------|-----------------|
| Segment table | per-segment `Sp.W. (g/cm³)` column, editable | Remove or replace with a single line-level density field |
| `CompensateProfile` input | `seg.SpecWeightGCm3` per segment | Single `rho` shared by all segments |
| `TaperedSegmentSinkSpeed` | per-segment `densityGcm3` | Single `rho` |
| UI | shared-density toggle (partial) | Always uniform — no per-segment override |

The physics engine (`SinkingSpeedCalc`) already supports this: passing the
same `densityGcm3` to every call is all that's needed. The work is
entirely in the UI and the data model.

---

## Numerical details

| Parameter | Value |
|-----------|-------|
| Slice length | 1 cm |
| Bisection tolerance | 1 × 10⁻¹² |
| Max bisection iterations | 100 |
| Upper diameter bracket | max(30 mm, 6 × d_orig) |
| Fallback (no root found) | d_new = d_orig, ρ_new = ρ_orig |
