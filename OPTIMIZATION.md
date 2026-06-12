# FlyLine Profiler — Optimization Process

## What "Optimize" does in one sentence

It redistributes segment **lengths** (never diameters) so that the impedance taper is as
uniform as possible along the line, minimising the energy the casting loop loses to
reflections on its way to the tip.

---

## 1 — The physics model

Before optimising, **Build Physics** converts the design segments into a continuous
representation of the fly line.

Each design segment (StartCm, EndCm, StartDiameterMm, EndDiameterMm, SpecWeightGCm3)
becomes a `LineSegment` with:

| Property | Source |
|---|---|
| Length | `(EndCm − StartCm) / 30.48` → feet |
| Tip / butt diameter | StartDiameterMm / EndDiameterMm |
| Taper type | Linear (tapered) or Step (cylinder) |
| Core material | GelSpun if `IsHead = true`, BraidedNylon otherwise |
| Coating material | PvcSink if `SpecWeightGCm3 ≥ 1.35`, PvcFloat otherwise |
| **Bulk density** | **SpecWeightGCm3** (user value, overrides material estimate) |

`Build()` then samples **500 points** uniformly along the total length.  At each point it
computes:

- **Outer diameter** — interpolated through the taper
- **Core / coating areas** — from minimum coating thickness
- **Mass per mm (μ)** — `SpecWeightGCm3 × 1e-6 kg/mm³ × A_total`  
  *(falls back to material-based estimate when SpecWeightGCm3 = 0)*
- **Effective Young's modulus** — volume-weighted composite of core and coating moduli
- **Mechanical impedance** — `Z = √(E_eff × ρ) × A`  where `ρ = μ / A`

---

## 2 — The score function (turnover quality)

The score is the **normalised impedance variation**:

```
score = (N−1) × Σ ( (Z[i+1] − Z[i]) / ((Z[i] + Z[i+1]) / 2) )²
```

### Physical meaning

Every point along the line where impedance changes causes the propagating loop to
partially reflect energy backwards.  The fraction of energy that continues forward at
each step is the transmission coefficient:

```
T_i = 1 − ( (Z[i+1] − Z[i]) / (Z[i+1] + Z[i]) )²  ≈  1 − (dZ / Z_avg)²
```

The score is proportional to the **total fractional energy lost to reflections**.
Minimising it = maximising the energy that reaches the tip = best turnover.

### Why normalise by Z_avg

The raw sum `Σ dZ²` over-weights the thick belly (large absolute Z) and under-weights
the thin tip.  Dividing by Z_avg makes every section contribute equally regardless of
absolute diameter.  A 5 % impedance step in the belly counts exactly the same as a 5 %
step in the tip.

### Why multiply by (N−1)

The raw sum `Σ(dZ/Z)²` scales as `1/N` (more sample points → smaller steps → smaller
sum).  Multiplying by `(N−1)` cancels that dependency so the score is the same whether
computed at 120 points (during optimisation) or 500 points (in Analyze).

### AFFTA grain weight

The 30 ft grain weight and AFFTA target are **computed and displayed** by Analyze but do
**not** enter the score.  They are purely informational.

---

## 3 — The optimisation algorithm

A **greedy hill-climber** that works exclusively on segment lengths.

```
initialScore  = ComputeTurnoverScore()        // at 120-point resolution
bestLengths   = current segment lengths

repeat (up to 80 outer iterations):
    improved = false
    for each adjacent pair (i, i+1):
        for each sign (+1, −1):
            delta = sign × step              // step decays 0.4 ft → 0.02 ft
            try: length[i]   += delta
                 length[i+1] −= delta        // total length stays constant
            Build(120 points)
            newScore = ComputeTurnoverScore()
            if newScore < bestScore:
                bestScore   = newScore
                bestLengths = current lengths
                improved    = true
            else:
                restore bestLengths
    if not improved: stop

RestoreState(bestLengths)
Build(500 points)                             // full-resolution final build
finalScore = ComputeTurnoverScore()
```

### Key properties

| Property | Value |
|---|---|
| Control variables | Segment lengths only |
| Diameters (taper shape) | **Frozen — never touched** |
| Length constraint | Total line length is preserved exactly |
| Perturbation | Transfer between adjacent pairs only |
| Step size | Decays linearly 0.4 → 0.02 ft over 80 iterations |
| Resolution during search | 120 points (fast) |
| Resolution for final result | 500 points |
| Convergence | Stops when no adjacent pair can improve the score |

### What it converges toward

In theory, the minimum of `Σ(dZ/Z)²` for a smooth profile with fixed endpoints is
achieved by an **exponential taper** — a profile where `Z(x) = Z_0 · e^(−kx)`.  An
exponential taper has equal fractional impedance change per unit length everywhere, which
gives equal transmission coefficient at every point and maximum total energy throughput.

In practice, diameters are fixed (the taper shape is not exponential in general), so the
optimiser finds the best length distribution *within* the constraint of the existing
diameter nodes.

---

## 4 — Practical effect on the design

For a given set of diameter nodes:

- Sections with a **steep** taper rate (large `dZ/Z` per cm) get **lengthened** — the
  same impedance change is spread over more distance → smaller step per step.
- Sections with a **shallow** taper rate get **shortened** — they contribute little to
  the score so the algorithm borrows length from them.
- Flat (cylinder) sections have `dZ = 0` and are never driven by the score directly —
  their length changes only as a side-effect of adjacent pair transfers.

---

## 5 — Limitations

| Limitation | Consequence |
|---|---|
| Lengths only, not diameters | Cannot change the taper shape; only re-spaces the nodes |
| Single objective | Does not account for desired head weight, sink rate, or presentation style |
| No head/running distinction | Treats the whole line as one continuous profile |
| Local search (hill-climber) | May stop at a local minimum; try re-running after manual node adjustments |
| SpecWeightGCm3 = 0 | Falls back to material-based density → mass and score less accurate |

---

## 6 — Status bar readout

After **Analyze**:
```
ΔZ/Z² 13.44 | 30ft 117.8 gr (AFFTA 140 NO) | R 0.740
```

After **Optimize**:
```
ΔZ/Z² 13.26 → 6.15 (53.6% less reflection) in 80 iter
```

- `ΔZ/Z²` — the resolution-independent turnover score (lower = better)
- `30ft X gr (AFFTA Y NO/OK)` — informational only, not in the score
- `R` — momentum ratio: mass of first 30 % of line / mass of remaining 70 %
- `% less reflection` — improvement in loop energy efficiency
