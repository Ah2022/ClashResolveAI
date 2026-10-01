using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.ClashEngine
{
    // For curved surfaces, sampled points are projected back onto the actual Revit
    // face. A witness below the threshold proves a violation; an absent witness
    // does NOT prove clearance. No bounding-box distance is called a surface gap.
    internal static class SurfaceDistance
    {
        internal sealed class Evidence
        {
            public double Distance = double.PositiveInfinity;
            public XYZ Point = XYZ.Zero;
            public bool Exact;
            public string Method = "Unverified surface clearance";
        }
        internal sealed class Prepared
        {
            internal List<Face> Faces = new List<Face>();
            internal List<XYZ[]> Triangles = new List<XYZ[]>();
            internal bool Planar;
        }
        internal static Prepared Prepare(List<Solid> solids)
        {
            var faces=solids.SelectMany(s=>s.Faces.Cast<Face>()).ToList();
            return new Prepared { Faces=faces,Triangles=Triangles(faces),Planar=faces.All(f=>f is PlanarFace) };
        }
        internal static IEnumerable<int> MeasureSteps(List<Solid> aa,List<Solid> bb,double threshold,Evidence e)
            => MeasureSteps(Prepare(aa),Prepare(bb),threshold,e);
        internal static IEnumerable<int> MeasureSteps(Prepared aa,Prepared bb,double threshold,Evidence e)
        {
            var fa=aa.Faces;var fb=bb.Faces;
            bool planar=aa.Planar&&bb.Planar;
            var ta=aa.Triangles;var tb=bb.Triangles;
            // Avoid pathological meshes blocking the UI. Explicitly unverified.
            if ((long)ta.Count * tb.Count > 100000) { e.Method = "Unverified: surface complexity limit"; yield break; }
            if(ta.Count==0||tb.Count==0){e.Method="No tessellated boundary";yield break;}
            if (planar)
            {
                foreach (var a in ta) foreach (var b in tb)
                {
                    yield return 0;
                    foreach (var p in a) Consider(e, p, Closest(p,b[0],b[1],b[2]));
                    foreach (var p in b) Consider(e, p, Closest(p,a[0],a[1],a[2]));
                    for(int i=0;i<3;i++) for(int j=0;j<3;j++)
                    { var pair = Segments(a[i],a[(i+1)%3],b[j],b[(j+1)%3]); Consider(e,pair.Item1,pair.Item2); }
                }
                e.Exact = true; e.Method = "Planar solid surface distance";
            }
            else
            {
                // Mesh vertices lie on the source boundary; project onto the target
                // trimmed face to obtain an actual pair of boundary points.
                foreach (var p in ta.SelectMany(t => t)) foreach(var f in fb)
                    { yield return 0; Project(e,p,f); }
                foreach (var p in tb.SelectMany(t => t)) foreach(var f in fa)
                    { yield return 0; Project(e,p,f); }
                e.Method = e.Distance < threshold ? "Confirmed surface witness; gap is an upper bound" : "Unverified curved-surface clearance";
            }
            yield break;
        }
        private static void Project(Evidence e, XYZ p, Face f)
        {
            try { var hit=f.Project(p); if(hit!=null && f.IsInside(hit.UVPoint)) Consider(e,p,hit.XYZPoint); }
            catch { /* A failed projection cannot establish clearance. */ }
        }
        private static List<XYZ[]> Triangles(List<Face> faces)
        {
            var result = new List<XYZ[]>();
            foreach(var face in faces) {
                using var mesh=face.Triangulate();
                for(int i=0;i<mesh.NumTriangles;i++) {
                    var t=mesh.get_Triangle(i);
                    result.Add(new[] {t.get_Vertex(0),t.get_Vertex(1),t.get_Vertex(2)});
                }
            }
            return result;
        }
        private static void Consider(Evidence e,XYZ a,XYZ b)
        { double d=a.DistanceTo(b); if(d<e.Distance){e.Distance=d;e.Point=(a+b)*0.5;} }
        private static double Clamp(double x)=>Math.Max(0,Math.Min(1,x));
        private static Tuple<XYZ,XYZ> Segments(XYZ p,XYZ q,XYZ r,XYZ s)
        {
            var d1=q-p; var d2=s-r; var v=p-r;
            double a=d1.DotProduct(d1),b=d1.DotProduct(d2),c=d2.DotProduct(d2),d=d1.DotProduct(v),e=d2.DotProduct(v);
            double u=0,t=0,den=a*c-b*b;
            if(a<1e-20) t=c<1e-20?0:Clamp(e/c);
            else if(c<1e-20) u=Clamp(-d/a);
            else {
                u=den>1e-20?Clamp((b*e-c*d)/den):0;
                t=(b*u+e)/c;
                if(t<0){t=0;u=Clamp(-d/a);} else if(t>1){t=1;u=Clamp((b-d)/a);}
            }
            return Tuple.Create(p+d1*u,r+d2*t);
        }
        private static XYZ Closest(XYZ p,XYZ a,XYZ b,XYZ c)
        {
            var ab=b-a;var ac=c-a;var ap=p-a;
            double d1=ab.DotProduct(ap),d2=ac.DotProduct(ap);
            if(d1<=0&&d2<=0)return a;
            var bp=p-b;double d3=ab.DotProduct(bp),d4=ac.DotProduct(bp);
            if(d3>=0&&d4<=d3)return b;
            double vc=d1*d4-d3*d2;
            if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;double d5=ab.DotProduct(cp),d6=ac.DotProduct(cp);
            if(d6>=0&&d5<=d6)return c;
            double vb=d5*d2-d1*d6;
            if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            double va=d3*d6-d5*d4;
            if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
            double total=va+vb+vc;
            if(Math.Abs(total)<1e-20)return a;
            return a+ab*(vb/total)+ac*(vc/total);
        }
    }
}
