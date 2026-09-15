namespace DiametroLineaDesktop.Physics;

public enum CastingScenarioKind { OverheadCast, Distance }

/// <summary>
/// A pre-registered, immutable casting kinematic input: the rod's rigid-rotation angle φr(t) (rad,
/// measured from vertical, positive = rotating forward) plus any scenario-specific extra physics.
/// The same <see cref="RodAngleRad"/> function is reused verbatim for every line loaded under a
/// given scenario — comparing Linea_V1 and Linea_V2 on "Overhead Cast" differs only in the line's
/// own response, never in the caster's input. The two scenarios differ from *each other* by
/// design (they are different casts); "immutable" means fixed per scenario, not identical across
/// scenarios.
/// </summary>
public sealed class CastingScenario
{
    public required CastingScenarioKind Kind { get; init; }
    public required string NameIt { get; init; }
    public required AngleProfile RodAngle { get; init; }

    /// <summary>Overhead Cast: matches the *measured expert cast* in Ekander, Perkins &amp; Richards
    /// ("Development of a simulation model for fly casting and application to overhead casting",
    /// Sports Engineering) — a real robotically-measured casting stroke, not an invented one. Their
    /// 66° casting arc is specifically the one that "produces a low air drag, narrow loop... in
    /// agreement with what is expected for an expert cast", with acceleration lasting 0.42 s and
    /// deceleration 0.13 s. The previous version of this scenario used a much smaller arc (~31°)
    /// swept in a third of the time — exactly the combination the same paper identifies as
    /// "excessive rod loading" that makes the rod tip dip before rising and produces a "tailing"
    /// loop (the line crossing itself): their 51° arc case shows precisely that fault. Only the
    /// forward cast is modelled (no back cast yet): t=0 is the instant the back cast has just
    /// finished — rod near vertical, tip at ≈4.5 m (<see cref="SimulationSettings.RodPivotHeightM"/>
    /// + <see cref="SimulationSettings.RodLengthM"/>), the whole head already shot out and laid out
    /// straight behind the caster, parallel to the ground at that same ≈4.5 m height (see
    /// <see cref="ElasticRodChain.InitializeLaidOutBehindCaster"/>).</summary>
    public static CastingScenario OverheadCast() => new()
    {
        Kind = CastingScenarioKind.OverheadCast,
        NameIt = "Overhead Cast",
        RodAngle = new AngleProfile(
            startAngleRad: -0.2,          // ~ -11°, just past vertical — end of the (unmodelled) back cast
            peakAngleRad: -0.2 + 1.1519,  // 66° arc (the paper's measured expert cast)
            accelDurationS: 0.42,         // measured: acceleration phase of the 66° expert cast
            stopDurationS: 0.13),         // measured: deceleration phase of the same cast
    };

    /// <summary>Distanza: the same paper's 81° casting arc — deliberately the *wider*, more
    /// energetic stroke they studied (same measured phase timing, larger sweep), which their own
    /// results describe as producing a wider loop with more air drag from the larger vertical rod
    /// tip movement it causes; used here for "a more powerful stroke", not as an endorsement that
    /// wider is better for actual distance. Same forward-cast-only start condition as Overhead Cast;
    /// the leader/fly mass at the free end (<see cref="CastSimulator"/>) is attached for every
    /// scenario, not just this one.</summary>
    public static CastingScenario Distance() => new()
    {
        Kind = CastingScenarioKind.Distance,
        NameIt = "Distanza",
        RodAngle = new AngleProfile(
            startAngleRad: -0.2,
            peakAngleRad: -0.2 + 1.4137, // 81° arc (the paper's "wide loop" case)
            accelDurationS: 0.42,        // same measured timing as the 66° cast — only the arc differs
            stopDurationS: 0.13),
    };

    public static IReadOnlyList<CastingScenario> AllPresets() =>
        new[] { OverheadCast(), Distance() };
}

/// <summary>
/// Smooth φr(t): angular velocity is a half-sine rise from 0 to a peak ωp over the acceleration
/// phase, then a half-cosine fall from ωp back to 0 over the (shorter) stop phase — a single
/// smooth peak, continuous velocity and acceleration everywhere including at the phase boundary
/// (both halves reach acceleration = 0 exactly at the peak), zero angular acceleration jumps.
/// Matches the paper's qualitative description of an overhead cast (rapid smooth acceleration,
/// then an abrupt-but-smooth stop that triggers loop formation) without the overshoot a
/// position-matched spline produces when the two phases have very different durations: ωp is
/// solved directly from the total angle to sweep, so it is exactly the peak — never exceeded.
/// After the stop the rod holds the forward angle (the caster's hand does not move further during
/// loop propagation).
///
/// <c>ω(t) = ωp·sin(π·t / 2Ta)</c> for 0 ≤ t ≤ Ta (accel), <c>ω(t) = ωp·cos(π·t' / 2Ts)</c> for
/// 0 ≤ t' = t-Ta ≤ Ts (stop), where ωp = (peakAngleRad-startAngleRad)·π / (2·(Ta+Ts)) — the value
/// that makes ∫ω dt over both phases equal exactly the requested angular sweep.
/// </summary>
public sealed class AngleProfile
{
    private readonly double _ta, _ts;
    private readonly double _start, _peak;
    private readonly double _omegaPeak;
    private readonly double _midAngle; // angle at t = Ta, i.e. angle(0) + ∫ω over the accel phase

    public AngleProfile(double startAngleRad, double peakAngleRad, double accelDurationS, double stopDurationS)
    {
        _start = startAngleRad;
        _peak = peakAngleRad;
        _ta = accelDurationS;
        _ts = stopDurationS;
        _omegaPeak = (peakAngleRad - startAngleRad) * Math.PI / (2.0 * (_ta + _ts));
        _midAngle = _start + _omegaPeak * 2.0 * _ta / Math.PI;
    }

    /// <summary>Rod angle (rad) at time t (seconds). Holds the start angle before t=0 and the stop
    /// angle for t beyond the stop.</summary>
    public double AngleAt(double t)
    {
        if (t <= 0) return _start;
        if (t <= _ta) return _start + _omegaPeak * (2.0 * _ta / Math.PI) * (1 - Math.Cos(Math.PI * t / (2 * _ta)));
        double tp = t - _ta;
        if (tp <= _ts) return _midAngle + _omegaPeak * (2.0 * _ts / Math.PI) * Math.Sin(Math.PI * tp / (2 * _ts));
        return _peak;
    }

    public double AngularVelocityAt(double t)
    {
        if (t <= 0 || t > _ta + _ts) return 0;
        if (t <= _ta) return _omegaPeak * Math.Sin(Math.PI * t / (2 * _ta));
        double tp = t - _ta;
        return _omegaPeak * Math.Cos(Math.PI * tp / (2 * _ts));
    }

    public double AngularAccelerationAt(double t)
    {
        if (t <= 0 || t > _ta + _ts) return 0;
        if (t <= _ta) return _omegaPeak * (Math.PI / (2 * _ta)) * Math.Cos(Math.PI * t / (2 * _ta));
        double tp = t - _ta;
        return -_omegaPeak * (Math.PI / (2 * _ts)) * Math.Sin(Math.PI * tp / (2 * _ts));
    }
}
