// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMEngine.Core.Geometry;
using ClashResolveAI.Core;
using SpatialElement = BIMEngine.Core.Models.SpatialElement;

namespace ClashResolveAI.Inspection
{
    public static class InspectionGeometry
    {
        public static Point3 Point(XYZ p) => new Point3(p.X,p.Y,p.Z);
        public static XYZ XYZ(Point3 p) => new XYZ(p.X,p.Y,p.Z);
        public static Element Resolve(Document host,long id,string linkId,out RevitLinkInstance link)
        {
            link=string.IsNullOrEmpty(linkId)?null:host.GetElement(linkId) as RevitLinkInstance;
            var source=string.IsNullOrEmpty(linkId)?host:link?.GetLinkDocument();
            var element=source?.GetElement(new ElementId(id));
            if(element==null)throw new InvalidOperationException("Element or linked model unavailable. Refresh the clash list.");
            return element;
        }
        private static SpatialElement Entry(Element element, RevitLinkInstance link, long key) => new SpatialElement {
            ElementId=element.Id.Value, ElementUniqueId=element.UniqueId, ScanKey=key,
            IsFromLinkedDocument=link!=null, LinkedDocumentReferenceId=link?.UniqueId };
        private static Point3 Direction(Element element, RevitLinkInstance link, IList<Solid> solids)
        {
            if(element.Location is LocationCurve location)
            {
                var tangent=location.Curve.ComputeDerivatives(.5,true).BasisX;
                return Point(link==null?tangent:link.GetTotalTransform().OfVector(tangent)).Unit;
            }
            var points=solids.SelectMany(s => Bounds(s.GetBoundingBox())).ToArray();
            if(points.Length==0) return new Point3(1,0,0);
            double dx=points.Max(p=>p.X)-points.Min(p=>p.X),dy=points.Max(p=>p.Y)-points.Min(p=>p.Y),dz=points.Max(p=>p.Z)-points.Min(p=>p.Z);
            return dz>dx&&dz>dy?new Point3(0,0,1):dy>dx?new Point3(0,1,0):new Point3(1,0,0);
        }
        internal static IEnumerable<Point3> Bounds(BoundingBoxXYZ box)
        {
            for(int i=0;i<8;i++) yield return Point(box.Transform.OfPoint(new XYZ((i&1)==0?box.Min.X:box.Max.X,(i&2)==0?box.Min.Y:box.Max.Y,(i&4)==0?box.Min.Z:box.Max.Z)));
        }
        private static InspectionMesh Mesh(Solid solid,int role)
        {
            var points=new List<Point3>();
            foreach(Face face in solid.Faces)
            {
                using(var mesh=face.Triangulate(.35))
                    for(int i=0;i<mesh.NumTriangles;i++)
                    {
                        var triangle=mesh.get_Triangle(i);
                        for(int j=0;j<3;j++) points.Add(Point(triangle.get_Vertex(j)));
                    }
            }
            return new InspectionMesh(points,role);
        }
        public static InspectionScene Build(Document doc, ClashResult row, double padding)
        {
            if(!DocumentSession.Matches(row,doc))throw new InvalidOperationException("Activate the clash project first.");
            var a=Resolve(doc,row.ElementAId,row.LinkInstanceA,out var linkA); var b=Resolve(doc,row.ElementBId,row.LinkInstanceB,out var linkB);
            var scene=new InspectionScene { Project=row.HostDocumentKey, IssueId=0, Clash=Point(row.ClashPoint) };
            using(var cache=new SolidCache(doc))
            {
                var sa=cache.GetSolids(Entry(a,linkA,1)); var sb=cache.GetSolids(Entry(b,linkB,2));
                if(sa.Count==0||sb.Count==0) throw new InvalidOperationException("Preview geometry unavailable. " + string.Join("; ",cache.Diagnostics));
                foreach(var solid in sa) scene.Meshes.Add(Mesh(solid,0));
                foreach(var solid in sb) scene.Meshes.Add(Mesh(solid,1));
                var all=scene.Meshes.SelectMany(m=>m.Points).ToArray();
                scene.Min=new Point3(all.Min(p=>p.X),all.Min(p=>p.Y),all.Min(p=>p.Z));
                scene.Max=new Point3(all.Max(p=>p.X),all.Max(p=>p.Y),all.Max(p=>p.Z));
                scene.AxisA=Direction(a,linkA,sa); scene.AxisB=Direction(b,linkB,sb);
                bool failed=false; Point3 weighted=new Point3();
                foreach(var first in sa) foreach(var second in sb)
                {
                    try
                    {
                        using(var overlap=BooleanOperationsUtils.ExecuteBooleanOperation(first,second,BooleanOperationsType.Intersect))
                        {
                            if(overlap==null||overlap.Volume<=1e-9) continue;
                            scene.Meshes.Add(Mesh(overlap,2));
                            weighted+=Point(overlap.ComputeCentroid())*overlap.Volume; scene.OverlapVolume+=overlap.Volume;
                        }
                    }
                    catch(Autodesk.Revit.Exceptions.InvalidOperationException) { failed=true; }
                    catch(Autodesk.Revit.Exceptions.ArgumentException) { failed=true; }
                }
                if(scene.OverlapVolume>0) scene.Clash=weighted*(1/scene.OverlapVolume);
                scene.Notice=failed?"Some overlap geometry could not be computed; red overlay is incomplete.":scene.HasOverlap?"Red: current solid intersection":"No current solid overlap. Refresh / Rescan to update this issue.";
                // Bounded context near the clash, including linked geometry in host coordinates.
                double radius=Math.Max(padding*2,10);
                var lo=XYZ(scene.Clash-new Point3(radius,radius,radius)); var hi=XYZ(scene.Clash+new Point3(radius,radius,radius));
                long key=3; int added=0;
                foreach(var source in new[]{Tuple.Create(doc,(RevitLinkInstance)null)}.Concat(
                    new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                        .Where(l=>l.GetLinkDocument()!=null&&!l.GetTotalTransform().HasReflection).Select(l=>Tuple.Create(l.GetLinkDocument(),l))))
                {
                    Transform transform=source.Item2?.GetTotalTransform()??Transform.Identity;
                    var corners=Bounds(new BoundingBoxXYZ { Min=lo,Max=hi }).Select(p=>transform.Inverse.OfPoint(XYZ(p))).ToArray();
                    using(var outline=new Outline(new XYZ(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Min(p=>p.Z)),new XYZ(corners.Max(p=>p.X),corners.Max(p=>p.Y),corners.Max(p=>p.Z))))
                    {
                        var elements=new FilteredElementCollector(source.Item1).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline))
                            .Where(e=>!e.ViewSpecific&&e.Category?.CategoryType==CategoryType.Model&&!(e is RevitLinkInstance));
                        foreach(var element in elements)
                        {
                            if((source.Item2?.UniqueId??"")==(row.LinkInstanceA??"")&&element.UniqueId==a.UniqueId ||
                               (source.Item2?.UniqueId??"")==(row.LinkInstanceB??"")&&element.UniqueId==b.UniqueId) continue;
                            if(added>=60 || scene.TriangleCount>150000) { scene.Notice+=" · Nearby context limited for responsiveness."; break; }
                            var contextSolids=cache.GetSolids(Entry(element,source.Item2,key++));
                            foreach(var solid in contextSolids) scene.Meshes.Add(Mesh(solid,3));
                            if(contextSolids.Count>0)scene.NativeContextIds.Add((source.Item2?.Id??element.Id).Value);
                            added++;
                        }
                    }
                    if(added>=60) break;
                }
            }
            return scene;
        }
    }
}
