---
name: flp-files
description: Read, inspect and edit FlyLine Profiler .flp project files directly (JSON on disk). Use to verify real saved geometry, diagnose "the app shows X but the file says Y", or apply a design change (e.g. a density zone) outside the UI.
---

# Working with .flp project files

Projects live in `C:\Users\Stefano\Documents\FlyLineProfiler\Projects\` (subfolders per brand).
Plain JSON, written by `System.Text.Json` with default PascalCase naming and `WriteIndented = true`.

**Always read with `encoding='utf-8-sig'`** — the files carry a BOM and a plain `utf-8` open fails.

```python
import json
with open(path, encoding='utf-8-sig') as f:
    d = json.load(f)
```

Reading the file directly is often the fastest way to settle a question. It has repeatedly decided
"is the data wrong, or only the display?" — twice the data was perfect and the bug was in rendering.

## Two hard rules

1. **Never overwrite one of his project files** unless he explicitly says to. Write to a new
   filename (`<name> - <what changed>.flp`) and tell him. He *did* later ask for an in-place edit —
   that's fine when asked, not by default.
2. **The app does not watch files.** After editing on disk, he must close and File > Open again.
   If he says "nothing changed", this is the first thing to check.

## Structure that matters

| Field | Meaning |
|---|---|
| `DesignNodes` | `[{X: cm, Y: mm diameter}]` — the drawn taper. Segments are the gaps *between* consecutive nodes. |
| `SegmentMetadata` | one entry per node-gap: `{StartCm, EndCm, Name, SpecWeight, IsHead}`. Keyed by `StartCm` on load. Insert a node ⟹ you must split the covering metadata entry too. |
| `Name` | the taper's name (`S1`, `S2`…). Consecutive segments sharing a Name are grouped as one taper for `S{n}` chart labels — so a split segment should keep the same Name. |
| `UseSharedDensity` / `SharedDensityGCm3` | normal designs: one base material for the whole line. |
| `NozzleDefinitions` | exactly 4 slots. **Index 0 = M1 = the base material**, used everywhere no zone covers. |
| `NozzleZones` | `[{StartCm, EndCm, NozzleIndex}]` — hand-authored material zones. M1 never appears here (it's the implicit default). Zones must not overlap. |
| `ZoneDensityAdaptDiameters` | `true` = diameters get the mass-conserving adaptation; `false` = drawn diameters kept as-is (mass changes instead). Model default is `true`. |
| `IsCompensatedDerivative` | this file is a baked "C" snapshot: hundreds of ~1cm segments, each carrying its own real density in `SegmentMetadata.SpecWeight`, `UseSharedDensity` false. |
| `ScanPoints` | raw caliper scan. A file can have scan data and **no** design yet (`DesignNodes: []`) — check before assuming there's a taper to work on. |

## Gotchas that cost time

- **A zone whose density differs from base by ≤ 0.02 g/cm³ is silently ignored** (`DensityMergeThreshold`).
  A "slight" density bump must exceed it or nothing happens at all.
- **`NozzleDefinitions[0].DensityGCm3` is often 0 on disk** for a design that never used zones — its
  real base density lives in `SharedDensityGCm3`. The app syncs the two at load; if you're reasoning
  about zone behaviour from the raw file, remember the in-memory value is the synced one.
- `CompSlice*` arrays are **not** persisted — a zone-derived profile is recomputed from
  `NozzleZones` + `NozzleDefinitions` on every load.

## Recipe: add a density zone with mass conserved and no diameter step

The clean approach — anchor the zone edge to a **real design node** and bake the scaled diameters
into `DesignNodes`. A node has a single Y shared by both adjacent segments, so continuity is
structural, not calculated. See the `line-physics` skill for why this is the only step-free option.

```python
import json, math, os
path = r'...\Projects\NAME.flp'
with open(path, encoding='utf-8-sig') as f: d = json.load(f)

base_rho, zone_rho = d['SharedDensityGCm3'], 1.05
zEnd   = 310.0                       # MUST be an existing node X (a real segment boundary)
factor = math.sqrt(base_rho / zone_rho)

d['DesignNodes'] = [
    {'X': n['X'], 'Y': round(n['Y'] * factor, 4)} if n['X'] <= zEnd else n
    for n in d['DesignNodes']
]
d['NozzleDefinitions'][1]['DensityGCm3'] = zone_rho
d['NozzleDefinitions'][1]['Label']       = 'Sink tip'
d['NozzleZones'] = [{'StartCm': 0, 'EndCm': zEnd, 'NozzleIndex': 1}]
d['ZoneDensityAdaptDiameters'] = False   # geometry already baked — don't adapt again (double-shrink)

with open(path, 'w', encoding='utf-8') as f: json.dump(d, f, indent=2)
```

Scaling **both** endpoints of a linear segment by the same factor scales every interior point by it
too, so mass is conserved exactly along the whole span, not just at sampled slices.

If the zone edge does *not* fall on an existing node, insert one at that X with
`Y = interpolate(originalTaper, X) * factor`, and split the covering `SegmentMetadata` entry
(same `Name` on both halves).
