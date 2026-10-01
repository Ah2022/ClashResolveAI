// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace BIMEngine.Core.Spatial
{
    /// <summary>
    /// A single entry stored in the octree: a per-scan identity (not the Revit
    /// ElementId) plus its bounding box. Host/link instances have distinct keys.
    /// </summary>
    public struct SpatialEntry
    {
        public long ElementId;
        public AABB Bounds;

        public SpatialEntry(long elementId, AABB bounds)
        {
            ElementId = elementId;
            Bounds = bounds;
        }
    }

    /// <summary>
    /// Loose octree used to cut the candidate-pair search space (Step 3/4 of the
    /// Model Scanner pipeline). "Loose" = child bounds are expanded slightly so
    /// elements straddling a split plane don't get pushed up to a coarse ancestor
    /// node, which is what kills recall in naive strict octrees for long, thin
    /// MEP elements like pipes and cable trays.
    ///
    /// Default parameters favour typical MEP densities. If profiling on a
    /// specific model shows clustering (e.g. a dense plant room vs. open
    /// corridors), tune MaxObjectsPerNode / MaxDepth rather than switching
    /// data structures.
    /// </summary>
    public class Octree
    {
        private const int DefaultMaxObjectsPerNode = 16;
        private const int DefaultMaxDepth = 8;
        private const double LooseFactor = 1.25; // 25% child-bound expansion

        private readonly OctreeNode _root;
        private readonly int _maxObjectsPerNode;
        private readonly int _maxDepth;

        public Octree(AABB worldBounds, int maxObjectsPerNode = DefaultMaxObjectsPerNode, int maxDepth = DefaultMaxDepth)
        {
            _maxObjectsPerNode = maxObjectsPerNode;
            _maxDepth = maxDepth;
            _root = new OctreeNode(worldBounds, 0);
        }

        /// <summary>Builds a tree sized to exactly fit the supplied entries, then inserts them all.
        /// This is the entry point ScanEngine calls after Step 1/2 (collect + bounding boxes).</summary>
        public static Octree Build(IEnumerable<SpatialEntry> entries,
                                    int maxObjectsPerNode = DefaultMaxObjectsPerNode,
                                    int maxDepth = DefaultMaxDepth,
                                    System.Action<int, int> progress = null)
        {
            var list = entries as IList<SpatialEntry> ?? entries.ToList();
            if (list.Count == 0)
                return new Octree(new AABB(0, 0, 0, 0, 0, 0), maxObjectsPerNode, maxDepth);

            var world = list[0].Bounds;
            for (int i = 1; i < list.Count; i++)
                world = world.Union(list[i].Bounds);

            // Pad the world box slightly so entries exactly on the outer face
            // never sit on a boundary during recursive splitting.
            world = world.Expand(0.5);

            var tree = new Octree(world, maxObjectsPerNode, maxDepth);
            for (int i = 0; i < list.Count; i++)
            {
                if (i % 256 == 0) progress?.Invoke(i, list.Count);
                tree.Insert(list[i]);
            }
            return tree;
        }

        public void Insert(SpatialEntry entry) => _root.Insert(entry, _maxObjectsPerNode, _maxDepth, LooseFactor);

        /// <summary>
        /// Step 4: "for each pipe, find nearby objects only" — returns every entry
        /// whose (possibly expanded) bounds intersect the query region, instead of
        /// comparing against every element in the model.
        /// </summary>
        public List<SpatialEntry> Query(AABB region)
        {
            var results = new List<SpatialEntry>();
            _root.Query(region, results);
            return results;
        }

        public int CountEntries() => _root.CountEntries();
    }

    internal class OctreeNode
    {
        private readonly AABB _bounds;
        private readonly int _depth;
        private List<SpatialEntry> _entries;
        private OctreeNode[] _children; // 8 octants when subdivided, else null

        public OctreeNode(AABB bounds, int depth)
        {
            _bounds = bounds;
            _depth = depth;
            _entries = new List<SpatialEntry>();
        }

        public void Insert(SpatialEntry entry, int maxObjectsPerNode, int maxDepth, double looseFactor)
        {
            if (_children != null)
            {
                InsertIntoChild(entry, maxObjectsPerNode, maxDepth, looseFactor);
                return;
            }

            _entries.Add(entry);

            if (_entries.Count > maxObjectsPerNode && _depth < maxDepth)
                Subdivide(maxObjectsPerNode, maxDepth, looseFactor);
        }

        private void Subdivide(int maxObjectsPerNode, int maxDepth, double looseFactor)
        {
            double cx = _bounds.CenterX, cy = _bounds.CenterY, cz = _bounds.CenterZ;
            double hx = (_bounds.MaxX - _bounds.MinX) * 0.5 * looseFactor * 0.5;
            double hy = (_bounds.MaxY - _bounds.MinY) * 0.5 * looseFactor * 0.5;
            double hz = (_bounds.MaxZ - _bounds.MinZ) * 0.5 * looseFactor * 0.5;

            _children = new OctreeNode[8];
            int idx = 0;
            for (int ix = 0; ix < 2; ix++)
            for (int iy = 0; iy < 2; iy++)
            for (int iz = 0; iz < 2; iz++)
            {
                // Strict octant (one of the 8 even splits of the parent box)...
                var quadrant = new AABB(
                    ix == 0 ? _bounds.MinX : cx, iy == 0 ? _bounds.MinY : cy, iz == 0 ? _bounds.MinZ : cz,
                    ix == 0 ? cx : _bounds.MaxX, iy == 0 ? cy : _bounds.MaxY, iz == 0 ? cz : _bounds.MaxZ);

                // ...then padded ("loose") so elements straddling the split plane
                // still fit fully inside exactly one child instead of being
                // bumped up to this node.
                double longestEdge = System.Math.Max(quadrant.MaxX - quadrant.MinX,
                                      System.Math.Max(quadrant.MaxY - quadrant.MinY, quadrant.MaxZ - quadrant.MinZ));
                double pad = longestEdge * (looseFactor - 1.0) * 0.5;
                _children[idx++] = new OctreeNode(quadrant.Expand(pad), _depth + 1);
            }

            var toReinsert = _entries;
            _entries = null;
            foreach (var e in toReinsert)
                InsertIntoChild(e, maxObjectsPerNode, maxDepth, looseFactor);
        }

        private void InsertIntoChild(SpatialEntry entry, int maxObjectsPerNode, int maxDepth, double looseFactor)
        {
            foreach (var child in _children)
            {
                if (child.CanHold(entry.Bounds))
                {
                    child.Insert(entry, maxObjectsPerNode, maxDepth, looseFactor);
                    return;
                }
            }
            // Straddles a split plane even with loose bounds (rare for very long
            // elements) — keep it at this level so it's never dropped from queries.
            (_entries ?? (_entries = new List<SpatialEntry>())).Add(entry);
        }

        private bool CanHold(AABB box) => _bounds.Contains(box);

        public void Query(AABB region, List<SpatialEntry> results)
        {
            if (!_bounds.Intersects(region))
                return;

            if (_entries != null)
            {
                foreach (var e in _entries)
                    if (e.Bounds.Intersects(region))
                        results.Add(e);
            }

            if (_children != null)
                foreach (var child in _children)
                    child.Query(region, results);
        }

        public int CountEntries()
        {
            int count = _entries?.Count ?? 0;
            if (_children != null)
                foreach (var child in _children)
                    count += child.CountEntries();
            return count;
        }
    }
}
