public readonly record struct Point(int X, int Y);
public static class PointOps { public static int Mag(Point p) => Math.Abs(p.X) + Math.Abs(p.Y); }
