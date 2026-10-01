using System;
using System.Collections.Generic;
using System.Diagnostics;
using ClashResolveAI.Core;

namespace ClashResolveAI.ClashEngine
{
    public sealed class ScanStatistics
    {
        public string Phase = "Preparing";
        public ScanMode Mode;
        public bool IncludeLinkToLink, NativeLinkQueries;
        public ScanScope Scope=new ScanScope();
        public int BelowTolerance, SkippedClearance;
        public int IndexedElements, TotalSources, CompletedSources, GeometryCacheHits, GeometryLoads;
        public long WallMilliseconds;
        public double ProgressPercent => Phase=="Complete"?100:TotalSources==0?0:Math.Min(99,15+85.0*CompletedSources/TotalSources);
        public int Sources, Candidates, Tested, Excluded, MissingGeometry, BooleanFailures, Unverified;
        public long WorkMilliseconds;
        public double MaxSliceMilliseconds;
        public int SliceCount;
        public int SlicesOver50ms;
        public double CollectIndexMilliseconds, CandidateMilliseconds, GeometryMilliseconds, BooleanMilliseconds, SurfaceDistanceMilliseconds;
        public int BooleanTests, SurfaceDistanceCalls;
        // Measure only active iterator work; never include time suspended between API slices.
        internal static IEnumerable<T> Timed<T>(IEnumerable<T> work,Action<double> record)
        {
            using var iterator=work.GetEnumerator();
            while(true) {
                bool next;long start=Stopwatch.GetTimestamp();
                try { next=iterator.MoveNext(); }
                finally { record((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency); }
                if(!next)yield break;
                yield return iterator.Current;
            }
        }
        internal static double Since(long start)=>(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
        public string TimingSummary => $"collect/index {CollectIndexMilliseconds:F2} ms; candidates {CandidateMilliseconds:F2} ms; geometry {GeometryMilliseconds:F2} ms; Boolean {BooleanMilliseconds:F2} ms ({BooleanTests} calls); surface distance {SurfaceDistanceMilliseconds:F2} ms ({SurfaceDistanceCalls} calls)";
        public string Summary => $"{Phase} | {Mode} | below tolerance {BelowTolerance}; skipped clearance {SkippedClearance} | {ProgressPercent:F0}% | {CompletedSources}/{TotalSources} sources | {IndexedElements} indexed; Sources {Sources}; candidate pairs {Candidates}; tested {Tested}; excluded {Excluded}; missing geometry {MissingGeometry}; Boolean failures {BooleanFailures}; unverified {Unverified}; elapsed {WallMilliseconds/1000.0:F1}s; API work {WorkMilliseconds} ms; longest slice {MaxSliceMilliseconds:F1} ms; {TimingSummary}";
    }
    public sealed class ScanJob : IDisposable
    {
        private readonly IEnumerator<int> _work;
        private readonly Stopwatch _elapsed=Stopwatch.StartNew();
        private readonly Action? _cleanup;
        private bool _disposed;
        public readonly List<ClashResult> Results;
        public readonly ScanStatistics Statistics;
        public bool Complete { get; private set; }
        public bool Cancelled { get; private set; }
        internal ScanJob(IEnumerable<int> work, List<ClashResult> results, ScanStatistics stats, Action? cleanup=null)
        { _cleanup=cleanup; _work = work.GetEnumerator(); Results = results; Statistics = stats; }
        public bool Advance(int milliseconds)
        {
            if (Complete || Cancelled) return true;
            var timer = Stopwatch.StartNew();
            try {
                do { if (!_work.MoveNext()) { Complete = true; Statistics.Phase="Complete"; break; } }
                while (timer.ElapsedMilliseconds < Math.Max(1, milliseconds));
            } finally {
                Statistics.WallMilliseconds=_elapsed.ElapsedMilliseconds;
                Statistics.SliceCount++;if(timer.Elapsed.TotalMilliseconds>50)Statistics.SlicesOver50ms++;
                Statistics.WorkMilliseconds += timer.ElapsedMilliseconds;
                Statistics.MaxSliceMilliseconds = Math.Max(Statistics.MaxSliceMilliseconds, timer.Elapsed.TotalMilliseconds);
            }
            return Complete;
        }
        public void Dispose() { if(_disposed)return;_disposed=true;Cancelled = !Complete;_elapsed.Stop();try{_work.Dispose();}finally{_cleanup?.Invoke();} }
    }
}
