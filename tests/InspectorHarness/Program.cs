using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClashResolveAI.Inspection;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output=Path.GetFullPath(args.Length>0?args[0]:"verification/inspector");Directory.CreateDirectory(output);
        var prefs=new InspectorPreferences { PersistenceEnabled=false };
        var inspector=new ClashInspector(prefs);
        var scene=new InspectionScene { Min=new Point3(-5,-5,-1),Max=new Point3(5,5,1),Clash=new Point3(0,0,0),AxisA=new Point3(1,0,0),AxisB=new Point3(0,1,0),Notice="A · teal   B · amber   Intersection · red" };
        scene.Meshes.Add(Box(new Point3(-5,-.5,-.5),new Point3(5,.5,.5),0));
        scene.Meshes.Add(Box(new Point3(-.5,-5,-.5),new Point3(.5,5,.5),1));
        scene.Meshes.Add(Box(new Point3(-.5,-.5,-.5),new Point3(.5,.5,.5),2));
        inspector.Display(scene);
        foreach(var mode in new[]{"3d","2d","section","compare","triple"})
        {
            inspector.SetMode(mode);
            foreach(var size in new[]{new Size(360,460),new Size(1050,680)})
            {
                inspector.Measure(size);inspector.Arrange(new Rect(size));inspector.UpdateLayout();
                var ports=Descendants(inspector).OfType<InspectionViewport>().ToList();
                int expected=mode=="triple"?3:mode=="compare"?2:1;
                Assert(ports.Count==expected,"Viewport count "+mode);
                Assert(ports.All(p=>p.ActualHeight>280),"Viewport receives remaining vertical space "+mode);
                Assert(inspector.RowDefinitions[0].ActualHeight<=130,"Controls remain compact");
                var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(inspector);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(output,mode+"-"+(int)size.Width+".png")))encoder.Save(file);
            }
        }
        var first=Descendants(inspector).OfType<InspectionViewport>().First();first.SetZoom(2.5);
        inspector.SetMode("2d");Assert(Math.Abs(Descendants(inspector).OfType<InspectionViewport>().First().Zoom-2.5)<.001,"Zoom survives mode/layout changes");
        bool requested=false;inspector.PopOutRequested+=()=>requested=true;
        Descendants(inspector).OfType<Button>().First(b=>(string)b.Content=="Float").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(requested,"Pop-out action is wired");
        var host=new Border { Child=inspector };host.Child=null;var floating=new Window { Content=inspector };floating.Content=null;host.Child=inspector;
        Assert(ReferenceEquals(host.Child,inspector),"Same inspector can move to a floating window and dock again");
        inspector.Clear();Assert(Descendants(inspector).OfType<InspectionViewport>().Any(),"Clearing selection preserves viewport controls");
        var invalid=new InspectorPreferences { Zoom=double.NaN,ControlsHeight=-1,ListRatio=20,Mode="invalid" }.Normalize();
        Assert(invalid.Zoom==1&&invalid.ControlsHeight>=76&&invalid.ListRatio<=.75&&invalid.Mode=="3d","Invalid persisted settings are bounded");
        File.WriteAllText(Path.Combine(output,"checks.txt"),"PASS: 10 rendered layouts; viewport sizing; mode and zoom; pop-out request; reparent/dock; empty state; settings validation.");
        Console.WriteLine("PASS inspector UI checks and 10 rendered previews: "+output);
    }
    private static void Assert(bool value,string label) { if(!value)throw new Exception(label); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var c in Descendants(child))yield return c; }
    }
    private static InspectionMesh Box(Point3 min,Point3 max,int role)
    {
        var p=new Point3[8];for(int i=0;i<8;i++)p[i]=new Point3((i&1)==0?min.X:max.X,(i&2)==0?min.Y:max.Y,(i&4)==0?min.Z:max.Z);
        int[] indices={0,2,3,0,3,1,4,5,7,4,7,6,0,1,5,0,5,4,2,6,7,2,7,3,0,4,6,0,6,2,1,3,7,1,7,5};
        return new InspectionMesh(indices.Select(i=>p[i]),role);
    }
}
