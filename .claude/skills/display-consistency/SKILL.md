---
name: display-consistency
description: Recurring bug archetypes in the chart, segments table and PDF export. Use when Stefano reports something looks wrong on screen or in the exported PDF — phantom materials, wrong densities, misplaced boundaries, steps, illegible labels.
---

# Display bug archetypes

Nearly every "it looks wrong" report in this project has been one of these. Check them in order —
each has bitten more than once, in more than one place.

## 1. There are three independent rendering paths

| Path | Where | Produces |
|---|---|---|
| `RenderSegmentOverlay` | MainWindow.xaml.cs | the on-screen chart |
| `RenderPdfChart` | MainWindow.xaml.cs | the chart **image** embedded in the PDF |
| `FlyLinePdfExporter.Export` | Services/ | the PDF's text blocks, legends and table |

They duplicate logic and **a fix in one is not in the others**. The same bug has been found and fixed
separately in two of them on the same day. After fixing one, grep the other two for the same pattern.
The Sink Map is on-screen only — `_showSinkSpeedMap` is not read by `RenderPdfChart` at all.

## 2. Slice centre used as a true boundary

`CompSliceXsCm[i]` holds slice **centres**. Using one as a segment/zone edge is off by half a slice.
Symptoms: a small step in saved geometry; a density band reported as `5–3105 mm` when the real split
is `0–3100 mm`. Fix: anchor outer ends to the real segment/line extremes, and use the midpoint
between two adjacent centres for an internal transition (see `GetMaterialZoneSpans` for the
reference implementation — keep the others in sync with it).

## 3. Raw base density vs effective density

`ProjectSegment.SpecWeightGCm3` stays at the design's **shared base density** even where a zone
applies a different material — the real value lives in `CompSliceDensities`. Anything user-facing
(Sp.W. column, Mass, PDF density cell) must read **`EffectiveSpecWeightGCm3`**, which falls back to
the base only when no zone applies. Same trap for any grouping or comparison logic: for a *live*
zone-derived design, comparing top-level `SpecWeightGCm3` finds no variation at all.

## 4. Forced cluster count invents phantom materials

`Quantize1D(values, 4)` with `k` capped only by `arr.Length` runs k-means with k=4 even when the data
holds two real densities — returning near-duplicate centroids printed as materials that exist
nowhere. Cap `k` at the number of genuinely distinct values (tolerance ≈ 0.005 g/cm³).

Related: legends built from **interpolated stops** (`for stop in 0..3: minD + stop/3·range`) show
densities that exist nowhere in the file. Build legends from the **real distinct values** present.

## 5. Nozzle lists: too many, or missing M1

- Iterating all 4 `NozzleDefinitions` prints empty M3/M4 slots as if they were real materials —
  filter on `DensityGCm3 > 0`, and keep the original index for the `M{n}` label.
- The mirror mistake: building the list from `NozzleZones` alone **drops M1**, which is the implicit
  base material everywhere no zone covers and therefore never appears as a `NozzleIndex`. Always
  `.Append(0)`.

## 6. Colours must come from the real material

A synthetic blue→red heat ramp (`DensityColorRgb` / `DensityColor`) for something the user thinks of
as a *material* contradicts the real nozzle swatch shown elsewhere on the same page. For zones, look
the colour up from the nozzle. Reserve the heat ramp for genuinely continuous quantities (sink speed
along a taper), and for a pure physics compensation that has no authored zone colours.

## 7. Mutual exclusivity: Sink Map vs materials

Standing requirement: when the Sink Map is shown, material/density colouring **and its legend** are
hidden, and vice versa — otherwise the two tints add up and the two legends fight. This includes the
`M{n}` badges, which are the material information in another form and were initially left out of the
rule.

## 8. Load-time sync gaps

`LoadProjectFromFile` assigns private fields directly (`_sharedDensity = project.SharedDensityGCm3`),
bypassing the property setters that normally propagate derived state. `ApplySharedDensity()` — which
mirrors the base density onto `Nozzles[0]` — only runs from the setter, so after a plain load M1's
density stayed 0 and `MaybeApplyZoneDensityChange`'s `baseDensity <= 0` guard silently skipped every
zone. When adding derived state, check whether load needs to reproduce it explicitly.

## 9. Grouping for display must never touch saved data

`DisplaySegments` groups a loaded C snapshot's hundreds of ~1cm segments into one row per material
zone, purely for the table and the PDF. `ProjectSegments` and the saved geometry stay full-precision.
*"Devi mantenere quello che già avevi calcolato — devi solo riportare i dati in tabella e nei pdf."*

# How to debug these

- **Read the artefact, not the code.** Open the `.flp` with Python (`utf-8-sig`) or read the exported
  PDF directly. This settled "is the data wrong or only the display?" several times — twice the data
  was correct and the renderer was at fault, once the reverse.
- **Temporary file logging beats guessing** for runtime state you can't see: append to a file from
  the suspect method, have Stefano reproduce, read it, then remove the instrumentation. This proved
  `DisplaySegments.Count == 6` when the PDF showed 3190 rows — pointing at a stale build, not a bug.
- **Confirm he's on the current build** before chasing a "still broken" report (see `build-deploy`).
