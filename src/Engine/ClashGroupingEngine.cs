// Engine/ClashGroupingEngine.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #4: Clash Grouping Engine
//
// Replicates Navisworks coordination grouping logic:
//   Instead of showing 17 raw clashes, shows 1 grouped issue with 17 members.
//
// Grouping strategies (applied in order):
//   1. Same discipline pair + same level + same route path   → "Same Route"
//   2. Same discipline pair + same zone/grid                 → "Same Zone"
//   3. Same primary offender element (root cause detection)  → "Same Source"
//   4. Same discipline pair + same level                     → "Same Level"
//
// IMPROVEMENT #19: Root Cause Detection
//   Identifies the PRIMARY OFFENDER — one large duct causing 34 clashes.
//   This is far more valuable than raw clash counts.

using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Engine
{
    public class ClashGroupingEngine
    {
        // ════════════════════════════════════════════════════════════════
        //  GROUP CLASHES  — main entry point
        // ════════════════════════════════════════════════════════════════

        public List<ClashGroup> GroupClashes(List<ClashResult> clashes)
        {
            if (clashes == null || clashes.Count == 0)
                return new List<ClashGroup>();

            if(clashes.Select(c=>c.TestType).Distinct().Count()>1)
                return clashes.GroupBy(c=>c.TestType).SelectMany(g=>GroupClashes(g.ToList())).ToList();
            var ungrouped  = new List<ClashResult>(clashes);
            var groups     = new List<ClashGroup>();
            var consumed = new HashSet<ClashResult>();

            // Strategy 1: Route-based grouping (same system route in same corridor)
            var routeGroups = GroupByRoute(ungrouped);
            foreach (var g in routeGroups)
            {
                if (g.Count >= 2)
                {
                    var group = BuildGroup(g, "Same Route Path");
                    groups.Add(group);
                    consumed.UnionWith(g);
                }
            }

            ungrouped.RemoveAll(consumed.Contains); consumed.Clear();
            // Strategy 2: Zone-based grouping (same grid + level corridor)
            var zoneGroups = GroupByZone(ungrouped);
            foreach (var g in zoneGroups)
            {
                if (g.Count >= 3)
                {
                    var group = BuildGroup(g, "Same Coordination Zone");
                    groups.Add(group);
                    consumed.UnionWith(g);
                }
            }

            ungrouped.RemoveAll(consumed.Contains); consumed.Clear();
            // Strategy 3: Root cause — single element causing multiple clashes
            var rootGroups = GroupByRootCause(ungrouped);
            foreach (var g in rootGroups)
            {
                if (g.Count >= 3)
                {
                    var group = BuildGroup(g, "Same Primary Offender");
                    group.PrimaryOffender = IdentifyPrimaryOffender(g);
                    groups.Add(group);
                    consumed.UnionWith(g);
                }
            }

            ungrouped.RemoveAll(consumed.Contains); consumed.Clear();
            // Strategy 4: Level-based grouping (same discipline pair + same level)
            var levelGroups = GroupByLevel(ungrouped);
            foreach (var g in levelGroups)
            {
                if (g.Count >= 2)
                {
                    var group = BuildGroup(g, "Same Level — Same Discipline Pair");
                    groups.Add(group);
                    consumed.UnionWith(g);
                }
            }

            ungrouped.RemoveAll(consumed.Contains); consumed.Clear();
            // Remainder: individual groups (one per ungrouped clash)
            foreach (var c in ungrouped)
            {
                var solo = BuildGroup(new List<ClashResult> { c }, "Individual");
                groups.Add(solo);
            }

            // Sort by severity, then by group size (largest first)
            return groups
                .OrderBy(g => (int)g.MaxSeverity)
                .ThenByDescending(g => g.Count)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 1: ROUTE-BASED GROUPING
        //  Groups clashes where ElementA shares the same system type
        //  and their clash points are within a corridor (3m linear distance)
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByRoute(List<ClashResult> clashes)
        {
            var groups = new List<List<ClashResult>>();
            var visited = new HashSet<string>();
            const double cell=9.84;
            string Scope(ClashResult c)=>c.HostDocumentKey+"|"+c.DisciplineA+"|"+c.DisciplineB+"|"+c.LevelName+"|"+c.SystemTypeA;
            string Key(ClashResult c,int x,int y,int z)=>Scope(c)+"|"+x+"|"+y+"|"+z;
            int X(ClashResult c)=>(int)Math.Floor(c.ClashPoint.X/cell);
            int Y(ClashResult c)=>(int)Math.Floor(c.ClashPoint.Y/cell);
            int Z(ClashResult c)=>(int)Math.Floor(c.ClashPoint.Z/cell);
            var cells=clashes.Where(c=>!string.IsNullOrEmpty(c.SystemTypeA)).GroupBy(c=>Key(c,X(c),Y(c),Z(c))).ToDictionary(g=>g.Key,g=>g.ToList());
            foreach(var c in clashes){
                if(visited.Contains(c.ClashId)||string.IsNullOrEmpty(c.SystemTypeA))continue;
                var group=new List<ClashResult>();
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(cells.TryGetValue(Key(c,X(c)+x,Y(c)+y,Z(c)+z),out var nearby))
                        foreach(var n in nearby)if(!visited.Contains(n.ClashId)&&PointDistance(c.ClashPoint,n.ClashPoint)<cell){group.Add(n);visited.Add(n.ClashId);}
                if(group.Count>0)groups.Add(group);
            }
            return groups;
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 2: ZONE-BASED GROUPING
        //  Groups clashes within the same structural bay (same grid ref)
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByZone(List<ClashResult> clashes)
        {
            return clashes
                .Where(c => !string.IsNullOrEmpty(c.GridRef) && c.GridRef != "N/A")
                .GroupBy(c => $"{c.GridRef}|{c.LevelName}|{c.DisciplineA}|{c.DisciplineB}")
                .Select(g => g.ToList())
                .Where(g => g.Count >= 2)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 3: ROOT CAUSE GROUPING
        //  One element (e.g., a large duct) causing clashes with many others.
        //  Groups by the element ID that appears most frequently.
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByRootCause(List<ClashResult> clashes)
        {
            var groups = new List<List<ClashResult>>();
            var visited = new HashSet<string>();

            // Count element appearances (both A and B sides)
            var elementFreq = new Dictionary<string, int>();
            foreach (var c in clashes)
            {
                string idA = ElementKey(c, true);
                string idB = ElementKey(c, false);
                if (c.ElementAId > 0) elementFreq[idA] = (elementFreq.TryGetValue(idA, out int va) ? va : 0) + 1;
                if (c.ElementBId > 0) elementFreq[idB] = (elementFreq.TryGetValue(idB, out int vb) ? vb : 0) + 1;
            }

            // Find high-frequency offenders (appears in 3+ clashes)
            var offenders = elementFreq
                .Where(kv => kv.Value >= 3)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            var byElement=clashes.SelectMany(c=>new[]{(key:ElementKey(c,true),clash:c),(key:ElementKey(c,false),clash:c)}).GroupBy(x=>x.key).ToDictionary(g=>g.Key,g=>g.Select(x=>x.clash).Distinct().ToList());
            foreach (string offenderId in offenders)
            {
                var group = byElement[offenderId]
                    .Where(c => !visited.Contains(c.ClashId))
                    .ToList();

                if (group.Count >= 3)
                {
                    groups.Add(group);
                    foreach (var c in group) visited.Add(c.ClashId);
                }
            }

            return groups;
        }

        // ════════════════════════════════════════════════════════════════
        //  STRATEGY 4: LEVEL-BASED GROUPING
        //  Same discipline pair + same level — catch remaining clusters
        // ════════════════════════════════════════════════════════════════

        private List<List<ClashResult>> GroupByLevel(List<ClashResult> clashes)
        {
            return clashes
                .GroupBy(c => $"{c.DisciplineA}|{c.DisciplineB}|{c.LevelName}|{c.Severity}")
                .Select(g => g.ToList())
                .Where(g => g.Count >= 2)
                .ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD GROUP  — create a ClashGroup from a list of clashes
        // ════════════════════════════════════════════════════════════════

        private static ClashGroup BuildGroup(List<ClashResult> clashes, string reason)
        {
            var first = clashes[0];
            var worstSev = (ClashSeverity)clashes.Min(c => (int)c.Severity);

            string title = BuildGroupTitle(clashes, reason);

            var group = new ClashGroup
            {
                GroupId=StableGroupKey(reason,clashes),
                GroupTitle      = title,
                MaxSeverity     = worstSev,
                GroupingReason  = reason,
                LevelName       = first.LevelName,
                ZoneName        = first.ZoneName,
                GridRef         = first.GridRef,
                DisciplineA     = first.DisciplineA,
                DisciplineB     = first.DisciplineB,
                Clashes         = clashes
            };

            // Assign group ID to all members
            // Grouping is a projection. Filtering/exporting must not rewrite canonical issue membership.

            return group;
        }
        private static string StableGroupKey(string reason,List<ClashResult> clashes)
        {
            using var hash=System.Security.Cryptography.SHA256.Create();
            string key=reason+"|"+string.Join("|",clashes.Select(c=>c.NormalizedKey).OrderBy(k=>k,StringComparer.Ordinal));
            return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).Replace("-", "");
        }

        private static string BuildGroupTitle(List<ClashResult> clashes, string reason)
        {
            var first = clashes[0];
            string discA = first.DisciplineA.ToString();
            string discB = first.DisciplineB.ToString();
            string level = !string.IsNullOrEmpty(first.LevelName) ? $" @ {first.LevelName}" : "";
            string grid  = !string.IsNullOrEmpty(first.GridRef) && first.GridRef != "N/A"
                ? $" [{first.GridRef}]" : "";

            return $"{discA} vs {discB}{level}{grid} — {clashes.Count} clashes ({reason})";
        }

        // ════════════════════════════════════════════════════════════════
        //  ROOT CAUSE IDENTIFICATION
        //  Returns element description of the primary offender
        // ════════════════════════════════════════════════════════════════

        public static string IdentifyPrimaryOffender(List<ClashResult> clashes)
        {
            var freq = new Dictionary<string, int>();
            var names = new Dictionary<string, string>();

            foreach (var c in clashes)
            {
                string idA = ElementKey(c, true);
                string idB = ElementKey(c, false);

                if (c.ElementAId > 0)
                {
                    freq[idA] = (freq.TryGetValue(idA, out int va) ? va : 0) + 1;
                    if (!names.ContainsKey(idA))
                        names[idA] = $"{c.CategoryNameA ?? c.DisciplineA.ToString()} ID:{idA}";
                }
                if (c.ElementBId > 0)
                {
                    freq[idB] = (freq.TryGetValue(idB, out int vb) ? vb : 0) + 1;
                    if (!names.ContainsKey(idB))
                        names[idB] = $"{c.CategoryNameB ?? c.DisciplineB.ToString()} ID:{idB}";
                }
            }

            if (!freq.Any()) return "";
            string topId = freq.OrderByDescending(kv => kv.Value).First().Key;
            return names.TryGetValue(topId, out string? name) ? name : topId.ToString();
        }

        // ════════════════════════════════════════════════════════════════
        //  COORDINATION INTELLIGENCE
        //  Generates a human-readable coordination report for BIM managers
        // ════════════════════════════════════════════════════════════════

        public static string GenerateCoordinationReport(List<ClashGroup> groups)
        {
            if (!groups.Any()) return "No clashes detected.";

            int totalClashes = groups.Sum(g => g.Count);
            int groupCount   = groups.Count;
            int critical     = groups.Count(g => g.MaxSeverity == ClashSeverity.Critical);

            var report = new System.Text.StringBuilder();
            report.AppendLine($"═══ COORDINATION INTELLIGENCE REPORT ═══");
            report.AppendLine($"Total Issues: {groupCount} groups / {totalClashes} individual clashes");
            report.AppendLine($"Critical Groups: {critical}");
            report.AppendLine();

            // Top offenders (elements causing most clashes)
            var allClashes = groups.SelectMany(g => g.Clashes).ToList();
            var rootOffenders = GetTopOffenders(allClashes, top: 5);

            if (rootOffenders.Any())
            {
                report.AppendLine("── TOP ROOT CAUSE ELEMENTS ──");
                foreach (var (name, count) in rootOffenders)
                    report.AppendLine($"  • {name}: {count} clashes");
                report.AppendLine();
            }

            // Worst groups
            report.AppendLine("── CRITICAL GROUPS ──");
            foreach (var g in groups.Take(10))
            {
                string sev = g.MaxSeverity == ClashSeverity.Critical ? "🔴" :
                             g.MaxSeverity == ClashSeverity.Hard     ? "🟠" : "🟡";
                report.AppendLine($"  {sev} {g.GroupTitle}");
                if (!string.IsNullOrEmpty(g.PrimaryOffender))
                    report.AppendLine($"     Root cause: {g.PrimaryOffender}");
            }

            return report.ToString();
        }

        private static List<(string name, int count)> GetTopOffenders(
            List<ClashResult> clashes, int top = 5)
        {
            var freq  = new Dictionary<string, int>();
            var names = new Dictionary<string, string>();

            foreach (var c in clashes)
            {
                void Register(string id, string name)
                {
                    freq[id] = (freq.TryGetValue(id, out int v) ? v : 0) + 1;
                    if (!names.ContainsKey(id)) names[id] = name;
                }

                if (c.ElementAId > 0) Register(ElementKey(c, true),
                    $"{c.CategoryNameA} (ID:{c.ElementAId})");
                if (c.ElementBId > 0) Register(ElementKey(c, false),
                    $"{c.CategoryNameB} (ID:{c.ElementBId})");
            }

            return freq.OrderByDescending(kv => kv.Value).Take(top)
                .Select(kv => (names.TryGetValue(kv.Key, out string? n) ? n : kv.Key.ToString(), kv.Value))
                .ToList();
        }

        private static string ElementKey(ClashResult c, bool a) => ClashIdentity.Pair(c.HostDocumentKey,
            a ? c.ElementAId : c.ElementBId, a ? c.LinkInstanceA : c.LinkInstanceB, 0, "");

        private static double PointDistance(Autodesk.Revit.DB.XYZ a, Autodesk.Revit.DB.XYZ b)
            => Math.Sqrt(
                Math.Pow(a.X - b.X, 2) +
                Math.Pow(a.Y - b.Y, 2) +
                Math.Pow(a.Z - b.Z, 2));
    }
}
