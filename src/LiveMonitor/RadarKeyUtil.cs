// LiveMonitor/RadarKeyUtil.cs — v5.1
//
// Pure helpers extracted from RadarDataStore / ClashRefreshHandler so they
// can be unit-tested without any Revit API references.

using System;

namespace ClashResolveAI.LiveMonitor
{
    /// <summary>
    /// Order-independent pair key for de-duplicating clashes by the two
    /// element ids involved. "A clashes with B" must equal "B clashes with A".
    /// </summary>
    public static class RadarKeyUtil
    {
        public static string Pair(long idA, long idB)
        {
            long a = Math.Min(idA, idB);
            long b = Math.Max(idA, idB);
            return a + ":" + b;
        }
    }

    /// <summary>
    /// Axis-aligned bounding box intersection test, used by the Refresh
    /// handler to prune resolved clashes. Public + pure → unit-testable.
    /// </summary>
    public readonly struct Aabb
    {
        public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public Aabb(double minX, double minY, double minZ,
                    double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }
    }

    public static class AabbIntersector
    {
        public static bool Intersects(Aabb a, Aabb b) =>
            a.MinX <= b.MaxX && a.MaxX >= b.MinX &&
            a.MinY <= b.MaxY && a.MaxY >= b.MinY &&
            a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
    }
}
