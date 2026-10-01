// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using BIMEngine.Core.Spatial;

namespace BIMEngine.Core.Models
{
    /// <summary>
    /// The "extract once" snapshot: everything the octree/candidate-pair stage
    /// needs, read from the Revit API exactly once per element in
    /// ElementCollector.Collect(), then never touched against live Revit
    /// objects again until Step 5 (and even then, only for the narrow
    /// candidate set - see Geometry/SolidCache.cs).
    ///
    ///   Revit API ---> SpatialElement (this class, in memory)
    ///                        |
    ///                        v
    ///                  Octree processing (Spatial/Octree.cs,
    ///                  Spatial/CandidatePairFinder.cs) - zero Revit API
    ///                  calls in either file, verified by grep in the
    ///                  Phase 1 README.
    /// </summary>
    public class SpatialElement
    {
        public long ElementId;
        public long ScanKey;
        public string ElementUniqueId;
        public string DocumentKey;
        public string Category;      // e.g. "Pipes", "Ducts", "Cable Trays", "Structural Framing", "Structural Columns"
        public string SystemType;
        public string SystemClassification;
        public string SystemName;
        public string FamilyType;
        public string DocumentTitle; // host document title, or linked document title
        public bool IsFromLinkedDocument;
        public string LinkedDocumentReferenceId; // RevitLinkInstance UniqueId, empty for host elements

        public AABB Bounds;

        /// <summary>Representative point: LocationPoint for point-based elements
        /// (columns), midpoint of the location curve for line-based elements
        /// (pipes, ducts, cable trays, framing).</summary>
        public double LocationX, LocationY, LocationZ;

        public string LevelName;
        public long LevelId;

        public SpatialEntry ToSpatialEntry() => new SpatialEntry(ScanKey, Bounds);
    }
}
