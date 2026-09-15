using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Physics;

/// <summary>Orchestrates one casting simulation run: samples a project's geometry, builds the DER
/// chain, couples it to the rod, integrates, and packages the result for the UI.</summary>
public static class CastSimulator
{
    public static CastSimulationResult Run(FlyLineProject project, CastingScenario scenario, SimulationSettings settings)
    {
        var sampler = new LineArcSampler(project);
        var chain = ElasticRodChain.Build(sampler, settings);
        // Every real cast has a leader and fly at the end — a completely bare line tip is not a
        // simplification any scenario should make. Without it, the free end's own mass is only a
        // few hundredths of a gram (whatever sliver of taper the last node samples), so drag and
        // bending push it around with almost no inertia to resist — visibly erratic, "whip-cracking"
        // motion that has nothing to do with the taper being simulated. Attaching the leader/fly
        // mass here, always, is what makes the free end behave like an actual cast rather than a
        // loose thread.
        chain.FlyMassKg = settings.FlyMassKg;
        chain.FlyDiameterMm = settings.FlyDiameterMm;
        chain.FlyDragCoeff = settings.FlyDragCoeff;

        var rod = new RodTipModel(scenario, settings);
        var tip0 = rod.ShadowTipPosition(0);
        chain.InitializeLaidOutBehindCaster(tip0, new Vec2(1, 0));

        int maxSteps = Math.Max(1, (int)(settings.DurationS / settings.TimeStepS));
        var times = new List<double>(maxSteps + 1);
        var speeds = new List<double>(maxSteps + 1);
        var heights = new List<double>(maxSteps + 1);

        int recordEvery = Math.Max(1, maxSteps / Math.Max(1, settings.MaxRecordedFrames));
        var frameTimes = new List<double>();
        var framePositions = new List<Vec2[]>();

        int freeEnd = chain.NodeCount - 1;
        for (int step = 0; step <= maxSteps; step++)
        {
            double t = step * settings.TimeStepS;
            var (tipPos, tipVel) = rod.Advance(t, settings.TimeStepS);
            if (step > 0) chain.Step(settings.TimeStepS, tipPos, tipVel);
            else chain.Positions[0] = tipPos; // record t=0 without stepping dynamics

            times.Add(t);
            speeds.Add(chain.Velocities[freeEnd].Length);
            heights.Add(chain.Positions[freeEnd].Y);

            if (step % recordEvery == 0)
            {
                frameTimes.Add(t);
                framePositions.Add((Vec2[])chain.Positions.Clone());
            }

            // A real cast ends the instant the fly reaches the water — everything after that is
            // unmodelled (no water contact/drag physics here) and, run for long enough, just shows
            // the anchored-at-the-rod-tip chain swinging like a pendulum with nothing to land on.
            // Stopping here keeps the recorded run to the part that is actually a cast.
            if (chain.Positions[freeEnd].Y <= 0) break;
        }

        double[] timesArr = times.ToArray();
        double[] speedsArr = speeds.ToArray();
        double[] heightsArr = heights.ToArray();

        int releaseIdx = FindReleaseIndex(speedsArr);
        var ballistic = BuildBallisticReference(timesArr, heightsArr, releaseIdx, settings.GravityMs2);

        double distance = framePositions.Count > 0
            ? Math.Abs(framePositions[^1][^1].X - framePositions[0][^1].X)
            : 0;

        return new CastSimulationResult
        {
            ScenarioNameIt = scenario.NameIt,
            TimesS = timesArr,
            FreeEndSpeedMs = speedsArr,
            FreeEndHeightM = heightsArr,
            FreeEndHeightBallisticM = ballistic,
            FrameTimesS = frameTimes.ToArray(),
            FramePositions = framePositions.ToArray(),
            NodeArcLengthsCm = chain.ArcLengthsCm,
            ReleaseTimeS = timesArr[releaseIdx],
            DistanceTraveledM = distance,
        };
    }

    /// <summary>"Release" is approximated as the instant of maximum free-end speed — a proxy for
    /// loop turnover, the point after which the line/fly is essentially in free flight and a
    /// no-drag ballistic comparison becomes physically meaningful (Fase 3's "perdita di quota per
    /// drag" chart).</summary>
    private static int FindReleaseIndex(double[] speeds)
    {
        int best = 0;
        for (int i = 1; i < speeds.Length; i++)
            if (speeds[i] > speeds[best]) best = i;
        return best;
    }

    private static double[] BuildBallisticReference(double[] times, double[] heights, int releaseIdx, double g)
    {
        var result = new double[times.Length];
        for (int i = 0; i < result.Length; i++) result[i] = double.NaN;
        if (releaseIdx >= times.Length) return result;

        // We only have height (Y) recorded at full resolution; reconstruct the release vertical
        // velocity from the two samples straddling release for the ballistic reference curve.
        double dt = times.Length > 1 ? times[1] - times[0] : 0;
        double vy0 = releaseIdx > 0 && dt > 0 ? (heights[releaseIdx] - heights[releaseIdx - 1]) / dt : 0;

        double y0 = heights[releaseIdx];
        double t0 = times[releaseIdx];
        for (int i = releaseIdx; i < times.Length; i++)
        {
            double dtR = times[i] - t0;
            result[i] = y0 + vy0 * dtR - 0.5 * g * dtR * dtR;
        }
        return result;
    }
}
