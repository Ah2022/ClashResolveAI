// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System;

namespace BIMEngine.Core.Spatial
{
    /// <summary>
    /// Plain axis-aligned bounding box in internal Revit feet units.
    /// Deliberately Revit-API-free so the octree stays unit-testable
    /// outside of Revit and reusable by any future BIM Engine module
    /// (clearance, code compliance, QA/QC, ...).
    /// </summary>
    public struct AABB
    {
        public double MinX, MinY, MinZ;
        public double MaxX, MaxY, MaxZ;

        public AABB(double minX, double minY, double minZ,
                    double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public double CenterX => (MinX + MaxX) * 0.5;
        public double CenterY => (MinY + MaxY) * 0.5;
        public double CenterZ => (MinZ + MaxZ) * 0.5;

        /// <summary>Grows the box in every direction by the given amount (feet).
        /// Used to widen a query region by the largest configured clearance
        /// before hitting the octree, so real near-misses aren't missed.</summary>
        public AABB Expand(double amount)
        {
            return new AABB(
                MinX - amount, MinY - amount, MinZ - amount,
                MaxX + amount, MaxY + amount, MaxZ + amount);
        }

        public bool Intersects(AABB other)
        {
            return MinX <= other.MaxX && MaxX >= other.MinX
                && MinY <= other.MaxY && MaxY >= other.MinY
                && MinZ <= other.MaxZ && MaxZ >= other.MinZ;
        }

        public bool Contains(AABB other)
        {
            return other.MinX >= MinX && other.MaxX <= MaxX
                && other.MinY >= MinY && other.MaxY <= MaxY
                && other.MinZ >= MinZ && other.MaxZ <= MaxZ;
        }

        /// <summary>Union of this box with another — used while building octree node bounds.</summary>
        public AABB Union(AABB other)
        {
            return new AABB(
                Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Min(MinZ, other.MinZ),
                Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));
        }

        public override string ToString() =>
            $"[({MinX:F2},{MinY:F2},{MinZ:F2}) -> ({MaxX:F2},{MaxY:F2},{MaxZ:F2})]";
    }
}
