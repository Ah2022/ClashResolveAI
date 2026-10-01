// Shared level and grid-reference helpers for clash locations.
using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace ClashResolveAI.Core
{
    public static class LocationUtils
    {
        /// <summary>Nearest level at or below the given point (host-document elevation).</summary>
        public static string GetLevelName(Document doc, XYZ pt)
        {
            try
            {
                if (doc == null || pt == null) return "";
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();

                Level? best = null;
                foreach (var lv in levels)
                    if (lv.Elevation <= pt.Z + 1.0) best = lv; else break;
                return best?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Nearest vertical/horizontal grid intersection label, e.g. "C/4".</summary>
        public static string GetGridRef(Document doc, XYZ pt)
        {
            try
            {
                if (doc == null || pt == null) return "";
                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid)).Cast<Grid>().ToList();
                if (grids.Count == 0) return "";

                string? h = null, v = null;
                double dh = double.MaxValue, dv = double.MaxValue;

                foreach (var g in grids)
                {
                    if (!(g.Curve is Line ln)) continue;
                    var p0 = ln.GetEndPoint(0);
                    var p1 = ln.GetEndPoint(1);
                    var dir = (p1 - p0).Normalize();

                    XYZ ptXY  = new XYZ(pt.X, pt.Y, 0);
                    XYZ p0XY  = new XYZ(p0.X, p0.Y, 0);
                    XYZ p1XY  = new XYZ(p1.X, p1.Y, 0);
                    XYZ dirXY = p1XY - p0XY;
                    double lineLen = dirXY.GetLength();

                    double dist;
                    if (lineLen < 1e-9)
                    {
                        dist = ptXY.DistanceTo(p0XY);
                    }
                    else
                    {
                        XYZ normDir = dirXY.Normalize();
                        double t = (ptXY - p0XY).DotProduct(normDir);
                        XYZ closest = p0XY + normDir.Multiply(t);
                        dist = ptXY.DistanceTo(closest);
                    }

                    bool isV = Math.Abs(dir.X) < Math.Abs(dir.Y); // more N-S than E-W
                    if (isV  && dist < dv) { dv = dist; v = g.Name; }
                    if (!isV && dist < dh) { dh = dist; h = g.Name; }
                }

                if (v == null && h == null) return "";
                return $"{v ?? "?"}/{h ?? "?"}";
            }
            catch { return ""; }
        }
    }
}

