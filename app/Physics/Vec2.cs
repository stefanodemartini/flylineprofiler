namespace DiametroLineaDesktop.Physics;

/// <summary>
/// Double-precision 2D vector for the casting simulator: X = downrange (horizontal), Y = height.
/// The model is deliberately 2D — the same vertical plane Gatti-Bono &amp; Perkins' own model uses —
/// so there is no third (out-of-plane/lateral) axis to carry. Double precision because a stiff
/// inextensibility constraint solved over thousands of sub-steps needs it (float error would show
/// up as visible line stretch within a few seconds of simulated cast).
/// </summary>
public readonly struct Vec2
{
    public readonly double X, Y;
    public Vec2(double x, double y) { X = x; Y = y; }
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => a * s;
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public double Dot(Vec2 b) => X * b.X + Y * b.Y;
    public double LengthSquared => X * X + Y * Y;
    public double Length => Math.Sqrt(LengthSquared);

    public Vec2 Normalized()
    {
        double len = Length;
        return len > 1e-12 ? this / len : Zero;
    }
}
