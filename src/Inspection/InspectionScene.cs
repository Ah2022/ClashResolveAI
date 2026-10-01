// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Inspection
{
    // Plain immutable coordinates: neither WPF nor graphics callbacks access live geometry.
    public struct Point3
    {
        public readonly double X, Y, Z;
        public Point3(double x, double y, double z) { X=x; Y=y; Z=z; }
        public static Point3 operator +(Point3 a, Point3 b) => new Point3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Point3 operator -(Point3 a, Point3 b) => new Point3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point3 operator *(Point3 a, double s) => new Point3(a.X*s,a.Y*s,a.Z*s);
        public double Dot(Point3 b) => X*b.X+Y*b.Y+Z*b.Z;
        public Point3 Cross(Point3 b) => new Point3(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public double Length => Math.Sqrt(Dot(this));
        public Point3 Unit => Length < 1e-9 ? new Point3(1,0,0) : this*(1/Length);
    }
    public sealed class InspectionMesh
    {
        // Triangle soup, three points per triangle. 0=A, 1=B, 2=overlap, 3=context.
        public readonly Point3[] Points;
        public readonly int Role;
        public InspectionMesh(IEnumerable<Point3> points, int role) { Points=points.ToArray(); Role=role; }
    }
    public sealed class InspectionScene
    {
        public string Project;
        public long IssueId;
        public Point3 Min, Max, Clash, AxisA, AxisB;
        public readonly List<InspectionMesh> Meshes = new List<InspectionMesh>();
        public readonly HashSet<long> NativeContextIds = new HashSet<long>();
        public string Notice = "";
        public double OverlapVolume;
        public bool HasOverlap => Meshes.Any(m => m.Role==2);
        public Point3 Center => (Min+Max)*.5;
        public int TriangleCount => Meshes.Sum(m => m.Points.Length/3);
        public void Frame(double padding, bool focus, out Point3 min, out Point3 max)
        {
            var p = new Point3(padding,padding,padding);
            min = focus ? Clash-p : Min-p;
            max = focus ? Clash+p : Max+p;
        }
        public void Axes(string mode, int corner, bool useB, out Point3 right, out Point3 up, out Point3 forward)
        {
            var axis=(useB?AxisB:AxisA).Unit;
            if (mode=="2d") { right=new Point3(1,0,0); up=new Point3(0,1,0); forward=new Point3(0,0,-1); return; }
            if (mode=="section")
            {
                forward=axis;
                var seed=Math.Abs(axis.Z)>.95?new Point3(0,1,0):new Point3(0,0,1);
                right=forward.Cross(seed).Unit; up=right.Cross(forward).Unit;
                return;
            }
            var horizontal=new Point3(axis.X,axis.Y,0).Unit;
            var side=new Point3(-horizontal.Y,horizontal.X,0);
            int c=((corner%4)+4)%4;
            var eye=horizontal*(c==0||c==3?1:-1)+side*(c<2?1:-1)+new Point3(0,0,.85);
            forward=eye.Unit*(-1); right=forward.Cross(new Point3(0,0,1)).Unit; up=right.Cross(forward).Unit;
        }

        // Clip triangles to a local half-space; used for real section cuts, not just projections.
        public static List<Point3> Clip(IList<Point3> polygon, Point3 origin, Point3 normal, double limit)
        {
            var result=new List<Point3>();
            if (polygon.Count==0) return result;
            Point3 previous=polygon[polygon.Count-1]; double dp=(previous-origin).Dot(normal)-limit;
            foreach(var current in polygon)
            {
                double dc=(current-origin).Dot(normal)-limit;
                if ((dp<=0)!=(dc<=0)) result.Add(previous+(current-previous)*(dp/(dp-dc)));
                if(dc<=0) result.Add(current);
                previous=current; dp=dc;
            }
            return result;
        }
    }
}
