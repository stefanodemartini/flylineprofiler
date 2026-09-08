---
name: line-physics
description: Domain model for fly-line density, mass conservation, sink speed and the two compensation systems. Use before changing anything under Services/SinkingSpeedCalc.cs, ApplyZoneDensities, the Compensate flow, or when reasoning about diameters, densities and steps in the profile.
---

# Fly-line physics and compensation model

## Mass conservation — the central identity

Changing a section's material while keeping its mass means

```
ρ_new · d_new² = ρ_orig · d_orig²        ⟹   d_new = d_orig · √(ρ_orig / ρ_new)
```

Denser material ⟹ thinner line for the same mass. Because the factor `√(ρ_orig/ρ_new)` is a
*constant* for a constant target density, scaling **both endpoints** of a linear taper segment by it
scales every interior point by it too — mass is then conserved exactly at every x, not merely at
sampled slices.

## The unavoidable-step theorem

If density jumps discontinuously at a zone edge and mass is conserved pointwise, then
`d(x) = d_orig(x)·√(ρ_orig/ρ(x))` **must** jump there too — `d_orig` is continuous, so the jump comes
entirely from ρ. You cannot have all three of: exact density value, exact mass, continuous diameter,
across a hard material boundary. Only three ways out:

1. **Anchor the zone edge to a real design node and bake the scaled diameters into `DesignNodes`.**
   A node carries one Y used by both adjacent segments ⟹ continuity by construction, mass still
   exact. *This is the approach Stefano chose and it is the right default.*
2. Ramp the density smoothly across a short band (`ZoneTransitionCm`, 10 cm) so ρ — and therefore d —
   is continuous. Costs the "exactly ρ up to exactly X" guarantee near the edge.
3. Don't adapt diameters at all (`ZoneDensityAdaptDiameters = false`) — continuous by definition, but
   mass changes. Only acceptable when the geometry was already baked correctly (as in option 1).

## Constants and models

- `RhoFloor = 0.94 g/cm³` — practical minimum manufacturable density; slices clamped to it are flagged.
- Fresh water ≈ 998.2 kg/m³ @ 20 °C (polynomial), salt = `1027 − 0.2·t`, ν salt = 1.07·ν fresh.
- Drag: `Cd = 1 + 10/Re^(2/3)`; terminal speed solved by bisection.
- A section denser than water always yields a **positive** sink speed — `SinkSpeedText` shows
  "floating" only when the solved speed is ≤ 0. Seeing "floating" on a >1.0 g/cm³ section means the
  density never reached the calculation, not that the physics failed.

Three speed models, deliberately different — don't swap them:

| Function | Model | Used for |
|---|---|---|
| `CylinderSinkSpeed` | isolated infinite cylinder at one diameter/density | Sink column endpoints, Sink Map colouring |
| `RigidBodySinkSpeed` | many slices, own diameter+density each, one shared equilibrium speed | a zone-derived segment's real speed |
| `DiameterForTargetSinkSpeedMm` | inverse of the first | solving diameter for a target speed |

## Two independent compensation systems

**1. Compensate button (physics-driven).** Continuous per-slice solve for a target speed, then
quantised to at most 4 real materials, then written as a **separate "C" file** with its own
full-precision geometry (~1 node per cm). *"C e NC sono due file distinti, completamente"* — a C file
is never a recipe recomputed on load; its geometry **is** the answer.

In the quantisation, **diameter is left exactly at the ideal continuous value** — no mass-conserving
correction per assigned material. That correction jumps wherever the nearest-material assignment
jumps, which produced visible diameter steps at material boundaries. Stefano's explicit ruling:
*accept a larger speed deviation, never a diameter jump.* The achieved speed absorbs the whole
discretisation error.

**2. Nozzle zones (hand-authored).** Up to 4 nozzles (M1–M4); M1 is the base material used wherever
no zone covers. Zones are a **live overlay on the NC design**, recomputed by `ApplyZoneDensities` on
every load — never forked into a separate file, and they never touch `DesignNodes`. Each nozzle owns
its colour and density; a colour/density change may land anywhere along a taper, not only at a slope
change.

## Labelling rule (mandatory, not a preference)

- **`S{n}`** identifies a **physical taper shape** only — grouped by `ProjectSegment.Name`. It must
  *never* encode a material or colour change.
- **`M{n}`** identifies a **material** — a badge in that nozzle's real colour.

The two must never mix in one label, even when they happen to coincide on a given line.

## Slice arrays

`CompSliceXsCm[i]` is the offset of slice *i*'s **centre** from `seg.StartCm` — not its edge. Treating
a centre as a boundary is off by half a slice and has caused visible steps and wrong positions three
separate times. To get a true boundary value: extrapolate outward from the two nearest centres, or
use the midpoint between adjacent centres for an internal transition, and anchor the outer ends to
the real segment/line extremes.
