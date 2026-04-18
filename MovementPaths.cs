#nullable disable
#pragma warning disable
using System;

namespace emu2026
{
    public static class MovementPaths
    {
        public enum PathType { Adaptive = 0, Bezier = 1, Linear = 2, Exponential = 3, None = 4 }

        public static Point Curve(Point start, Point end, double t, PathType type)
        {
            switch (type)
            {
                case PathType.Linear: return Lerp(start, end, t);
                case PathType.Bezier: return CubicBezier(start, end, t);
                case PathType.Exponential: return Exponential(start, end, t, 2.0);
                case PathType.Adaptive: return Adaptive(start, end, t);
                default: return end;
            }
        }

        private static Point Lerp(Point a, Point b, double t)
        {
            return new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        private static Point CubicBezier(Point start, Point end, double t)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            Point c1 = new Point(start.X + dx / 3.0, start.Y + dy / 3.0);
            Point c2 = new Point(start.X + 2.0 * dx / 3.0, start.Y + 2.0 * dy / 3.0);
            double mt = 1.0 - t;
            double x = mt * mt * mt * start.X + 3 * mt * mt * t * c1.X + 3 * mt * t * t * c2.X + t * t * t * end.X;
            double y = mt * mt * mt * start.Y + 3 * mt * mt * t * c1.Y + 3 * mt * t * t * c2.Y + t * t * t * end.Y;
            return new Point(x, y);
        }

        private static Point Exponential(Point start, Point end, double t, double exponent)
        {
            return new Point(
                start.X + (end.X - start.X) * Math.Pow(t, exponent),
                start.Y + (end.Y - start.Y) * Math.Pow(t, exponent));
        }

        private static Point Adaptive(Point start, Point end, double t)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 100) return Lerp(start, end, t);
            return CubicBezier(start, end, t);
        }
    }

    public struct Point
    {
        public double X, Y;
        public Point(double x, double y) { X = x; Y = y; }
    }
}
