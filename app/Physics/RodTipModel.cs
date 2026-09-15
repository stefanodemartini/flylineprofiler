namespace DiametroLineaDesktop.Physics;

/// <summary>
/// Drives the position/velocity of the line's proximal node (node 0) from the rod: a prescribed
/// rigid rotation φr(t) (the scenario's <see cref="AngleProfile"/>, the caster's sole input) plus
/// the rod's own single-mode flexible-tip response, using the paper's actual mode-shape equations
/// (Gatti-Bono &amp; Perkins, eq. 27 and 29) rather than a guessed approximation — an earlier version
/// of this class guessed at these coefficients because the source PDF's automated text extraction
/// had corrupted that region; a cleaner Markdown extraction of the same paper later recovered them
/// exactly.
///
/// What is implemented, exactly per the paper:
/// - The first cantilever mode shape ṡ₁(x) (eq. 27) and its two mode integrals (eq. 29), computed
///   once per run by numerical quadrature — no more guessed proportionality constants.
/// - The centrifugal softening term (ω₁²-φ̇ᵣ²) in the modal ODE — the base's own rotation reduces
///   the mode's effective stiffness; an earlier version had only ω₁², i.e. always assumed the rod
///   was at rest.
/// - The exact base-angular-acceleration forcing coefficient C₁ = ∫x·ṡ₁dx / ∫ṡ₁²dx (eq. 29) — an
///   effective lever arm computed from the real mode shape, not a fitted fraction of ρrArlr².
/// - A small added structural damping term on the modal ODE. The paper's eq. 29 has none (an
///   idealized, undamped Euler-Bernoulli beam); a real composite rod blank does dissipate energy,
///   so a light damping ratio is kept as a physically-motivated addition, not a paper term.
///
/// What is deliberately dropped: every term in eq. (29) and (37)-(38) driven by the line's own tip
/// shear Vʳ (and its derivatives). A first attempt fed the line's reaction force back into the rod
/// — both as an ODE forcing term and as the paper's quasi-static Vʳlr³/(3ErIr) tip-deflection term
/// — and the coupled system diverged within milliseconds every time. The mechanism: this class's
/// Vʳ can only come from an *explicit* per-step force estimate off the line's PBD-constrained
/// positions (see <see cref="ElasticRodChain"/>), which is stiff and can carry large transient
/// noise; the paper's Vʳ instead comes out of a single, fully-implicit two-point BVP solve each
/// step, which has no equivalent transient to amplify. Feeding an explicit, noisy force estimate
/// straight into a position offset (the quasi-static term) or into an undamped-in-this-regard ODE
/// forcing is exactly the shape of a numerical instability, and that is what happened. The
/// controlled centrifugal + base-acceleration terms above depend only on the *prescribed* φr(t) —
/// smooth and bounded by construction — so they carry none of that risk, and are kept.
/// </summary>
public sealed class RodTipModel
{
    private readonly CastingScenario _scenario;
    private readonly SimulationSettings _settings;

    private readonly double _lr;
    private readonly double _omega1;      // first-mode natural frequency at rest (rad/s)
    private readonly double _baseForcingLeverArm; // C1 = ∫x·v1 dx / ∫v1² dx  (metres)
    private readonly double _v1AtTip;     // mode shape value at x = lr

    private double _mu1, _mu1Dot;         // modal coordinate and its rate (native, non-tip-normalized units)

    private const double Lambda1L = 1.8751040687; // first eigenvalue of a clamped-free beam (dimensionless)
    // Added beyond the paper — real rod material damping (an idealized undamped Euler-Bernoulli
    // beam, per eq. 29, rings forever). 0.05 was too light: the isolated tip flex peaks at ~22 cm
    // right at the stop (a genuinely large swing, not the sub-centimetre figure an earlier, buggy
    // standalone diagnostic script reported — that script called Advance(t, dt) once per *printed*
    // sample with t jumping by far more than dt each call, so it only ever integrated a tiny
    // fraction of the elapsed time it was reporting; CastSimulator.Run, and this default, were never
    // affected, only that one diagnostic's read-out) — and took the better part of a second to decay
    // at 0.05, which a modern high-modulus graphite blank would settle out of far faster ("fast
    // action" rods are prized for exactly that). Measured against the line's own loop-shape quality
    // (X-direction reversals / max local turn angle) rather than assumed: 0.25 damped the rod
    // faster but made the line shape slightly worse (more reversals, one sharp spike to ~110°);
    // 0.12 is the value that both damps the visible counterflex *and* keeps the line's loop
    // slightly cleaner than 0.05 did. Not a coincidence-free result — the rod tip genuinely drives
    // node 0's boundary condition, so how it rings does feed into the line — just not in the
    // direction "more rod damping always helps" would predict.
    private const double DampingRatio = 0.12;

    public RodTipModel(CastingScenario scenario, SimulationSettings settings)
    {
        _scenario = scenario;
        _settings = settings;
        _lr = settings.RodLengthM;
        double ei = settings.RodBendingStiffnessNm2;
        double mu = settings.RodMassPerLengthKgM;
        double lambda1 = Lambda1L / _lr;

        (double intVV, double intXV) = IntegrateModeShape(lambda1, _lr);
        _baseForcingLeverArm = intXV / intVV;
        _v1AtTip = ModeShape(_lr, lambda1, _lr);

        _omega1 = lambda1 * lambda1 * Math.Sqrt(ei / mu);
    }

    /// <summary>Eq. (27): the first clamped-free cantilever mode shape.</summary>
    private static double ModeShape(double x, double lambda1, double lr)
    {
        double c = Math.Cos(lambda1 * lr), ch = Math.Cosh(lambda1 * lr);
        double s = Math.Sin(lambda1 * lr), sh = Math.Sinh(lambda1 * lr);
        return (c + ch) * (Math.Sin(lambda1 * x) - Math.Sinh(lambda1 * x))
             - (s + sh) * (Math.Cos(lambda1 * x) - Math.Cosh(lambda1 * x));
    }

    /// <summary>Simpson's rule over [0, lr] for ∫v1²dx and ∫x·v1dx — smooth analytic integrands, so
    /// a fixed, generous subdivision count is accurate to far better precision than anything else
    /// in this model.</summary>
    private static (double intVV, double intXV) IntegrateModeShape(double lambda1, double lr)
    {
        const int n = 400; // even
        double h = lr / n;
        double sumVV = 0, sumXV = 0;
        for (int i = 0; i <= n; i++)
        {
            double x = i * h;
            double w = (i == 0 || i == n) ? 1 : (i % 2 == 1 ? 4 : 2);
            double v = ModeShape(x, lambda1, lr);
            sumVV += w * v * v;
            sumXV += w * x * v;
        }
        return (sumVV * h / 3.0, sumXV * h / 3.0);
    }

    /// <summary>Rigid-body (undeflected "shadow beam") tip position at time t, pivot at the origin.</summary>
    public Vec2 ShadowTipPosition(double t)
    {
        double phi = _scenario.RodAngle.AngleAt(t);
        return new Vec2(_lr * Math.Sin(phi), _settings.RodPivotHeightM + _lr * Math.Cos(phi));
    }

    /// <summary>
    /// Advances the flexible-mode ODE by one step and returns the actual (rigid + flexible) tip
    /// position and velocity, to be used as the line's node-0 boundary condition. Driven purely by
    /// the prescribed φr(t) — see class remarks for why the line's own tip-shear feedback is not
    /// part of this.
    /// </summary>
    public (Vec2 position, Vec2 velocity) Advance(double t, double dt)
    {
        double phi = _scenario.RodAngle.AngleAt(t);
        double phiDot = _scenario.RodAngle.AngularVelocityAt(t);
        double phiDdot = _scenario.RodAngle.AngularAccelerationAt(t);

        double mu1Ddot = -(_omega1 * _omega1 - phiDot * phiDot) * _mu1
                          - 2 * DampingRatio * _omega1 * _mu1Dot
                          - phiDdot * _baseForcingLeverArm;
        _mu1Dot += mu1Ddot * dt;
        _mu1 += _mu1Dot * dt;

        double flex = _mu1 * _v1AtTip;

        // "lateral" is perpendicular to the rod's instantaneous axis — the one direction the single
        // flexible mode is allowed to deflect in.
        var lateral = new Vec2(Math.Cos(phi), -Math.Sin(phi));
        var velocityDir = new Vec2(Math.Sin(phi), Math.Cos(phi));

        var shadow = ShadowTipPosition(t);
        var position = shadow + lateral * flex;

        var shadowVel = velocityDir * (_lr * phiDot);
        // d/dt(lateral)·flex + lateral·flexDot — the lateral direction itself rotates with φ.
        var lateralDot = new Vec2(-Math.Sin(phi) * phiDot, -Math.Cos(phi) * phiDot);
        var velocity = shadowVel + lateralDot * flex + lateral * _mu1Dot * _v1AtTip;

        return (position, velocity);
    }
}
