// Adapted from the user-provided BIMEngine_Phase1_Hybrid project.
#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ClashResolveAI.Inspection
{
    /// <summary>Geometry-only preview. No transactions, view elements, or document changes.</summary>
    public sealed class InspectionViewport : Grid
    {
        private readonly Viewport3D viewport = new Viewport3D();
        private readonly Viewport3D overlapViewport = new Viewport3D { IsHitTestVisible=false };
        private readonly CutCanvas canvas = new CutCanvas();
        private readonly OrientationOverlay overlay=new OrientationOverlay { IsHitTestVisible=false };
        public event Action<double> ZoomChanged;
        public double Zoom => zoom;
        public void SetZoom(double value) { zoom=Math.Max(.15,Math.Min(20,value));Render(); }
        private InspectionScene scene;
        private string mode="2d";
        private int corner;
        private bool useB, context=true, focus;
        private double padding=6, zoom=1;
        private Vector pan;
        private Point last;
        private InspectionScene cachedScene;
        private bool cachedContext,cachedFocus;
        private double cachedPadding;
        private Model3DGroup cachedModels, cachedOverlap;
        public InspectionViewport()
        {
            Background=new SolidColorBrush(Color.FromRgb(10,15,26)); ClipToBounds=true; MinHeight=80;
            Children.Add(viewport); Children.Add(overlapViewport); Children.Add(canvas); Children.Add(overlay);
            SizeChanged+=(s,e)=>Render();
            MouseWheel+=(s,e)=>{ zoom=Math.Max(.15,Math.Min(20,zoom*(e.Delta>0?1.2:1/1.2))); Render(); ZoomChanged?.Invoke(zoom); e.Handled=true; };
            MouseLeftButtonDown+=(s,e)=>{ last=e.GetPosition(this); CaptureMouse(); };
            MouseMove+=(s,e)=>{ if(!IsMouseCaptured)return; var next=e.GetPosition(this); pan+=next-last; last=next; Render(); };
            MouseLeftButtonUp+=(s,e)=>ReleaseMouseCapture();
        }
        public void Display(InspectionScene value,string viewMode,int viewCorner,bool axisB,bool showContext,bool focusClash,double margin)
        {
            if(scene!=value || mode!=viewMode || corner!=viewCorner || useB!=axisB) { pan=new Vector(); }
            scene=value; mode=viewMode; corner=viewCorner; useB=axisB; context=showContext; focus=focusClash; padding=margin; Render();
        }
        public void Clear() { scene=null;cachedScene=null;cachedModels=null; viewport.Children.Clear(); overlapViewport.Children.Clear(); canvas.Scene=null; canvas.InvalidateVisual();overlay.Ready=false;overlay.InvalidateVisual(); }
        public void Fit() { zoom=1;pan=new Vector();Render();ZoomChanged?.Invoke(zoom); }
        private static Point3D P(Point3 p) => new Point3D(p.X,p.Y,p.Z);
        private static Vector3D V(Point3 p) => new Vector3D(p.X,p.Y,p.Z);
        internal static Color ColorFor(int role) => role==0?Color.FromRgb(32,178,170):role==1?Color.FromRgb(255,155,40):role==2?Color.FromRgb(255,40,65):Color.FromRgb(150,160,175);
        private void Render()
        {
            if(scene==null)return;
            viewport.Visibility=mode=="3d"?Visibility.Visible:Visibility.Collapsed;
            overlapViewport.Visibility=viewport.Visibility;
            canvas.Visibility=mode=="3d"?Visibility.Collapsed:Visibility.Visible;
            scene.Axes(mode,corner,useB,out var right,out var up,out var forward);
            scene.Frame(padding,focus,out var min,out var max);
            Point3 center=(min+max)*.5;
            var corners=new List<Point3>();
            for(int i=0;i<8;i++) corners.Add(new Point3((i&1)==0?min.X:max.X,(i&2)==0?min.Y:max.Y,(i&4)==0?min.Z:max.Z));
            double w=corners.Max(p=>(p-center).Dot(right))-corners.Min(p=>(p-center).Dot(right));
            double h=corners.Max(p=>(p-center).Dot(up))-corners.Min(p=>(p-center).Dot(up));
            double aspect=Math.Max(.1,ActualWidth/Math.Max(1,ActualHeight));
            double width=Math.Max(w,h*aspect)*1.05/zoom;
            overlay.Ready=true;overlay.Right=right;overlay.Up=up;overlay.FeetPerPixel=Math.Max(.1,width)/Math.Max(1,ActualWidth);overlay.InvalidateVisual();
            if(mode=="3d")
            {
                if(cachedScene!=scene||cachedContext!=context||cachedFocus!=focus||cachedPadding!=padding)
                {
                var models=new Model3DGroup();
                var overlapModels=new Model3DGroup();
                overlapModels.Children.Add(new AmbientLight(Colors.White));
                models.Children.Add(new AmbientLight(Color.FromRgb(145,145,145)));
                models.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-.4,-.6,-1)));
                var visible=scene.Meshes.Where(m=>context||m.Role!=3).OrderByDescending(m=>m.Role==3).ThenBy(m=>m.Role==2);
                foreach(var mesh in visible)
                {
                    var geometry=new MeshGeometry3D();
                    for(int i=0;i<mesh.Points.Length;i+=3)
                    {
                        var polygon=new List<Point3>{mesh.Points[i],mesh.Points[i+1],mesh.Points[i+2]};
                        foreach(var axis in new[]{new Point3(1,0,0),new Point3(0,1,0),new Point3(0,0,1)})
                        {
                            polygon=InspectionScene.Clip(polygon,max,axis,0);
                            polygon=InspectionScene.Clip(polygon,min,axis*(-1),0);
                        }
                        for(int j=1;j+1<polygon.Count;j++)
                            foreach(var p in new[]{polygon[0],polygon[j],polygon[j+1]})
                            { geometry.TriangleIndices.Add(geometry.Positions.Count);geometry.Positions.Add(P(p)); }
                    }
                    geometry.Freeze();
                    var color=ColorFor(mesh.Role);
                    color.A=(byte)(mesh.Role==3?35:mesh.Role==2?255:150);
                    var brush=new SolidColorBrush(color); brush.Freeze();
                    Material material=new DiffuseMaterial(brush);
                    var model=new GeometryModel3D(geometry,material) { BackMaterial=material }; model.Freeze();
                    // Separate depth buffer keeps the exact overlap visible through both clashing elements.
                    if(mesh.Role==2)overlapModels.Children.Add(model);else models.Children.Add(model);
                }
                cachedModels=models;cachedOverlap=overlapModels;cachedScene=scene;cachedContext=context;cachedFocus=focus;cachedPadding=padding;
                }
                if(viewport.Children.Count==0||!ReferenceEquals((viewport.Children[0] as ModelVisual3D)?.Content,cachedModels))
                { viewport.Children.Clear();viewport.Children.Add(new ModelVisual3D { Content=cachedModels }); }
                overlapViewport.Children.Clear();overlapViewport.Children.Add(new ModelVisual3D { Content=cachedOverlap });
                double distance=Math.Max(10,(max-min).Length*3);
                Point3 shift=right*(-pan.X*width/Math.Max(1,ActualWidth))+up*(pan.Y*width/Math.Max(1,ActualWidth));
                viewport.Camera=new OrthographicCamera(P(center+shift-forward*distance),V(forward),V(up),Math.Max(.1,width)) { NearPlaneDistance=.01, FarPlaneDistance=distance*4 };
                overlapViewport.Camera=viewport.Camera;
            }
            else
            {
                canvas.Scene=scene;canvas.Mode=mode;canvas.Right=right;canvas.Up=up;canvas.Forward=forward;
                canvas.Center=center;
                canvas.Scale=Math.Max(1,ActualWidth)/Math.Max(.1,width);canvas.Pan=pan;canvas.Context=context;canvas.Depth=padding;
                canvas.InvalidateVisual();
            }
        }

        private sealed class OrientationOverlay : FrameworkElement
        {
            public bool Ready;
            public Point3 Right,Up;
            public double FeetPerPixel;
            private static void Text(DrawingContext dc,string text,Point p,Brush brush)
                => dc.DrawText(new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,brush,1),p);
            protected override void OnRender(DrawingContext dc)
            {
                if(!Ready||ActualWidth<90||ActualHeight<85)return;
                var origin=new Point(ActualWidth-37,38);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190,8,12,22)),null,new Rect(ActualWidth-75,4,71,69),5,5);
                var axes=new[]{new Point3(1,0,0),new Point3(0,1,0),new Point3(0,0,1)};
                var colors=new[]{Brushes.OrangeRed,Brushes.LightGreen,Brushes.LightSkyBlue};
                for(int i=0;i<3;i++)
                {
                    var tip=new Point(origin.X+axes[i].Dot(Right)*24,origin.Y-axes[i].Dot(Up)*24);
                    dc.DrawLine(new Pen(colors[i],2),origin,tip);Text(dc,new[]{"X","Y","Z"}[i],new Point(tip.X+2,tip.Y-7),colors[i]);
                }
                double meters=Math.Max(1e-9,FeetPerPixel*80*.3048),power=Math.Pow(10,Math.Floor(Math.Log10(meters)));
                double tick=(meters/power>=5?5:meters/power>=2?2:1)*power;
                double pixels=tick/.3048/FeetPerPixel;
                double y=ActualHeight-17;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200,8,12,22)),null,new Rect(6,y-24,pixels+25,36),3,3);
                var pen=new Pen(Brushes.White,1.2);
                dc.DrawLine(pen,new Point(14,y),new Point(14+pixels,y));
                dc.DrawLine(pen,new Point(14,y-4),new Point(14,y+3));dc.DrawLine(pen,new Point(14+pixels,y-4),new Point(14+pixels,y+3));
                Text(dc,tick<1?(tick*1000).ToString("0.#")+" mm":tick.ToString("0.#")+" m",new Point(14,y-20),Brushes.White);
            }
        }

        private sealed class CutCanvas : FrameworkElement
        {
            public InspectionScene Scene;
            public string Mode;
            public Point3 Right,Up,Forward,Center;
            public double Scale,Depth;
            public Vector Pan;
            public bool Context;
            private Point Project(Point3 p) => new Point(ActualWidth/2+(p-Center).Dot(Right)*Scale+Pan.X,ActualHeight/2-(p-Center).Dot(Up)*Scale+Pan.Y);
            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10,15,26)),null,new Rect(RenderSize));
                if(Scene==null)return;
                foreach(var mesh in Scene.Meshes.OrderByDescending(m=>m.Role==3).ThenBy(m=>m.Role==2))
                {
                    if(mesh.Role==3&&!Context)continue;
                    var color=ColorFor(mesh.Role);
                    var fill=new SolidColorBrush(color) { Opacity=mesh.Role==3?.08:mesh.Role==2?.7:.12 };
                    var pen=new Pen(new SolidColorBrush(color) { Opacity=mesh.Role==3?.15:mesh.Role==2?1:.5 },mesh.Role==2?1.3:.55);
                    for(int i=0;i<mesh.Points.Length;i+=3)
                    {
                        var polygon=new List<Point3>{mesh.Points[i],mesh.Points[i+1],mesh.Points[i+2]};
                        if(Mode=="section")
                        {
                            // Keep only geometry behind the cutting plane and within section depth.
                            polygon=InspectionScene.Clip(polygon,Scene.Clash,Forward*(-1),0);
                            polygon=InspectionScene.Clip(polygon,Scene.Clash,Forward,Depth);
                        }
                        if(polygon.Count<3)continue;
                        var path=new StreamGeometry();
                        using(var g=path.Open()) { g.BeginFigure(Project(polygon[0]),true,true);g.PolyLineTo(polygon.Skip(1).Select(Project).ToArray(),true,false); }
                        dc.DrawGeometry(fill,pen,path);
                        if(Mode=="section")
                        {
                            var crossings=new List<Point3>();
                            for(int k=0;k<3;k++)
                            {
                                var a=mesh.Points[i+k];var b=mesh.Points[i+(k+1)%3];
                                double da=(a-Scene.Clash).Dot(Forward),db=(b-Scene.Clash).Dot(Forward);
                                if((da<0)!=(db<0)) crossings.Add(a+(b-a)*(da/(da-db)));
                            }
                            if(crossings.Count==2)dc.DrawLine(new Pen(new SolidColorBrush(color),2),Project(crossings[0]),Project(crossings[1]));
                        }
                    }
                }
                var center=Project(Scene.Clash);var marker=new Pen(Brushes.White,1.2);
                dc.DrawLine(marker,new Point(center.X-6,center.Y),new Point(center.X+6,center.Y));
                dc.DrawLine(marker,new Point(center.X,center.Y-6),new Point(center.X,center.Y+6));
            }
        }
    }
}
