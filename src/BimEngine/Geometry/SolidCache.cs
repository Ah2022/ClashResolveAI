// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SpatialElement = BIMEngine.Core.Models.SpatialElement;

namespace BIMEngine.Core.Geometry
{
    public sealed class SolidCache : IDisposable
    {
        private readonly Document _doc;
        private readonly Dictionary<long, List<Solid>> _cache = new Dictionary<long, List<Solid>>();
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        public readonly List<string> Diagnostics = new List<string>();
        public int ApiCallCount { get; private set; }
        public SolidCache(Document doc) { _doc = doc; }

        public List<Solid> GetSolids(SpatialElement info)
        {
            if (_cache.TryGetValue(info.ScanKey, out var cached)) return cached;
            ApiCallCount++;
            var solids = new List<Solid>();
            _cache.Add(info.ScanKey, solids);
            try
            {
                Document source = _doc;
                Transform transform = Transform.Identity;
                if (info.IsFromLinkedDocument)
                {
                    var link = _doc.GetElement(info.LinkedDocumentReferenceId) as RevitLinkInstance;
                    source = link?.GetLinkDocument();
                    transform = link?.GetTotalTransform();
                    if (transform != null && transform.HasReflection)
                        throw new InvalidOperationException("Mirrored link geometry is not supported; scan coverage is partial.");
                }
                var el = source?.GetElement(new ElementId(info.ElementId));
                if (el == null || transform == null) throw new InvalidOperationException("Element or link unavailable.");
                using (var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine })
                {
                    var geometry = el.get_Geometry(options);
                    if (geometry != null) { _owned.Add(geometry); Extract(geometry, transform, solids); }
                }
                if (solids.Count == 0) Diagnostics.Add("No usable solid: " + info.ScanKey);
            }
            catch (Exception ex)
            {
                solids.Clear();
                Diagnostics.Add("Geometry unavailable for " + info.ScanKey + ": " + ex.Message);
            }
            return solids;
        }

        private void Extract(GeometryElement geometry, Transform transform, List<Solid> solids)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid && solid.Volume > 1e-9)
                {
                    var copy = SolidUtils.CreateTransformed(solid, transform);
                    _owned.Add(copy);
                    solids.Add(copy);
                }
                else if (obj is GeometryInstance instance)
                {
                    // Instance geometry is already in its parent coordinate system.
                    var nested = instance.GetInstanceGeometry();
                    if (nested != null) { _owned.Add(nested); Extract(nested, transform, solids); }
                }
            }
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear(); _cache.Clear();
        }
    }
}

