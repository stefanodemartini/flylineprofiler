namespace DiametroLineaDesktop.Physics;

/// <summary>
/// The Discrete Elastic Rod chain: N+1 2D nodes representing the line in the vertical casting
/// plane, sampled from a <see cref="LineArcSampler"/> at a fixed spacing. Implements the
/// Gatti-Bono &amp; Perkins physics (tension, bending, Morison drag, weight) — the same 2D plane
/// their own model uses — with a Discrete-Elastic-Rods-style numerical scheme, documented
/// deliberately different from the paper's own solver where that is more tractable for an explicit
/// node chain — see remarks on each piece:
///
/// - Inextensibility AND bending are both hard constraints solved by XPBD position projection
///   (Macklin, Müller, Chentanez, "XPBD: Position-Based Simulation of Compliant Constrained
///   Dynamics", 2016), not forces integrated explicitly. An earlier version integrated bending as
///   an explicit spring force (F=ma); on this chain's very light, thin nodes that spring's natural
///   frequency (~550-650 rad/s near the rod end) needed an impractically small time step for good
///   accuracy. Moving it to XPBD fixed that, but the first XPBD version used the bending constraint
///   C = p_i - 0.5(p_{i-1}+p_{i+1}) (straight-line-in-Cartesian-coordinates) — a small-angle
///   linearization of "zero curvature" that stops tracking the true reduce-curvature direction once
///   the local turn gets large, which a loop's tightest bend (30-90°+) reaches routinely: it let
///   spurious extra loops persist in the shape instead of resolving into one clean bend. The
///   constraint actually used now is C = θ, the exact signed turning angle (atan2 of the cross and
///   dot products of the two adjacent edges), with the exact analytic gradient of that angle — valid
///   up to ±180°, not just for small deviations from straight. Compliance = l̄/EI, matching the same
///   M=EI·κ constitutive law as before (now as an angular stiffness, EI/l̄, since the constraint is
///   an angle rather than a length). Same physics (eq. 2-3 for inextensibility, M=EI·κ for bending),
///   solved together in the same iterative loop, unconditionally stable at any time step.
/// - Drag: exactly the paper's Morison formulation, normal + tangential components with the same
///   asymmetric π factor, integrated explicitly (not stiff, no equivalent issue).
/// - Bending damping: a small implicit velocity relaxation toward the local neighbour average,
///   with a rate derived from a physical time constant (not a fixed per-step fraction) so it does
///   not compound differently depending on the time step or node spacing chosen.
/// </summary>
public sealed class ElasticRodChain
{
    public Vec2[] Positions { get; }
    public Vec2[] Velocities { get; }
    public double[] NodeMassesKg { get; }
    public double[] DiametersMm { get; }
    public double[] DensitiesGCm3 { get; }
    public double[] RestLengthsM { get; }   // length NodeCount-1
    public double[] BendStiffnessNm2 { get; } // EI at each node's own diameter
    public double[] ArcLengthsCm { get; private set; } = Array.Empty<double>(); // for zone-color lookup in the UI

    /// <summary>Diagnostic only: how much each edge "wanted" to stretch (+) or compress (-) from
    /// external forces alone, before the hard inextensibility constraint corrected it back to exact
    /// length — a proxy for whether that stretch of line is in tension (+) or has gone slack/would
    /// buckle under compression (-) a real line has no way to resist. Not fed back into the
    /// dynamics; read after <see cref="Step"/> for display/analysis only.</summary>
    public double[] EdgeStretchM { get; }

    public int NodeCount => Positions.Length;

    /// <summary>Extra point mass at the free end (kg) — 0 unless a fly is attached (Distance scenario).</summary>
    public double FlyMassKg { get; set; }
    public double FlyDiameterMm { get; set; }
    public double FlyDragCoeff { get; set; }

    private readonly SimulationSettings _settings;
    private const int ConstraintIterations = 40;
    private readonly double[] _bendingLambda;

    /// <summary>Bending damping time constant (s) — an added, physically-motivated material
    /// damping (real fly line cores dissipate energy; the paper's own eq. 29-equivalent bending law
    /// does not model this), expressed as a time constant rather than a fixed per-step fraction so
    /// the total damping over real elapsed time stays the same regardless of the chosen time step —
    /// a per-step constant burned this project once already (see git history/session notes): it
    /// compounds differently depending on how many steps you take to cover the same second.</summary>
    private const double BendingDampingTimeConstantS = 0.02;

    private ElasticRodChain(int nodeCount, SimulationSettings settings)
    {
        Positions = new Vec2[nodeCount];
        Velocities = new Vec2[nodeCount];
        NodeMassesKg = new double[nodeCount];
        DiametersMm = new double[nodeCount];
        DensitiesGCm3 = new double[nodeCount];
        RestLengthsM = new double[nodeCount - 1];
        BendStiffnessNm2 = new double[nodeCount];
        EdgeStretchM = new double[nodeCount - 1];
        _bendingLambda = new double[Math.Max(0, nodeCount - 2)];
        _settings = settings;
    }

    public static ElasticRodChain Build(LineArcSampler sampler, SimulationSettings settings)
    {
        double[] arcsCm = sampler.SampleArcLengths(settings.NodeSpacingCm);
        if (arcsCm.Length < 2)
            throw new InvalidOperationException("Line profile too short to build a simulation chain.");

        // The .flp convention is tip=0, reel=max (MANUALE.md). Node 0 of this chain is the end
        // driven by the rod tip, and the last node is the free end a fly gets attached to — so node
        // 0 must be the REEL end (arc length = total) and the last node the TIP/fly end (arc length
        // = 0), reversing the sampler's natural 0→total order. Building it the other way round means
        // simulating the heavy butt/reel section as the "fly end" while the actual tip sits pinned
        // at the rod — which is exactly backwards and produces a nonsensical loop.
        Array.Reverse(arcsCm);

        var chain = new ElasticRodChain(arcsCm.Length, settings) { ArcLengthsCm = arcsCm };
        for (int i = 0; i < arcsCm.Length; i++)
        {
            chain.DiametersMm[i] = sampler.DiameterMmAt(arcsCm[i]);
            chain.DensitiesGCm3[i] = sampler.DensityGCm3At(arcsCm[i]);
            double dM = chain.DiametersMm[i] / 1000.0;
            double jM4 = Math.PI * Math.Pow(dM, 4) / 64.0;
            chain.BendStiffnessNm2[i] = settings.YoungsModulusPa * jM4;
        }
        for (int i = 0; i < arcsCm.Length - 1; i++)
            chain.RestLengthsM[i] = Math.Abs(arcsCm[i + 1] - arcsCm[i]) / 100.0;

        // Lumped node mass: half of each adjacent edge's mass-per-length × edge length.
        for (int i = 0; i < arcsCm.Length; i++)
        {
            double m = 0;
            if (i > 0) m += 0.5 * MuAt(chain, i - 1, i) * chain.RestLengthsM[i - 1];
            if (i < arcsCm.Length - 1) m += 0.5 * MuAt(chain, i, i + 1) * chain.RestLengthsM[i];
            chain.NodeMassesKg[i] = Math.Max(m, 1e-9);
        }
        return chain;

        static double MuAt(ElasticRodChain c, int a, int b)
        {
            double dAvgM = 0.5 * (c.DiametersMm[a] + c.DiametersMm[b]) / 1000.0;
            double rhoAvg = 0.5 * (c.DensitiesGCm3[a] + c.DensitiesGCm3[b]) * 1000.0; // g/cm3 -> kg/m3
            double areaM2 = Math.PI * dAvgM * dAvgM / 4.0;
            return rhoAvg * areaM2; // kg/m
        }
    }

    /// <summary>Lays the line out at rest along <paramref name="castingDirectionX"/> from the given
    /// tip position, per the paper's initial condition ("laid out horizontally at rest at the end of
    /// a perfect back cast"). Node 0 sits at the tip; node N is the free end, one line-length behind.</summary>
    public void InitializeLaidOutBehindCaster(Vec2 tipPosition, Vec2 castingDirectionX)
    {
        var dir = castingDirectionX.Normalized();
        double s = 0;
        for (int i = 0; i < NodeCount; i++)
        {
            Positions[i] = tipPosition - dir * s;
            Velocities[i] = Vec2.Zero;
            if (i < RestLengthsM.Length) s += RestLengthsM[i];
        }
    }

    /// <summary>One sub-step: node 0 is a Dirichlet boundary condition driven by the rod tip; every
    /// other node integrates drag + gravity, then the chain is projected onto the combined
    /// inextensibility + bending constraints (both XPBD, both unconditionally stable).</summary>
    public void Step(double dt, Vec2 tipPosition, Vec2 tipVelocity)
    {
        int n = NodeCount;
        var forces = new Vec2[n];
        AccumulateDragForces(forces);
        AccumulateGravity(forces);

        var predicted = new Vec2[n];
        predicted[0] = tipPosition;
        for (int i = 1; i < n; i++)
        {
            double invM = 1.0 / EffectiveMass(i);
            var accel = forces[i] * invM;
            predicted[i] = Positions[i] + Velocities[i] * dt + accel * (dt * dt);
        }

        for (int i = 0; i < RestLengthsM.Length; i++)
            EdgeStretchM[i] = (predicted[i + 1] - predicted[i]).Length - RestLengthsM[i];

        ProjectConstraints(predicted, dt);

        for (int i = 1; i < n; i++)
            Velocities[i] = (predicted[i] - Positions[i]) / dt;
        Velocities[0] = tipVelocity;

        ApplyBendingDamping(dt);

        for (int i = 1; i < n; i++) Positions[i] = predicted[i];
        Positions[0] = tipPosition;
    }

    private double EffectiveMass(int i) => i == NodeCount - 1 ? NodeMassesKg[i] + FlyMassKg : NodeMassesKg[i];

    /// <summary>Both constraints solved together, Gauss-Seidel style, in the same iterative loop —
    /// standard XPBD practice for coupled constraints. λ accumulators reset once per call (i.e. once
    /// per physics step) and accumulate across the iterations within it, per the XPBD algorithm.</summary>
    private void ProjectConstraints(Vec2[] p, double dt)
    {
        Array.Clear(_bendingLambda);
        double dt2 = dt * dt;

        for (int iter = 0; iter < ConstraintIterations; iter++)
        {
            // Alternating sweep direction (forward/backward) each iteration: plain one-directional
            // Gauss-Seidel on a long chain only propagates a correction ~1 node per iteration in the
            // sweep direction, so a local perturbation near one end takes many iterations to be felt
            // at the other; alternating lets information travel both ways every pass, converging
            // markedly faster for the same iteration budget — standard practice for chain-like
            // constraint systems (this is what finally cleared a persistent, localised >150°
            // convergence artifact that a plain forward sweep left behind even at 40 iterations).
            bool forward = (iter & 1) == 0;

            // Inextensibility (rigid: zero compliance).
            int distCount = RestLengthsM.Length;
            for (int k = 0; k < distCount; k++)
            {
                int i = forward ? k : distCount - 1 - k;
                var delta = p[i + 1] - p[i];
                double len = delta.Length;
                if (len < 1e-9) continue;
                double diff = (len - RestLengthsM[i]) / len;
                double invMa = i == 0 ? 0.0 : 1.0 / EffectiveMass(i);
                double invMb = 1.0 / EffectiveMass(i + 1);
                double wSum = invMa + invMb;
                if (wSum < 1e-12) continue;
                var correction = delta * diff;
                p[i] += correction * (invMa / wSum);
                p[i + 1] -= correction * (invMb / wSum);
            }

            // Bending, compliant: C = θ (the signed turning angle at node i), target 0 (straight).
            // Angle-based, not the earlier Cartesian "move toward the midpoint" — that formula is
            // only the small-angle limit of this one, and a real loop's tightest bend reaches
            // 30-90° locally, well outside where that limit holds. There, the linear version's
            // restoring direction stops tracking the true "reduce curvature" direction and let
            // spurious extra loops persist in the shape instead of resolving. θ = atan2(cross,dot)
            // is exact for any angle up to ±180°, and its gradient below is the exact analytic
            // gradient of that exact θ, not a linearization of it.
            int bendCount = NodeCount - 2;
            for (int k = 0; k < bendCount; k++)
            {
                int i = forward ? 1 + k : NodeCount - 2 - k;
                var e1 = p[i] - p[i - 1];
                var e2 = p[i + 1] - p[i];
                double len1Sq = e1.LengthSquared, len2Sq = e2.LengthSquared;
                if (len1Sq < 1e-12 || len2Sq < 1e-12) continue;

                double lBar = 0.5 * (RestLengthsM[i - 1] + RestLengthsM[i]);
                if (lBar < 1e-9) continue;
                // Angular stiffness EI/l̄ (bending energy (1/2)(EI/l̄)θ², matching the paper's
                // M=EI·κ with κ≈θ/l̄) — dimensionally distinct from the old EI/l̄³ linear stiffness.
                double kAngle = BendStiffnessNm2[i] / lBar;
                if (kAngle < 1e-12) continue;
                double alphaTilde = 1.0 / (kAngle * dt2);

                double theta = Math.Atan2(e1.X * e2.Y - e1.Y * e2.X, e1.Dot(e2));

                var g0 = Perp(e1) / len1Sq;
                var g2 = Perp(e2) / len2Sq;
                var g1 = -g0 - g2;

                double wPrev = i - 1 == 0 ? 0.0 : 1.0 / EffectiveMass(i - 1);
                double wSelf = 1.0 / EffectiveMass(i);
                double wNext = 1.0 / EffectiveMass(i + 1);
                double denom = wPrev * g0.LengthSquared + wSelf * g1.LengthSquared + wNext * g2.LengthSquared + alphaTilde;
                if (denom < 1e-12) continue;

                int idx = i - 1;
                double dLambda = -(theta + alphaTilde * _bendingLambda[idx]) / denom;
                _bendingLambda[idx] += dLambda;

                p[i - 1] += g0 * (wPrev * dLambda);
                p[i] += g1 * (wSelf * dLambda);
                p[i + 1] += g2 * (wNext * dLambda);
            }
        }
    }

    private static Vec2 Perp(Vec2 v) => new(-v.Y, v.X);

    /// <summary>Implicit (unconditionally stable) velocity relaxation toward the local neighbour
    /// average — the added material damping described in the class remarks. Uses a copy so the pass
    /// does not depend on node processing order.</summary>
    private void ApplyBendingDamping(double dt)
    {
        if (NodeCount < 3) return;
        double rate = 1.0 - Math.Exp(-dt / BendingDampingTimeConstantS);
        var damped = (Vec2[])Velocities.Clone();
        for (int i = 1; i < NodeCount - 1; i++)
        {
            var neighborAvg = (Velocities[i - 1] + Velocities[i + 1]) * 0.5;
            damped[i] = Velocities[i] + (neighborAvg - Velocities[i]) * rate;
        }
        Array.Copy(damped, Velocities, Velocities.Length);
    }

    private void AccumulateDragForces(Vec2[] forces)
    {
        double rhoAir = _settings.AirDensityKgM3, cd1 = _settings.NormalDragCoeff, cd3 = _settings.TangentialDragCoeff;
        for (int i = 0; i < NodeCount; i++)
        {
            var tangent = LocalTangent(i);
            var vRel = Velocities[i];
            var vT = tangent * vRel.Dot(tangent);
            var vN = vRel - vT;
            double dM = DiametersMm[i] / 1000.0;
            double voronoi = VoronoiLengthM(i);

            var fNormal = vN * (-0.5 * rhoAir * dM * Math.PI * cd1 * vN.Length) * voronoi;
            var fTangential = vT * (-0.5 * rhoAir * dM * cd3 * vT.Length) * voronoi;
            forces[i] += fNormal + fTangential;

            if (i == NodeCount - 1 && FlyMassKg > 0)
            {
                // Small "disc" drag for the attached fly, added on top of the line-end contribution.
                double flyD = FlyDiameterMm / 1000.0;
                double area = Math.PI * flyD * flyD / 4.0;
                forces[i] += -0.5 * rhoAir * FlyDragCoeff * area * vRel.Length * vRel;
            }
        }
    }

    private void AccumulateGravity(Vec2[] forces)
    {
        double g = _settings.GravityMs2;
        for (int i = 0; i < NodeCount; i++)
            forces[i] += new Vec2(0, -EffectiveMass(i) * g);
    }

    private Vec2 LocalTangent(int i)
    {
        if (i == 0) return (Positions[1] - Positions[0]).Normalized();
        if (i == NodeCount - 1) return (Positions[i] - Positions[i - 1]).Normalized();
        return (Positions[i + 1] - Positions[i - 1]).Normalized();
    }

    private double VoronoiLengthM(int i)
    {
        double len = 0;
        if (i > 0) len += 0.5 * RestLengthsM[i - 1];
        if (i < NodeCount - 1) len += 0.5 * RestLengthsM[i];
        return len;
    }

}
