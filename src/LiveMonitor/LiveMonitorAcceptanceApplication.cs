using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;

namespace ClashResolveAI.LiveMonitor
{
    // Explicit test-only manifest entry point. Normal App startup never invokes
    // this runner. All editable documents are fixtures under its verification dir.
    public sealed class LiveMonitorAcceptanceApplication : IExternalApplication,IExternalEventHandler
    {
        private readonly App _product=new App();
        private ExternalEvent? _event;
        private DispatcherTimer? _timer;
        private string _folder="",_hostPath="",_otherPath="",_pairKey="",_documentKey="";
        private Document? _host;
        private ElementId? _a,_b,_source,_linkType;
        private int _step,_passed;
        private DateTime _next,_deadline;
        private readonly List<string> _results=new List<string>();
        private readonly List<LiveDiagnosticSnapshot> _batches=new List<LiveDiagnosticSnapshot>();
        private sealed class ProductionRequest {public string Model="";public long[] SourceIds=Array.Empty<long>();}
        private ProductionRequest? _production;
        private int _resumes;
        private string[] _productionKeys=Array.Empty<string>();
        private ElementId? _tray;
        private string _trayKey="";
        public Result OnStartup(UIControlledApplication app)
        {
            _folder=Environment.GetEnvironmentVariable("CLASHRESOLVE_LIVE_ACCEPTANCE_DIR")??"";
            string verificationRoot=Environment.GetEnvironmentVariable("CLASHRESOLVE_LIVE_ACCEPTANCE_ROOT")??Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,"..","..","verification");
            if(_folder==""||!Path.GetFullPath(_folder).StartsWith(Path.GetFullPath(verificationRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return Result.Failed;
            Directory.CreateDirectory(_folder);
            string request=Path.Combine(_folder,"production-request.json");
            if(File.Exists(request))_production=JsonConvert.DeserializeObject<ProductionRequest>(File.ReadAllText(request));
            // Initialize the product without activating the older full-system
            // verification harness. Then isolate settings, logs and databases.
            Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",null);
            var result=_product.OnStartup(app);
            Environment.SetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR",_folder);
            if(result!=Result.Succeeded)return result;
            AppSettings.Save(new AppSettings {ShowToast=false,ScanLinkedModels=_production==null,LiveSliceMilliseconds=25,LiveMaximumSliceMilliseconds=100,OutputFolder=_folder});
            LiveDiagnostics.Recorded+=Record;
            File.WriteAllText(Path.Combine(_folder,"started.txt"),Assembly.GetExecutingAssembly().Location);
            _event=ExternalEvent.Create(this);_timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
            _timer.Tick+=(_,__)=>{if(DateTime.UtcNow>=_next)_event.Raise();};_timer.Start();Next(_production==null?0:20);
            return Result.Succeeded;
        }
        private void Record(LiveDiagnosticSnapshot snapshot){if(snapshot.Event=="BatchFinished")_batches.Add(snapshot);}
        private void Next(int step){_step=step;_next=DateTime.UtcNow.AddSeconds(1);_deadline=DateTime.UtcNow.AddSeconds(90);}
        private void Check(bool condition,string name){_results.Add((condition?"PASS ":"FAIL ")+name);File.WriteAllLines(Path.Combine(_folder,"results.txt"),_results);if(!condition)throw new InvalidOperationException(name);_passed++;}
        private static DirectShape Box(Document doc,BuiltInCategory category,XYZ origin)
        {
            var p=new[]{origin,origin+new XYZ(2,0,0),origin+new XYZ(2,2,0),origin+new XYZ(0,2,0)};var loop=new CurveLoop();
            for(int i=0;i<4;i++)loop.Append(Line.CreateBound(p[i],p[(i+1)%4]));
            var shape=DirectShape.CreateElement(doc,new ElementId((long)category));shape.SetShape(new GeometryObject[]{GeometryCreationUtilities.CreateExtrusionGeometry(new[]{loop},XYZ.BasisZ,2)});return shape;
        }
        private static Pipe PipeAt(Document doc,XYZ start,XYZ end)
        {
            var system=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElement();
            var type=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElement();
            var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement();
            var pipe=Pipe.Create(doc,system.Id,type.Id,level.Id,start,end);pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100/304.8);return pipe;
        }
        private void Edit(string name,Action action){using var tx=new Transaction(_host!,"Live acceptance "+name);tx.Start();action();tx.Commit();}
        private static void Post(UIApplication app,PostableCommand command){var id=RevitCommandId.LookupPostableCommandId(command);if(!app.CanPostCommand(id))throw new InvalidOperationException(command+" is not postable");app.PostCommand(id);}
        private void Capture(System.Windows.FrameworkElement element,string name){
            element.UpdateLayout();int width=(int)element.ActualWidth,height=(int)element.ActualHeight;if(width<1||height<1)return;
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(element);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(_folder,name)))png.Save(file);
        }
        private void Select(LiveClashDto row){
            var panel=ClashRadarPanel.Instance;panel.ViewModel.Refresh();
            var grid=(System.Windows.Controls.DataGrid)typeof(ClashRadarPanel).GetField("_rowsPanel",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!;
            var selected=panel.ViewModel.Rows.Single(c=>c.ClashKey==row.ClashKey);grid.SelectedItem=selected;panel.ViewModel.Selected=selected;
        }
        private bool Pair(LiveClashDto row)=>(row.ElementAId==_a?.Value&&row.ElementBId==_b?.Value)||(row.ElementAId==_b?.Value&&row.ElementBId==_a?.Value);
        private bool Ready(LiveMonitorService service)
        {
            if(service.ScanStatus?.Outcome==LiveScanOutcome.ProtectionPaused)throw new InvalidOperationException("Native fixture protection pause: "+service.ScanStatus.Reason);
            if(service.ScanStatus?.Outcome==LiveScanOutcome.Failed)throw new InvalidOperationException(service.ScanStatus.Reason);
            return service.ScanStatus?.QueueDepth==0&&service.ScanStatus.Outcome==LiveScanOutcome.Completed;
        }
        public void Execute(UIApplication app)
        {
            if(DateTime.UtcNow<_next)return;
            try {
                if(DateTime.UtcNow>_deadline)throw new TimeoutException("Live acceptance step "+_step);
                var service=LiveMonitorService.Instance;var radar=RadarDataStore.Instance;
                if(_step==20){
                    Check(app.ActiveUIDocument==null,"Production live verification begins without an open user document");
                    string allowed=Path.GetFullPath(Path.Combine(_folder,"..","..","production"))+Path.DirectorySeparatorChar;
                    if(_production==null||!Path.GetFullPath(_production.Model).StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||_production.SourceIds.Length==0)throw new InvalidOperationException("Production request must use a local verification model copy and explicit source IDs");
                    File.WriteAllText(Path.Combine(_folder,"production-opening.txt"),_production.Model);
                    _host=app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(_production.Model),new OpenOptions {DetachFromCentralOption=DetachFromCentralOption.DetachAndPreserveWorksets},false).Document;
                    DocumentSession.Activate(_host);_documentKey=DocumentSession.Key(_host);
                    var ids=_production.SourceIds.Select(id=>new ElementId(id)).ToList();
                    Check(ids.All(id=>_host.GetElement(id)!=null),"Requested production pipe/accessory sources exist in the detached copy");
                    service.Start(app);app.ActiveUIDocument!.Selection.SetElementIds(ids);service.CheckCurrentSelection();Next(21);return;
                }
                if(_step==21||_step==22){
                    if(service.ScanStatus?.Outcome==LiveScanOutcome.ProtectionPaused){
                        Check(service.ScanStatus.QueueDepth>0&&++_resumes<=3,"Native production hard protection retains pending work for an explicit resume");
                        service.RequestScan(LiveScanAction.Ledger);service.ExecuteQueued(app);return;
                    }
                    if(!Ready(service))return;
                    Check(!ScanCoordinator.Busy&&Dashboard.ClashDashboard.Instance.Clashes.Count==0,"Production Live Monitor remains independent of Full Scan and Dashboard");
                    if(_step==21){
                        Check(radar.GetActive().Any(c=>c.TestType==ClashTestType.HardClash&&c.Verification==LiveVerificationState.Verified),"Cold production local check publishes confirmed hard clashes");
                        var expected=new ClashResolveAI.ClashEngine.ClashEngine(_host!).RunTargetedScan(_production!.SourceIds.Select(id=>new ElementId(id)),ScanMode.HardOnly);
                        Check(new HashSet<string>(expected.Select(c=>c.NormalizedKey)).SetEquals(radar.GetActive().Select(c=>c.LegacyPairKey)),"Production Radar agrees with a same-input host-only targeted reference check");
                        _productionKeys=radar.GetActive().Select(c=>c.ClashKey).OrderBy(k=>k).ToArray();
                        app.ActiveUIDocument.Selection.SetElementIds(Array.Empty<ElementId>());
                        app.ActiveUIDocument.Selection.SetElementIds(_production.SourceIds.Select(id=>new ElementId(id)).ToList());service.CheckCurrentSelection();Next(22);return;
                    }
                    Check(_productionKeys.SequenceEqual(radar.GetActive().Select(c=>c.ClashKey).OrderBy(k=>k)),"Warm production recheck preserves stable clash keys without duplicate rows");
                    Check(_batches.Any(s=>s.DocumentKey==_documentKey&&s.FirstPublishedResultUtc!=null&&s.ScanCompletedUtc!=null),"Production first-result publication and completion are recorded");
                    Finish(app,service);return;
                }
                if(_step==0){
                    Check(app.ActiveUIDocument==null,"Fresh test process has no user document");
                    var link=app.Application.NewProjectDocument(UnitSystem.Metric);
                    using(var tx=new Transaction(link,"Live linked fixture")){tx.Start();Box(link,BuiltInCategory.OST_StructuralColumns,new XYZ(50,50,0));tx.Commit();}
                    string linkPath=Path.Combine(_folder,"live-link.rvt");link.SaveAs(linkPath,new SaveAsOptions());link.Close(false);
                    var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
                    using(var tx=new Transaction(doc,"Live host fixture")){
                        tx.Start();Level.Create(doc,0);Box(doc,BuiltInCategory.OST_StructuralColumns,new XYZ(50,50,0));
                        var result=RevitLinkType.Create(doc,ModelPathUtils.ConvertUserVisiblePathToModelPath(linkPath),new RevitLinkOptions(false));_linkType=result.ElementId;
                        RevitLinkInstance.Create(doc,_linkType);var second=RevitLinkInstance.Create(doc,_linkType);
                        ElementTransformUtils.RotateElement(doc,second.Id,Line.CreateBound(new XYZ(51,51,0),new XYZ(51,51,10)),Math.PI/4);tx.Commit();
                    }
                    _hostPath=Path.Combine(_folder,"live-host.rvt");doc.SaveAs(_hostPath,new SaveAsOptions());doc.Close(false);
                    _host=app.OpenAndActivateDocument(_hostPath).Document;DocumentSession.Activate(_host);_documentKey=DocumentSession.Key(_host);
                    service.Start(app);
                    Edit("place",()=>{_a=PipeAt(_host,new XYZ(-5,0,3),new XYZ(5,0,3)).Id;_b=PipeAt(_host,new XYZ(0,-5,3),new XYZ(0,5,3)).Id;_source=Box(_host,BuiltInCategory.OST_MechanicalEquipment,new XYZ(50.5,50.5,0)).Id;});Next(1);return;
                }
                if(_step==1){
                    if(!Ready(service))return;
                    var pair=radar.GetActive().Single(c=>Pair(c)&&c.TestType==ClashTestType.HardClash);_pairKey=pair.ClashKey;
                    Check(pair.Verification==LiveVerificationState.Verified,"Committed crossing pipes publish a verified hard clash to Radar");
                    Check(radar.GetActive().Count(c=>c.LinkInstanceA!=""||c.LinkInstanceB!="")>=2,"Rotated and repeated loaded links produce distinct verified live pairs");
                    Check(!ScanCoordinator.Busy&&Dashboard.ClashDashboard.Instance.Clashes.Count==0,"Live detection neither starts Full Scan nor populates Dashboard");
                    Check(app.GetDockablePane(ClashRadarPanel.PaneId).IsShown(),"Clash Radar is visible as the registered dockable pane");
                    var expected=new ClashResolveAI.ClashEngine.ClashEngine(_host!).RunTargetedScanWithLinks(new[]{_a!,_b!,_source!});
                    Check(new HashSet<string>(expected.Select(c=>c.NormalizedKey)).SetEquals(radar.GetActive().Select(c=>c.LegacyPairKey)),"Published live pairs agree with a same-input targeted reference check");
                    var scene=Inspection.InspectionGeometry.Build(_host!,LiveClashResolver.Resolve(_host!,pair),20);
                    Check(scene.Meshes.Count>=2,"Radar inspection resolves both native pipe solids");
                    Edit("move clear",()=>ElementTransformUtils.MoveElement(_host!,_b!,new XYZ(0,0,1)));Next(2);return;
                }
                if(_step==2){if(!Ready(service))return;Check(!radar.GetActive().Any(Pair),"Moving a pipe clear automatically resolves its live clash");Post(app,PostableCommand.Undo);Next(3);return;}
                if(_step==3){if(!Ready(service)||!radar.GetActive().Any(Pair))return;Check(radar.GetActive().Single(Pair).ClashKey==_pairKey,"Native Undo restores the same stable clash key without duplication");Post(app,PostableCommand.Redo);Next(4);return;}
                if(_step==4){if(!Ready(service)||radar.GetActive().Any(Pair))return;Check(!radar.GetActive().Any(Pair),"Native Redo resolves the restored pipe clash again");
                    service.RequestMode(MonitorMode.Trigger,_documentKey,service.SessionGeneration(_documentKey));service.ExecuteQueued(app);Edit("trigger edit",()=>ElementTransformUtils.MoveElement(_host!,_b!,new XYZ(0,0,-1)));Next(5);return;}
                if(_step==5){Check(service.Mode==MonitorMode.Trigger&&service.ScanStatus!.QueueDepth>0&&!radar.GetActive().Any(Pair),"Trigger retains committed edits until Check Changes");service.RequestScan(LiveScanAction.Ledger);service.ExecuteQueued(app);Next(6);return;}
                if(_step==6){if(!Ready(service))return;Check(radar.GetActive().Any(Pair),"Trigger Check Changes publishes the pending pipe clash");service.RequestMode(MonitorMode.Off,_documentKey,service.SessionGeneration(_documentKey));service.ExecuteQueued(app);Edit("off edit",()=>ElementTransformUtils.MoveElement(_host!,_b!,new XYZ(0,0,1)));Next(7);return;}
                if(_step==7){Check(service.ScanStatus!.QueueDepth==0&&radar.GetActive().Where(Pair).All(c=>c.Verification!=LiveVerificationState.Verified),"Off does not certify existing rows after untracked edits");service.RequestMode(MonitorMode.Live,_documentKey,service.SessionGeneration(_documentKey));service.ExecuteQueued(app);Next(8);return;}
                if(_step==8){if(!Ready(service))return;Check(!radar.GetActive().Any(Pair),"Returning from Off rechecks the session without requiring another edit");
                    ((RevitLinkType)_host!.GetElement(_linkType!)).Unload(null);Next(9);return;}
                if(_step==9){if(service.ScanStatus?.Outcome!=LiveScanOutcome.Unverified&& !Ready(service))return;
                    Check(radar.GetAll().Where(c=>c.LinkInstanceA!=""||c.LinkInstanceB!="").Any(c=>c.Status!=ClashStatus.Resolved&&c.Verification!=LiveVerificationState.Verified),"Unloading a link marks missing live coverage uncertain instead of resolved");
                    ((RevitLinkType)_host!.GetElement(_linkType!)).Load();Next(10);return;}
                if(_step==10){if(!Ready(service))return;Check(radar.GetActive().Any(c=>(c.LinkInstanceA!=""||c.LinkInstanceB!="")&&c.Verification==LiveVerificationState.Verified),"Reloading a link rechecks monitored sources locally");
                    Edit("resize",()=>((Pipe)_host!.GetElement(_a!)).get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(200/304.8));Next(11);return;}
                if(_step==11){if(!Ready(service))return;Check(!radar.GetActive().Any(Pair),"Resizing a separated native pipe keeps the pair clear");
                    _host!.SaveAs(Path.Combine(_folder,"live-save-as.rvt"),new SaveAsOptions());Check(DocumentSession.Key(_host)==_documentKey,"Save As preserves the open live document identity");
                    Edit("burst",()=>{for(int i=0;i<25;i++)Box(_host,BuiltInCategory.OST_MechanicalEquipment,new XYZ(100+i*4,0,0));});Next(12);return;}
                if(_step==12){if(!Ready(service))return;Check(service.ScanStatus!.QueueDepth==0,"A committed placement burst drains without lost pending IDs");
                    Edit("delete",()=>_host!.Delete(_source!));Next(13);return;}
                if(_step==13){if(!Ready(service))return;Check(!radar.GetActive().Any(c=>c.ElementAId==_source!.Value||c.ElementBId==_source!.Value),"Deleting a monitored source resolves its former host and linked clashes");
                    var other=app.Application.NewProjectDocument(UnitSystem.Metric);_otherPath=Path.Combine(_folder,"live-other.rvt");other.SaveAs(_otherPath,new SaveAsOptions());other.Close(false);app.OpenAndActivateDocument(_otherPath);Next(14);return;}
                if(_step==14){Check(radar.GetAll().Count==0,"Switching projects isolates the new project's Radar rows");
                    EventHandler<DocumentClosingEventArgs> cancel=(_,args)=>{if(args.Document.Equals(_host))args.Cancel();};app.Application.DocumentClosing+=cancel;
                    bool closed;try{closed=_host!.Close(false);}finally{app.Application.DocumentClosing-=cancel;}
                    Check(!closed&&_host!.IsValidObject,"Cancelled document close leaves its live session recoverable");app.OpenAndActivateDocument(_host!.PathName);Next(15);return;}
                if(_step==15){Check(DocumentSession.CurrentKey==_documentKey,"Reactivating the host resumes its original open-document session");
                    service.RequestScan(LiveScanAction.Ledger);service.ExecuteQueued(app);Next(16);return;}
                if(_step==16){if(!Ready(service))return;
                    Check(!ScanCoordinator.Busy&&Dashboard.ClashDashboard.Instance.Clashes.Count==0,"Full Scan and Dashboard remain separate after all live scenarios");
                    var traces=_batches.Where(s=>s.DocumentKey==_documentKey).ToList();
                    Check(traces.Any(s=>s.FirstResultUtc!=null&&s.FirstPublishedResultUtc!=null&&s.ScanCompletedUtc!=null),"Native detection, publication and completion timestamps are correlated");
                    Check(traces.Any(s=>s.PreparationMilliseconds>0&&s.DtoCaptureMilliseconds>0&&s.BooleanMilliseconds>0),"Native live diagnostics expose preparation, DTO and Boolean timings");
                    var settings=AppSettings.Load().ScanSnapshot();settings.ShowToast=true;AppSettings.Save(settings);
                    Edit("video cable tray",()=>{
                        var type=new FilteredElementCollector(_host!).OfClass(typeof(CableTrayType)).FirstElement();
                        var level=new FilteredElementCollector(_host!).OfClass(typeof(Level)).FirstElement();
                        var tray=CableTray.Create(_host!,type.Id,new XYZ(-5,20,3),new XYZ(5,20,3),level.Id);
                        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM).Set(300/304.8);
                        tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM).Set(100/304.8);_tray=tray.Id;
                        Box(_host!,BuiltInCategory.OST_StructuralColumns,new XYZ(-1,19,2));
                    });Next(30);return;
                }
                if(_step==30){
                    if(!Ready(service))return;
                    var row=radar.GetActive().First(c=>(c.ElementAId==_tray!.Value||c.ElementBId==_tray.Value)&&c.TestType==ClashTestType.HardClash);
                    _trayKey=row.ClashKey;
                    Check(row.Verification==LiveVerificationState.Verified,"Video workflow: committed native cable tray publishes a verified clash automatically");
                    var toast=System.Windows.PresentationSource.CurrentSources.Cast<System.Windows.PresentationSource>().Select(s=>s.RootVisual).OfType<Alert.ToastWindow>().FirstOrDefault(w=>w.IsVisible);
                    Check(toast!=null,"Video workflow: the committed tray clash produces a visible native toast");Capture(toast!,"video-toast.png");
                    var vm=ClashRadarPanel.Instance.ViewModel;Select(row);
                    Check(vm.Rows.Any(c=>c.ClashKey==_trayKey)&&vm.InspectCommand.CanExecute(null),"Video workflow: cable tray row appears in Radar with an enabled inspector");
                    vm.InspectCommand.Execute(null);service.ExecuteQueued(app);
                    var inspector=typeof(ClashRadarPanel).GetField("_inspector",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(ClashRadarPanel.Instance);
                    var scene=(Inspection.InspectionScene?)inspector!.GetType().GetField("_scene",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(inspector);
                    Check(scene?.Meshes.Count>=2,"Video workflow: actual selected-row inspector receives both native solids through the gateway");
                    Capture((System.Windows.FrameworkElement)inspector,"video-inspector.png");Capture(ClashRadarPanel.Instance,"video-radar.png");
                    File.WriteAllText(Path.Combine(_folder,"video-inspection.json"),JsonConvert.SerializeObject(new {scene!.IssueId,scene.TriangleCount,MeshRoles=scene.Meshes.Select(m=>m.Role).ToArray(),Bounds=new[]{scene.Min.X,scene.Min.Y,scene.Min.Z,scene.Max.X,scene.Max.Y,scene.Max.Z}},Formatting.Indented));
                    Select(row);Check(vm.Show3DCommand.CanExecute(null),"Video workflow: selected tray enables Show 3D");
                    vm.Show3DCommand.Execute(null);service.ExecuteQueued(app);Next(31);return;
                }
                if(_step==31){
                    if(!(app.ActiveUIDocument!.ActiveView is View3D view)||view.Name!="Clash_Radar_3D"||!Ready(service))return;
                    Check(view.IsSectionBoxActive&&app.ActiveUIDocument.Selection.GetElementIds().Contains(_tray!),"Video workflow: Show 3D activates the focused native view and selects the tray");
                    var vm=ClashRadarPanel.Instance.ViewModel;Select(radar.GetActive().First(c=>c.ClashKey==_trayKey));
                    vm.Show2DCommand.Execute(null);service.ExecuteQueued(app);Next(32);return;
                }
                if(_step==32){
                    if(!(app.ActiveUIDocument!.ActiveView is ViewPlan)||!Ready(service))return;
                    Check(app.ActiveUIDocument.ActiveView.Name.StartsWith("Clash_Radar_2D_")&&app.ActiveUIDocument.Selection.GetElementIds().Contains(_tray!),"Video workflow: Show 2D activates the focused native plan and selects the tray");
                    var vm=ClashRadarPanel.Instance.ViewModel;Select(radar.GetActive().First(c=>c.ClashKey==_trayKey));
                    string csv=Path.Combine(_folder,"video-radar.csv");RadarExporter.WriteCsv(vm.Rows,csv);
                    Check(vm.ExportCommand.CanExecute(null)&&File.ReadAllLines(csv).Length==vm.Rows.Count+1&&File.ReadAllText(csv).Contains(_tray!.Value.ToString()),"Video workflow: Radar CSV writer exports the displayed cable-tray list");
                    vm.IgnoreCommand.Execute(null);vm.Refresh();
                    Check(!radar.GetActive().Any(c=>c.ClashKey==_trayKey)&&radar.GetAll().Single(c=>c.ClashKey==_trayKey).Status==ClashStatus.Ignored,"Video workflow: Ignore removes the selected tray clash from active Radar");
                    vm.CheckChangesCommand.Execute(null);service.ExecuteQueued(app);Next(33);return;
                }
                if(_step==33){
                    if(!Ready(service))return;
                    Check(radar.GetAll().Single(c=>c.ClashKey==_trayKey).Status==ClashStatus.Ignored&&!radar.GetActive().Any(c=>c.ClashKey==_trayKey),"Video workflow: rechecking preserves the local ignored clash");
                    Check(!ScanCoordinator.Busy&&Dashboard.ClashDashboard.Instance.Clashes.Count==0,"Video workflow leaves Full Scan idle and Dashboard empty");
                    Finish(app,service);
                }
            }catch(Exception ex){File.WriteAllText(Path.Combine(_folder,"failed.txt"),"Step "+_step+Environment.NewLine+ex);_timer?.Stop();}
        }
        private void Finish(UIApplication app,LiveMonitorService service){
            File.WriteAllText(Path.Combine(_folder,"batches.json"),JsonConvert.SerializeObject(_batches,Formatting.Indented));
            service.Stop();Diagnostics.FlushLive(TimeSpan.FromSeconds(2));
            File.WriteAllText(Path.Combine(_folder,"complete.txt"),"PASS "+_passed+" native Live Monitor checks");
            if(app.Application.Documents.Cast<Document>().Where(d=>!d.IsLinked).All(d=>d.Equals(_host)||d.PathName.StartsWith(_folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))File.WriteAllText(Path.Combine(_folder,"safe-to-close.txt"),System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            _timer!.Stop();
        }
        public Result OnShutdown(UIControlledApplication app){_timer?.Stop();LiveDiagnostics.Recorded-=Record;_event?.Dispose();return _product.OnShutdown(app);}
        public string GetName()=>"Live Monitor only native acceptance";
    }
}
