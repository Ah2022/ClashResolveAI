// LiveMonitor/ClashSnapshotService.cs  — NEW in v9.0
//
// Captures REAL PNG thumbnails from the live Revit model for each clash
// (3D isometric section box + 2D floor plan crop box). Replaces the old
// fake WPF polygon preview that never called Document.ExportImage.
//
// MUST run on the Revit API thread — only ever called from an
// IExternalEventHandler.Execute (ClashSnapshotHandler below).

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    public class ClashSnapshotResult
    {
        public string  ClashId { get; set; } = "";
        public string? Path3D  { get; set; }
        public string? Path2D  { get; set; }
        public string  Error   { get; set; } = "";
    }

    // Written on Revit thread; read on WPF dispatcher via SnapshotReady event.
    public class ClashSnapshotStore
    {
        private static ClashSnapshotStore? _instance;
        public  static ClashSnapshotStore   Instance =>
            _instance ?? (_instance = new ClashSnapshotStore());

        private readonly object _lock = new object();
        private ClashSnapshotResult? _latest;
        public event EventHandler? SnapshotReady;
        private ClashSnapshotStore() { }

        public void Publish(ClashSnapshotResult result)
        {
            lock (_lock) { _latest = result; }
            SnapshotReady?.Invoke(this, EventArgs.Empty);
        }
        public ClashSnapshotResult? GetLatest()
        {
            lock (_lock) return _latest;
        }
    }

    // ── IExternalEventHandler wrapper ─────────────────────────────────────
    // Raised by the panel row on UI-thread click; executes on Revit API thread.

    public class ClashSnapshotHandler : IExternalEventHandler
    {
        private ClashResult? _pending;
        private readonly Dictionary<string,ClashSnapshotResult> _cache=new Dictionary<string,ClashSnapshotResult>();
        private readonly object _pendLock = new object();

        public void RequestSnapshot(ClashResult clash)
        {
            lock (_pendLock) _pending = clash;
        }

        public void Execute(UIApplication app)
        {
            ClashResult? clash;
            lock (_pendLock) { clash = _pending; _pending = null; }
            if (clash == null) return;

            string key=clash.ClashId+":"+clash.GeometryRevision;
            if(_cache.TryGetValue(key,out var cached)&&File.Exists(cached.Path3D)&&File.Exists(cached.Path2D)){ClashSnapshotStore.Instance.Publish(cached);return;}
            var result = ClashSnapshotService.GenerateSnapshots(app, clash);
            if(_cache.Count>=32)_cache.Clear();
            _cache[key]=result;
            ClashSnapshotStore.Instance.Publish(result);
        }

        public string GetName() => "ClashResolveAI_SnapshotCapture";
    }

    // ── Core service ──────────────────────────────────────────────────────

    public static class ClashSnapshotService
    {
        private const string View3DName = "ClashResolveAI_PreviewSnap3D";
        private const string View2DName = "ClashResolveAI_PreviewSnap2D";
        private const int    PixelSize  = 460;

        public static ClashSnapshotResult GenerateSnapshots(UIApplication app, ClashResult clash)
        {
            var result = new ClashSnapshotResult { ClashId = clash?.ClashId ?? "" };
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc   = uidoc?.Document;
                if (doc == null || clash == null || !DocumentSession.Matches(clash, doc))
                {
                    result.Error = "No active document.";
                    return result;
                }
                if (clash.ElementA == null || !clash.ElementA.IsValidObject)
                {
                    result.Error = "Clash element no longer valid (model changed since detection).";
                    return result;
                }

                string folder = GetSnapshotFolder(doc);
                Directory.CreateDirectory(folder);

                string base3D = Path.Combine(folder, $"Clash_{SanitiseFileName(clash.ClashId)}_3D");
                string base2D = Path.Combine(folder, $"Clash_{SanitiseFileName(clash.ClashId)}_2D");

                result.Path3D = Export3D(uidoc!, doc, clash, base3D);
                result.Path2D = Export2D(uidoc!, doc, clash, base2D);

                if (result.Path3D == null && result.Path2D == null)
                    result.Error = "Both exports failed — check Output/Debug pane.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnapshotSvc] GenerateSnapshots: {ex.Message}");
                result.Error = ex.Message;
            }
            return result;
        }

        // ── 3D snapshot ─────────────────────────────────────────────────

        private static string? Export3D(
            UIDocument uidoc, Document doc, ClashResult clash, string baseFile)
        {
            try
            {
                bool bIsHost = clash.ElementB != null && clash.ElementB.IsValidObject &&
                               clash.ElementB.Document.Equals(doc);

                var ids = clash.GetHostSelectionIds(doc);

                View3D? view;
                using (var tx = new Transaction(doc, "ClashResolveAI — Preview Snap 3D"))
                {
                    tx.Start();

                    view = GetOrCreate3DView(doc);
                    if (view == null) { tx.RollBack(); return null; }

                    // Clear any prior temporary hide/isolate so the new
                    // IsolateElementsTemporary call is accepted.
                    if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                    // Build section box around clash point, expanded to
                    // include both elements' bounding boxes plus a small pad.
                    const double padFt = 3.0;   // ~900 mm on each side
                    XYZ cp = clash.ClashPoint ?? XYZ.Zero;
                    var box = new BoundingBoxXYZ
                    {
                        Min = new XYZ(cp.X - padFt, cp.Y - padFt, cp.Z - padFt),
                        Max = new XYZ(cp.X + padFt, cp.Y + padFt, cp.Z + padFt)
                    };
                    if (clash.ElementA.Document == doc) ExpandToInclude(box, clash.ElementA);
                    if (bIsHost) ExpandToInclude(box, clash.ElementB);

                    view.SetSectionBox(box);
                    view.IsSectionBoxActive = true;
                    if (ids.Count > 0) view.IsolateElementsTemporary(ids);
                    view.DetailLevel  = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.ShadingWithEdges;

                    tx.Commit();
                }

                return ExportViewToPng(doc, view!, baseFile);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Snapshot 3D export", ex);
                return null;
            }
        }

        private static View3D? GetOrCreate3DView(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == View3DName);
            if (existing != null) return existing;

            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            var v3 = View3D.CreateIsometric(doc, vft.Id);
            v3.Name = View3DName;
            return v3;
        }

        // ── 2D snapshot ─────────────────────────────────────────────────

        private static string? Export2D(
            UIDocument uidoc, Document doc, ClashResult clash, string baseFile)
        {
            try
            {
                XYZ cp = clash.ClashPoint ?? XYZ.Zero;

                var level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => Math.Abs(l.Elevation - cp.Z))
                    .FirstOrDefault();
                if (level == null) return null;

                bool bIsHost = clash.ElementB != null && clash.ElementB.IsValidObject &&
                               clash.ElementB.Document.Equals(doc);
                var ids = clash.GetHostSelectionIds(doc);

                ViewPlan? view;
                using (var tx = new Transaction(doc, "ClashResolveAI — Preview Snap 2D"))
                {
                    tx.Start();

                    view = GetOrCreate2DView(doc, level);
                    if (view == null) { tx.RollBack(); return null; }

                    if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                    using(var range=view.GetViewRange()){
                        double z=cp.Z-level.Elevation;
                        foreach(var plane in new[]{PlanViewPlane.TopClipPlane,PlanViewPlane.CutPlane,PlanViewPlane.BottomClipPlane,PlanViewPlane.ViewDepthPlane})range.SetLevelId(plane,level.Id);
                        range.SetOffset(PlanViewPlane.TopClipPlane,z+4);range.SetOffset(PlanViewPlane.CutPlane,z+0.1);
                        range.SetOffset(PlanViewPlane.BottomClipPlane,z-4);range.SetOffset(PlanViewPlane.ViewDepthPlane,z-4);view.SetViewRange(range);
                    }
                    // Tight plan crop: 9 ft (~2.7 m) radius square around clash
                    const double halfFt = 9.0;
                    view.CropBoxActive  = true;
                    view.CropBoxVisible = false;
                    var crop = view.CropBox;
                    crop.Min = new XYZ(cp.X - halfFt, cp.Y - halfFt, crop.Min.Z);
                    crop.Max = new XYZ(cp.X + halfFt, cp.Y + halfFt, crop.Max.Z);
                    view.CropBox = crop;

                    if (ids.Count > 0) view.IsolateElementsTemporary(ids);
                    view.DetailLevel  = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.ShadingWithEdges;

                    tx.Commit();
                }

                return ExportViewToPng(doc, view!, baseFile);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Snapshot 2D export", ex);
                return null;
            }
        }

        private static ViewPlan? GetOrCreate2DView(Document doc, Level level)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == View2DName);

            if (existing != null)
            {
                // Clash may be on a different level than when the view was created
                if (existing.GenLevel != null && existing.GenLevel.Id == level.Id)
                    return existing;
                doc.Delete(existing.Id);
            }

            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.FloorPlan);
            if (vft == null) return null;

            var vp = ViewPlan.Create(doc, vft.Id, level.Id);
            vp.Name = View2DName;
            return vp;
        }

        // ── Export helper ───────────────────────────────────────────────
        // ExportRange.CurrentView → output file is exactly FilePath + ".png"
        // (no view-name suffix, unlike ExportRange.SetOfViews).

        private static string? ExportViewToPng(Document doc, View view, string baseFileName)
        {
            try
            {
                var ieo = new ImageExportOptions
                {
                    ExportRange          = ExportRange.SetOfViews,
                    ZoomType              = ZoomFitType.FitToPage,
                    PixelSize             = PixelSize,
                    FitDirection          = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType   = ImageFileType.PNG,
                    ImageResolution       = ImageResolution.DPI_72,
                    FilePath              = baseFileName
                };
                ieo.SetViewsAndSheets(new[] { view.Id });
                doc.ExportImage(ieo);

                string expected = baseFileName + ".png";
                return File.Exists(expected) ? expected : Directory.GetFiles(Path.GetDirectoryName(baseFileName)!, Path.GetFileName(baseFileName) + "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Snapshot image export", ex);
                return null;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static void ExpandToInclude(BoundingBoxXYZ box, Element? el)
        {
            try
            {
                if (el == null || !el.IsValidObject) return;
                var bb = el.get_BoundingBox(null);
                if (bb == null) return;
                const double m = 0.5;
                box.Min = new XYZ(
                    Math.Min(box.Min.X, bb.Min.X - m),
                    Math.Min(box.Min.Y, bb.Min.Y - m),
                    Math.Min(box.Min.Z, bb.Min.Z - m));
                box.Max = new XYZ(
                    Math.Max(box.Max.X, bb.Max.X + m),
                    Math.Max(box.Max.Y, bb.Max.Y + m),
                    Math.Max(box.Max.Z, bb.Max.Z + m));
            }
            catch { }
        }

        private static string GetSnapshotFolder(Document doc)
        {
            string proj = SanitiseFileName(string.IsNullOrEmpty(doc.PathName)
                ? "Untitled"
                : Path.GetFileNameWithoutExtension(doc.PathName));
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClashResolveAI", "LiveSnapshots", proj);
        }

        private static string SanitiseFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
