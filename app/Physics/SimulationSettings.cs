namespace DiametroLineaDesktop.Physics;

/// <summary>
/// Physical parameters the casting simulator needs that a <see cref="Models.FlyLineProject"/> does
/// not carry (bending stiffness, drag coefficients, air, fly). Deliberately kept out of the .flp
/// schema — these are simulator-run settings, editable in the Simulator window, not part of a
/// line's manufacturing spec. Defaults are documented, literature-order-of-magnitude values, not
/// measurements of any specific real line.
/// </summary>
public sealed class SimulationSettings
{
    /// <summary>Young's modulus of the line core (Pa). Gatti-Bono & Perkins' yarn-rod experiment
    /// used 0.5e9 Pa for a yarn thread; a real fly line core (braided/monofilament) is stiffer. This
    /// default is a starting point, meant to be tuned in the UI, not a measured value.</summary>
    public double YoungsModulusPa { get; set; } = 0.9e9;

    /// <summary>Normal (bluff-body) drag coefficient, Cd1 in the paper's notation — the paper's own
    /// Table 1 value for their yarn-rod experiment.</summary>
    public double NormalDragCoeff { get; set; } = 1.0;

    /// <summary>Tangential (skin-friction) drag coefficient, Cd3 in the paper's notation. The
    /// paper's own Table 1 value is 0.01, and at that value — with an *invented*, fast/short casting
    /// arc — this chain's simplifications (angle-based XPBD bending instead of the paper's implicit
    /// two-point BVP solve, finite constraint iterations, no rotary inertia) left curvature waves
    /// reflecting back and forth for long enough to fold into extra loops: 10 spurious direction
    /// reversals, turn angles over 140°. That symptom turned out to be mostly a real effect with the
    /// wrong cause diagnosed first: Ekander, Perkins &amp; Richards (Sports Engineering) identify
    /// "tailing loops" as the genuine consequence of too small/fast a casting arc (their 51° case),
    /// distinct from a solver artefact. Switching <see cref="CastingScenario"/> to their *measured*
    /// 66°/81° arcs and real phase timing (0.42 s accel / 0.13 s stop) already brings the reversal
    /// count most of the way down on its own. With that fix in place, a Cd3 sweep against the new
    /// timing (0.01/0.05/0.1/0.4, checked at t=0.5/0.8/1.2/1.6 s) found 0.1 both cleaner and more
    /// consistent over time than the earlier 0.4 hack (1-1-1-4 reversals / ≤26° vs 0.4's 0-6-6-7 /
    /// up to 45°) — realistic kinematics let the compensation shrink from 40× the paper's value to
    /// 10×, though not all the way back to 0.01-0.05, which still shows reversals climbing late in
    /// the run (2-10 by t=1.6s) as the residual solver simplifications reassert themselves over a
    /// longer propagation. Revisit toward 0.01 if the bending/solver scheme is ever replaced with
    /// something closer to the paper's own implicit method.</summary>
    public double TangentialDragCoeff { get; set; } = 0.1;

    public double AirDensityKgM3 { get; set; } = 1.225;
    public double GravityMs2 { get; set; } = 9.81;

    /// <summary>Leader/fly point mass attached at the free end (kg) — every scenario gets one
    /// (<see cref="CastSimulator"/>), since a real cast never presents a completely bare line tip.
    /// Without it the free end's own mass is a few hundredths of a gram (whatever sliver of taper
    /// the last node samples), light enough that drag and bending push it into visibly erratic
    /// motion with almost no inertia behind it. ~0.5 g default leader+fly.</summary>
    public double FlyMassKg { get; set; } = 0.0005;
    public double FlyDiameterMm { get; set; } = 5.0;
    public double FlyDragCoeff { get; set; } = 1.0;

    /// <summary>Rod length (m) and pivot ("caster's hand") height above the ground/water (m). Sized
    /// so a near-vertical rod (the scenarios' start angle, i.e. the position the back cast leaves it
    /// in) puts the tip at ≈4.5 m, matching a real back-cast-stop height.</summary>
    public double RodLengthM { get; set; } = 2.7;
    public double RodPivotHeightM { get; set; } = 1.8;

    /// <summary>Rod bending stiffness Er·Ir (N·m²) and mass per unit length ρr·Ar (kg/m) — feed the
    /// single-mode flexible-tip response in <see cref="RodTipModel"/> (paper eq. 27-29, 37-38).
    /// The paper's own Table 2 values (500 GPa graphite, ~2.5 mm hollow tube) are for their small
    /// laboratory "yarn rod" (1.22 m of the *upper half* of a fly rod, ω₁≈34 rad/s) — a full 2.7 m
    /// rod at the same 500 GPa but a thicker, tapered cross-section needs an EI an order of
    /// magnitude higher than a naive "same material" guess to reach a comparable natural frequency;
    /// 25 N·m² (ω₁≈11 rad/s) put ω₁ close enough to the cast's own angular rate that eq. (29)'s
    /// centrifugal term (ω₁²-φ̇ᵣ²) and base-acceleration forcing drove tens of centimetres of tip
    /// deflection even with no load from the line at all — not a bug, just too flexible a rod for
    /// this equation at this angular rate. 1000 N·m² (ω₁≈70 rad/s) keeps the same isolated-tip test
    /// under ~10 cm, consistent with a stiff modern graphite blank.</summary>
    public double RodBendingStiffnessNm2 { get; set; } = 1000.0;
    public double RodMassPerLengthKgM { get; set; } = 0.05;

    /// <summary>Discretization step along the line (cm) — the DER chain's node spacing. 2 cm gives
    /// a full fly line a few hundred nodes, resolving loop curvature well without the cost of going
    /// much finer. Bending is now an XPBD constraint (<see cref="ElasticRodChain"/>), not an
    /// explicit spring force, so — unlike an earlier version of this model — this spacing is a pure
    /// resolution/cost dial again: it no longer interacts with <see cref="TimeStepS"/> through a
    /// bending-spring natural frequency, because there is no such explicitly-integrated spring left
    /// to excite.</summary>
    public double NodeSpacingCm { get; set; } = 2.0;

    /// <summary>Physics sub-step (s). Bending and inextensibility are both solved as XPBD
    /// constraints (<see cref="ElasticRodChain"/>), which are unconditionally stable regardless of
    /// this value — there is no bending-spring natural frequency for a small Δt to chase anymore, so
    /// this can stay at a cost-driven value rather than an accuracy-driven one. Drag is still
    /// integrated explicitly, but it is not stiff enough at these speeds/diameters to need a finer
    /// step than this.</summary>
    public double TimeStepS { get; set; } = 0.0005;

    /// <summary>Upper time cap (s) — the real end of most runs is the water-plane stop in
    /// <see cref="CastSimulator"/>, reached well before this. Deliberately generous: the paper's
    /// own experiment used a 2.13 m "yarn rod" whose loop fully turns over in under a second, but a
    /// real fly line here is 5-10× longer, and loop propagation takes proportionally longer to
    /// travel end to end — cutting the run short at the yarn-rod's timescale would stop the
    /// simulation mid-turnover, which looks like unresolved swinging because it *is* unresolved,
    /// not because the swinging itself is wrong.</summary>
    public double DurationS { get; set; } = 4.0;

    /// <summary>Cap on recorded animation frames (the sim itself always runs at <see cref="TimeStepS"/>
    /// resolution; this only controls how many frames are kept for playback/plotting).</summary>
    public int MaxRecordedFrames { get; set; } = 300;
}
