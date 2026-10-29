using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;

internal static class Program
{
    [STAThread] private static void Main()
    {
        string folder=Path.GetFullPath(Environment.GetEnvironmentVariable("CLASHRESOLVE_UI_VERIFY_DIR")??"verification/phase4-radar");Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",folder);
        var panel=ClashRadarPanel.Instance;
        var provider=new Autodesk.Revit.UI.DockablePaneProviderData();panel.SetupDockablePane(provider);
        Check(ReferenceEquals(provider.FrameworkElement,panel)&&provider.InitialState!.DockPosition==Autodesk.Revit.UI.DockPosition.Right,"Dockable provider supplies the real Radar page and right docking state");
        var app=new Autodesk.Revit.UI.UIApplication();panel.ShowInApi(app,true);Check(app.Pane.Visible,"Gateway pane action shows the registered pane");panel.ShowInApi(app,false);Check(!app.Pane.Visible,"Gateway pane action hides the registered pane");
        var store=RadarDataStore.Instance;store.Activate("host");store.BeginSession();store.ThisSession=false;
        var engineRow=new ClashResult {ElementA=new Autodesk.Revit.DB.Element {Id=new Autodesk.Revit.DB.ElementId(1)},ElementB=new Autodesk.Revit.DB.Element {Id=new Autodesk.Revit.DB.ElementId(2)},HostDocumentKey="host",SessionGeneration=10,Origin=ResultOrigin.Live,TestType=ClashTestType.HardClash};
        var row=new LiveClashDto(engineRow,new LiveElementIdentity(1,"A","host"),new LiveElementIdentity(2,"B","host"),new LivePoint(0,0,0),"environment");store.AddClashes(new[]{row});
        panel.ViewModel.SetSession("host",10,true,MonitorMode.Trigger);panel.ViewModel.SetStatus(new LiveScanStatus(LiveScanOutcome.Watching,"Waiting for Check Changes",4));
        foreach(int width in new[]{360,800})
        {
            var size=new Size(width,960);panel.Measure(size);panel.Arrange(new Rect(size));panel.UpdateLayout();Drain();
            var controls=Descendants(panel).ToList();
            Check(controls.OfType<Button>().Any(b=>(b.Content as string)=="Check Changes"&&b.IsEnabled),"Trigger Check Changes command binds at width "+width);
            Check(controls.OfType<TextBlock>().Any(t=>t.Text=="4 queued"),"Queue depth binding renders at width "+width);
            Check(controls.OfType<DataGrid>().Single().Items.Count==1,"DTO rows bind at width "+width);
            Check(controls.OfType<ComboBox>().Any(c=>c.Items.Count==3&&c.Items[0] is MonitorMode),"All three monitoring modes are available at width "+width);
            var bitmap=new RenderTargetBitmap(width,960,96,96,PixelFormats.Pbgra32);bitmap.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(folder,"radar-trigger-"+width+".png")))encoder.Save(file);
        }
        panel.ViewModel.SetStatus(new LiveScanStatus(LiveScanOutcome.RecheckingInputs,"Inputs changed; click Check Changes",4));Drain();
        Check(Descendants(panel).OfType<Button>().Any(b=>(b.Content as string)=="Check Changes"&&b.IsEnabled),"Input refresh keeps the real bound Radar check button enabled");
        panel.ViewModel.SetSession("host",11,true,MonitorMode.Off);Drain();
        Check(Descendants(panel).OfType<TextBlock>().Any(t=>t.Text=="0 queued")&&!panel.ViewModel.Show3DCommand.CanExecute(null),"Off clears displayed queue and disables stale result commands");
        LiveDiagnostics.Recorded+=Diagnostics.RecordLive;
        LiveDiagnostics.Session("host",11,MonitorMode.Off);
        Check(Diagnostics.FlushLive(TimeSpan.FromSeconds(5))&&File.Exists(Path.Combine(folder,"diagnostics","live-monitor.jsonl")),"Production asynchronous diagnostic writer flushes a structured log");
        var diagnosticLines=File.ReadAllLines(Path.Combine(folder,"diagnostics","live-monitor.jsonl"));
        Check(diagnosticLines.All(line=>Newtonsoft.Json.Linq.JObject.Parse(line)["DocumentKey"]!=null),"Diagnostic log records are valid JSON with document correlation");
        panel.ViewModel.SetDiagnostics(LiveDiagnostics.Current("host"));
        Check(panel.ViewModel.DiagnosticText.Contains("Maximum API slice")&&panel.ViewModel.ExportDiagnosticsCommand.CanExecute(null),"Radar exposes diagnostic counters and export command");
        LiveDiagnostics.Recorded-=Diagnostics.RecordLive;
        panel.Shutdown();Console.WriteLine("16 Radar WPF/provider/writer checks passed; two previews saved to "+folder);
    }
    private static void Drain()=>Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}
