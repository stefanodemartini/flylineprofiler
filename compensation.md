# Compensation Profile — How It Works

## Goal

Make every point along the fly line sink at the **same speed** (uniform sink rate), even though the line has varying diameter from tip to butt.

The compensation algorithm finds, for each 1 cm slice, the new diameter that achieves the target sink speed while keeping the slice **mass unchanged**. As a result:

| Property | Result |
|---|---|
| Sinking speed | **Uniform** — every slice sinks at the target speed (the goal) |
| Mass per slice | **Preserved** — same as the original (constraint) |
| Diameter | **Changes** per slice — the staircase shape visible on the chart |
| Density | **Changes** per slice — derived automatically from mass conservation |

The line physically weighs the same as before. What changes is the shape (and implied density distribution) needed to achieve uniform sinking.

---

## Step-by-Step Workflow

1. **Design your segments** in the Design Project panel (nodes → segment table auto-fills).

2. **Assign density** to each segment via the `Sp.W. (g/cm³)` column, or use the `Same density` checkbox plus the shared density field, or click `⚖ From weight…` to back-calculate density from a measured mass.

3. **Set the target sink speed** in the `Desired sink:` box (in/s). Typical AFTM classes:
   - Class I: 1.25–2.00 in/s (slow intermediate)
   - Class II: 2.00–3.00 in/s
   - Class III: 2.50–3.50 in/s
   - Class IV: 3.50–4.50 in/s
   - Class V: 4.50–6.00 in/s
   - Class VI: 6.00–8.00 in/s
   - Class VII: > 8.00 in/s (very fast)

4. **Click `⚖ Compensate`**. The algorithm slices each segment into 1 cm pieces and solves a force-balance bisection for each slice to find the new diameter that achieves the target sink speed while preserving mass (`ρ_orig × d_orig² = ρ_new × d_new²`). Results appear in the segment table columns: `Comp. Sink`, `Comp. Start Ø`, `Comp. End Ø`.

5. **Toggle `Comp. Profile`** to visualise the compensated staircase on the chart (see Visual Behaviour below).

6. **Toggle `Sink Map`** while `Comp. Profile` is ON to add the density overlay on top (see Visual Behaviour below).

---

## Visual Behaviour — Button States

The two toggles (`Comp. Profile` and `Sink Map`) combine to give four distinct views:

| Comp. Profile | Sink Map | What you see on the chart |
|---|---|---|
| OFF | OFF | Normal design profile (smooth tapered outline, design colour) |
| OFF | ON | Normal profile + speed heatmap — trapezoid slices coloured by local sink speed on a relative min→max scale (blue = slowest section, red = fastest) |
| ON | OFF | Compensated staircase coloured by **sink speed** using the **same min→max scale as the uncompensated Sink Map**. A well-compensated line shows a single uniform colour everywhere |
| ON | ON | Same compensated staircase + a semi-transparent **density overlay** blended on top, coloured blue (low density) → red (high density) |

### Comp. Profile ON — details

- The original design profile is faded to a ghost outline (barely visible) so the staircase shape is clearly distinguished.
- Each 1 cm step is filled with the **sink speed colour** derived from the original profile's speed range. Because the compensation forces all slices to the same speed, every step shows the same colour — that is the visual confirmation that the compensation worked correctly.
- The **orange staircase outline** is always drawn on top, making the step shape unambiguous.
- The chart legend always shows:
  - The 4-stop speed scale (same stops as the uncompensated Sink Map, for direct comparison).
  - A `★ Target X.XX in/s` entry showing where the target speed falls on that scale and what colour it maps to.
- Segment-boundary labels show the compensated diameter (mm) and position (cm) at each transition.

### Sink Map ON while Comp. Profile is ON — details

- The density overlay is painted **on top of the speed colour** at reduced opacity (~42%) so both layers are visible simultaneously.
- The density legend (ρ in g/cm³, 4 stops) is added to the chart legend alongside the speed legend.
- This view is useful for understanding **why** the staircase has the shape it does: a section that needs high density to reach the target speed will appear in the warm density colours; a section that needs low density will appear blue.

### Reading the confirmation correctly

When `Comp. Profile` is ON and the line is well-compensated:
- All steps show the **same colour** → uniform sink speed achieved.
- That colour corresponds to the target speed on the legend. If the target is in the middle of the original speed range, the colour will be somewhere in the green/yellow band. If the target is near the slow end, the colour will be blue-ish.
- If any step shows a **noticeably different colour**, the bisection for that slice did not converge (e.g., the target speed is physically unachievable for that segment's density) — inspect the `Comp. Sink` column in the segment table for the affected row.

---

## Physics

### Force balance per slice

For each 1 cm slice with original diameter `d_orig` and density `ρ_orig`, the algorithm finds the new diameter `d_new` such that the net force is zero at exactly the target speed `V_target`:

```
(π/4) · g · (ρ_orig · d_orig² − ρ_W · d_new²)  −  0.5 · Cd(d_new) · |V| · V · d_new · ρ_W  =  0
```

where:
- `Cd = 1 + 10 / Re^(2/3)` — drag coefficient for a cylinder
- `Re = |V| · d_new / ν` — Reynolds number
- `ρ_W` — water density (fresh or salt, temperature-corrected)
- `ν` — kinematic viscosity (temperature-corrected)

### Mass conservation

```
ρ_orig · d_orig²  =  ρ_new · d_new²
→  ρ_new  =  ρ_orig × (d_orig / d_new)²
```

The new density is derived from the new diameter; it is never an independent variable.

### Bisection details

- Slice length: 1 cm (fine enough to capture taper variation within a segment).
- Upper bracket: `max(30 mm, 6 × d_orig)` — guarantees the root is always bracketed for any physically plausible fly line diameter.
- Convergence tolerance: 1 × 10⁻¹² (effectively exact).
- Fallback: if no root is found in the bracket (e.g., target speed is physically unachievable), the original diameter is kept unchanged and the slice will not match the target speed.

### Two sinking speed models in the app

| Model | Used for | Physics |
|---|---|---|
| `TaperedSegmentSinkSpeed` | Segment table `Sink (in/s)` column | Rigid body: all slices of a segment move at the same speed; single combined force balance |
| `CylinderSinkSpeed` | Sink Map, Comp. Profile speed overlay | Independent cylinders: each slice finds its own terminal speed |

The compensation algorithm uses the independent-cylinder model because it solves per-slice — each slice is treated as a free body.

---

## What the Segment Table Shows After Compensation

| Column | Meaning |
|---|---|
| `Comp. Sink (in/s)` | Average effective sink speed of the compensated segment (rigid-body model; should be close to the target) |
| `Comp. Start Ø (mm)` | Compensated diameter at the start of the segment (first 1 cm slice) |
| `Comp. End Ø (mm)` | Compensated diameter at the end of the segment (last 1 cm slice) |

---

## Toolbar Controls Reference

All compensation controls are in the toolbar, always visible regardless of the Floating/Sinking setting:

| Control | Purpose |
|---|---|
| `Desired sink:` (text box, in/s) | Target uniform sink speed for the compensation |
| `⚖ Compensate` | Run the algorithm — populates `Comp.*` columns in the segment table |
| `Sink Map` (toggle) | Overlay sink speed heatmap on the original profile; OR add density overlay when Comp. Profile is also ON |
| `Comp. Profile` (toggle) | Replace the design profile view with the compensated staircase, coloured by sink speed |
