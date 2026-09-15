namespace DiametroLineaDesktop.Physics;

/// <summary>Output of one <see cref="CastSimulator"/> run: everything the Simulator UI's 2D loop
/// viewer and the three comparison charts (loop shape, free-end speed, height loss to drag) need.</summary>
public sealed class CastSimulationResult
{
    public required string ScenarioNameIt { get; init; }

    /// <summary>Full-resolution scalar time series (one entry per simulation step).</summary>
    public required double[] TimesS { get; init; }
    public required double[] FreeEndSpeedMs { get; init; }
    public required double[] FreeEndHeightM { get; init; }
    /// <summary>No-drag ballistic reference height from the release point (peak free-end speed) —
    /// only meaningful, and only populated, from that point on; NaN before it.</summary>
    public required double[] FreeEndHeightBallisticM { get; init; }

    /// <summary>Sparser snapshots of the whole line's shape, for animation/scrubbing — capped at
    /// <see cref="SimulationSettings.MaxRecordedFrames"/>.</summary>
    public required double[] FrameTimesS { get; init; }
    public required Vec2[][] FramePositions { get; init; }

    /// <summary>Arc length (cm from the butt) of each node in <see cref="FramePositions"/> — for
    /// resolving each node's nozzle-zone color in the loop viewer.</summary>
    public required double[] NodeArcLengthsCm { get; init; }

    public double ReleaseTimeS { get; init; }
    public double DistanceTraveledM { get; init; }
}
