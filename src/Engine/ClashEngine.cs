// Shared Revit 2024 clash engine. Geometry and orchestration are in UnifiedScan.cs.
using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
namespace ClashResolveAI.ClashEngine
{
    public partial class ClashEngine
    {
        private readonly Document _doc;
        private RulesEngine _rules;
        private readonly string _ruleSetName;
        private readonly LinkedModelManager _links;
        public ClashEngine(Document doc, string ruleSetName = "DefaultRules")
        { _doc = doc; _ruleSetName=ruleSetName; _rules = ScanSessionCache.Rules(doc,ruleSetName); _links = ScanSessionCache.Links(doc); }
        public List<ClashResult> RunFullScan(bool includeLinks = true, IProgress<string>? progress = null, CoordinationZone? zone = null, string levelId = "", ScanMode? mode = null)
            => ScanUnified(null, includeLinks, progress, zone, levelId, mode);
        public List<ClashResult> RunTargetedScan(IEnumerable<ElementId> ids,ScanMode? mode=null) => ScanUnified(ids, false,mode:mode);
        public List<ClashResult> RunTargetedScanWithLinks(IEnumerable<ElementId> ids) => ScanUnified(ids, true);
        public List<ClashResult> RunSelectionScan(Element element) => ScanUnified(new[] { element.Id }, false);
        public List<ClashResult> RunSelectionScanWithLinks(Element element) => ScanUnified(new[] { element.Id }, true);
        private ClashResult? BuildResult(
            int id,
            Element elA, Discipline dA, Element elB, Discipline dB,
            string ruleKey, ClashSeverity sev, double gapMm,
            BoundingBoxXYZ bbA, BoundingBoxXYZ bbB,
            string linkFileA, string linkFileB, double overlapVolMM3)
        {
            var finalSev = sev; var ruleApplied = ruleKey;
            if (finalSev == ClashSeverity.Ignore) return null;

            var    pt     = GetClashPoint(bbA, bbB);
            double toM(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
            string linkInfo = string.IsNullOrEmpty(linkFileA)
                ? "" : $" [Link:{linkFileA}]";
            string volInfo = overlapVolMM3 > 0 ? $" | Vol:{overlapVolMM3:F1}mm³" : "";

            Discipline mover = PriorityMatrix.GetMovingDiscipline(dA, dB);

            return new ClashResult
            {
                ElementA          = elA,
                ElementB          = elB,
                DisciplineA       = dA,
                DisciplineB       = dB,
                SystemTypeA       = GetSystemType(elA),
                SystemTypeB       = GetSystemType(elB),
                // FIX E: populate category/family fields for dashboard filtering
                CategoryNameA     = ElementCollector.GetCategoryName(elA),
                CategoryNameB     = ElementCollector.GetCategoryName(elB),
                FamilyTypeA       = ElementCollector.GetFamilyTypeName(elA),
                FamilyTypeB       = ElementCollector.GetFamilyTypeName(elB),
                StructuralSubTypeA = dA == Discipline.Structural ? ElementCollector.GetStructuralSubType(elA) : "",
                StructuralSubTypeB = dB == Discipline.Structural ? ElementCollector.GetStructuralSubType(elB) : "",
                ClashType         = ruleKey,
                TestType          = overlapVolMM3 > 0 ? ClashTestType.HardClash : ClashTestType.ClearanceClash,
                Severity          = finalSev,
                GapMM             = Math.Round(gapMm, 1),
                OverlapVolumeMM3  = Math.Round(overlapVolMM3, 1),
                ClashPoint        = pt,
                LocationText      = $"X:{toM(pt.X):F2}m  Y:{toM(pt.Y):F2}m  Z:{toM(pt.Z):F2}m{linkInfo}{volInfo}",
                LevelName         = GetLevel(pt),
                GridRef           = GetGrid(pt),
                RuleApplied       = ruleApplied,
                Priority          = AssignPriority(finalSev, dA, dB),
                MovingDiscipline  = mover.ToString(),
                LinkFileA         = linkFileA,
                LinkFileB         = linkFileB,
                Status            = ClashStatus.New
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  LEVEL FILTER  — restricts elements to a specific level
        // ════════════════════════════════════════════════════════════════

        private List<Element> FilterByLevel(List<Element> elements, string levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return elements;
            try
            {
                var level = _doc.GetElement(new ElementId(long.Parse(levelId))) as Level;
                if (level == null) return elements;

                // Get the next level elevation for the upper bound
                double levelElev = level.Elevation;
                double nextElev  = GetNextLevelElevation(levelElev);
                double bandFt    = 3.28; // 1m tolerance above and below

                return elements.Where(el =>
                {
                    try
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) return false;
                        double midZ = (bb.Min.Z + bb.Max.Z) / 2.0;
                        return midZ >= (levelElev - bandFt) && midZ < (nextElev + bandFt);
                    }
                    catch { return false; }
                }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] FilterByLevel: {ex.Message}");
                return elements;
            }
        }

        private double GetNextLevelElevation(double currentElev)
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var next = levels.FirstOrDefault(l => l.Elevation > currentElev + 0.1);
            return next?.Elevation ?? (currentElev + 16.4); // default 5m if no next level
        }

        private List<Element> ApplyZoneFilter(List<Element> elements, CoordinationZone zone)
        {
            if (zone == null) return elements;
            var result = new List<Element>(elements.Count);
            foreach (var el in elements)
            {
                try
                {
                    if (!string.IsNullOrEmpty(zone.LevelName))
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        if (!GetLevel(GetClashPoint(bb, bb)).Equals(
                            zone.LevelName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    result.Add(el);
                }
                catch { result.Add(el); }
            }
            return result;
        }

        // ════════════════════════════════════════════════════════════════
        //  GEOMETRY HELPERS
        // ════════════════════════════════════════════════════════════════

        private static bool AABBOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
            a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
            a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        private static BoundingBoxXYZ Expand(BoundingBoxXYZ bb, double ft)
        {
            var e = new BoundingBoxXYZ();
            e.Min = new XYZ(bb.Min.X - ft, bb.Min.Y - ft, bb.Min.Z - ft);
            e.Max = new XYZ(bb.Max.X + ft, bb.Max.Y + ft, bb.Max.Z + ft);
            return e;
        }

        private static double ComputeGapMm(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double gx = Math.Max(a.Min.X, b.Min.X) - Math.Min(a.Max.X, b.Max.X);
            double gy = Math.Max(a.Min.Y, b.Min.Y) - Math.Min(a.Max.Y, b.Max.Y);
            double gz = Math.Max(a.Min.Z, b.Min.Z) - Math.Min(a.Max.Z, b.Max.Z);
            return Math.Max(gx, Math.Max(gy, gz)) * 304.8;
        }

        private static XYZ GetClashPoint(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            new XYZ(
                (Math.Max(a.Min.X, b.Min.X) + Math.Min(a.Max.X, b.Max.X)) / 2,
                (Math.Max(a.Min.Y, b.Min.Y) + Math.Min(a.Max.Y, b.Max.Y)) / 2,
                (Math.Max(a.Min.Z, b.Min.Z) + Math.Min(a.Max.Z, b.Max.Z)) / 2);

        private List<Level>? _levelCache;
        private List<Level> GetSortedLevels()
        {
            if (_levelCache != null) return _levelCache;
            _levelCache = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            return _levelCache;
        }

        private string GetLevel(XYZ pt)
        {
            Level? best = null;
            foreach (var lv in GetSortedLevels())
                if (lv.Elevation <= pt.Z + 1.0) best = lv; else break;
            return best?.Name ?? "Unknown Level";
        }

        private List<Grid>? _grids;
        private string GetGrid(XYZ pt)
        {
            var grids = _grids ??= new FilteredElementCollector(_doc)
                .OfClass(typeof(Grid)).Cast<Grid>().ToList();
            if (!grids.Any()) return "N/A";

            string? h = null, v = null;
            double dh = double.MaxValue, dv = double.MaxValue;

            foreach (var g in grids)
            {
                if (!(g.Curve is Line ln)) continue;

                var p0  = ln.GetEndPoint(0);
                var p1  = ln.GetEndPoint(1);
                var dir = (p1 - p0).Normalize();

                // FIX v8.0: was "double dist = 0.0 /*stub*/;" — always zero,
                // so EVERY clash showed the first grid line found.
                // Correct: project the clash point onto the grid line and
                // measure perpendicular distance. Use XY plane only so
                // elevation differences don't affect the nearest-grid result.
                XYZ ptXY  = new XYZ(pt.X,   pt.Y,   0);
                XYZ p0XY  = new XYZ(p0.X,   p0.Y,   0);
                XYZ p1XY  = new XYZ(p1.X,   p1.Y,   0);
                XYZ dirXY = (p1XY - p0XY);
                double lineLen = dirXY.GetLength();

                double dist;
                if (lineLen < 1e-9)
                {
                    dist = ptXY.DistanceTo(p0XY);
                }
                else
                {
                    // Scalar projection of (pt - p0) onto the line direction
                    XYZ normDir = dirXY.Normalize();
                    double t    = (ptXY - p0XY).DotProduct(normDir);
                    // Clamp to segment; unclamped gives nearest line (not just segment)
                    XYZ closest = p0XY + normDir.Multiply(t);
                    dist = ptXY.DistanceTo(closest);
                }

                // Classify as vertical (N-S running) or horizontal (E-W running)
                // based on the dominant direction component in XY.
                bool isV = Math.Abs(dir.X) < Math.Abs(dir.Y); // more N-S than E-W

                if (isV  && dist < dv) { dv = dist; v = g.Name; }
                if (!isV && dist < dh) { dh = dist; h = g.Name; }
            }

            return $"{v ?? "?"}/{h ?? "?"}";
        }

        private static string AssignPriority(ClashSeverity s, Discipline a, Discipline b)
        {
            if (s == ClashSeverity.Critical) return "Critical";
            if (s == ClashSeverity.Hard)
                return (a == Discipline.Structural || b == Discipline.Structural ||
                        a == Discipline.GravityDrainage || b == Discipline.GravityDrainage)
                    ? "Critical" : "High";
            return s == ClashSeverity.Soft ? "Medium" : "Low";
        }

        // ════════════════════════════════════════════════════════════════
        //  GET SYSTEM TYPE
        //
        //  FIX v9.0: previously read raw casted integer literals
        //  ((BuiltInParameter)(-1176500), -1176800, -1001100) that do not
        //  correspond to documented BuiltInParameter members. This silently
        //  returned blank/wrong system types for a large share of elements
        //  — the root cause of "Full Scan doesn't get the accurate system
        //  type". Delegated to SystemClassificationService, which reads
        //  only named, documented parameters (RBS_PIPING_SYSTEM_TYPE_PARAM,
        //  RBS_SYSTEM_CLASSIFICATION_PARAM, RBS_DUCT_SYSTEM_TYPE_PARAM,
        //  RBS_CTC_SERVICE_TYPE) with safe fallbacks. See that file for the
        //  full root-cause writeup.
        // ════════════════════════════════════════════════════════════════

        private static string GetSystemType(Element el) =>
            SystemClassificationService.GetSystemTypeName(el);

        private static string GetRuleKey(Discipline a, Discipline b)
        {
            var entry = ElementCollector.ClashMatrix.FirstOrDefault(m => 
                (m.Source == a && m.Target == b) || (m.Source == b && m.Target == a));
            return entry?.RuleKey ?? "Generic";
        }
    }
}
