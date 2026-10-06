using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Dashboard;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClashResolveAI.Core
{
    internal static partial class IntegrationVerification
    {
        private static void VerifyLifecycleScope(UIApplication app)
        {
            var doc=app.Application.NewProjectDocument(UnitSystem.Metric);
            try {
                Level lower;Element moving;
                using(var tx=new Transaction(doc,"Scoped lifecycle fixture")){
                    tx.Start();lower=Level.Create(doc,0);Level.Create(doc,10);
                    moving=Box(doc,BuiltInCategory.OST_MechanicalEquipment,XYZ.Zero);
                    Box(doc,BuiltInCategory.OST_StructuralColumns,new XYZ(.5,.5,0));
                    Box(doc,BuiltInCategory.OST_MechanicalEquipment,new XYZ(20,0,12));
                    Box(doc,BuiltInCategory.OST_StructuralColumns,new XYZ(20.5,.5,12));tx.Commit();
                }
                DocumentSession.Activate(doc);
                var engine=new ClashResolveAI.ClashEngine.ClashEngine(doc);
                var full=engine.RunFullScan(false,mode:ScanMode.HardAndClearance);
                Check(full.Count==2,"Two-level fixture has exactly two independent hard pairs");
                var dashboard=ClashDashboard.Instance;dashboard.RestoreSession(full,new ResultViewFilter {Types=ResultTypes.All});
                var low=full.Single(c=>c.InvolvesHost(new HashSet<long>{moving.Id.Value}));var high=full.Single(c=>c!=low);var status=high.Status;
                using(var tx=new Transaction(doc,"Clear lower-floor pair")){tx.Start();ElementTransformUtils.MoveElement(doc,moving.Id,new XYZ(5,0,0));tx.Commit();}
                var found=engine.RunFullScan(false,levelId:lower.Id.Value.ToString(),mode:ScanMode.HardOnly);
                dashboard.MergeFullScan(found,ScanMode.HardOnly,engine.LastStatistics.Scope);
                Check(dashboard.Clashes.Count==2&&low.Status==ClashStatus.Resolved&&high.Status==status,
                    "Real level-scoped hard-only scan resolves only the clear lower-floor row and retains upper-floor status");
                Check(!engine.LastStatistics.Scope.Contains(high)&&engine.LastStatistics.Scope.Contains(low),"Engine coverage matches the selected level's actual sources");
            } finally {doc.Close(false);}
        }
        private static void VerifyLifecycleStateAndUi(Document doc)
        {
            var dashboard=ClashDashboard.Instance;var saved=dashboard.Clashes.ToList();var savedFilter=dashboard.ViewFilter.Copy();
            string key=DocumentSession.Key(doc);
            ClashResult Row(int id,ClashTestType type,UnverifiedReason reason=UnverifiedReason.SurfaceClearance)=>new ClashResult {
                ClashId="PHASE2-"+id,HostDocumentKey=key,ElementAId=90000000+id,ElementBId=91000000+id,TestType=type,UnverifiedReason=reason,
                Status=ClashStatus.Active,CategoryNameA="Pipe",CategoryNameB="Equipment",FamilyTypeA="Lifecycle fixture",FamilyTypeB="Fixture",LevelName="L1",Severity=ClashSeverity.Hard
            };
            var clearance=Enumerable.Range(1,739).Select(i=>Row(i,ClashTestType.ClearanceClash)).ToList();
            var hard=Row(800,ClashTestType.HardClash);var possible=Row(801,ClashTestType.Unverified,UnverifiedReason.SolidTest);possible.LevelName="L2";
            var surface=Row(802,ClashTestType.Unverified);var missing=Row(803,ClashTestType.Unverified,UnverifiedReason.MissingGeometry);
            var seeded=clearance.Concat(new[]{hard,possible,surface,missing}).ToList();
            var scope=new ScanScope(key);foreach(var c in seeded)scope.Note(c.ElementAId,"","");
            DashboardWindow? window=null;ClashRadarPanel? radar=null;
            try {
                dashboard.RestoreSession(seeded,new ResultViewFilter {Types=ResultTypes.All});ClashDatabase.Instance.BulkInsertClashes(seeded);
                int retained=dashboard.MergeFullScan(new List<ClashResult>(),ScanMode.HardOnly,scope);
                Check(retained==739&&clearance.All(c=>c.Status==ClashStatus.Active)&&dashboard.Clashes.Count==743,"Dashboard hard-only merge retains all 739 seeded clearance rows unchanged");
                Check(hard.Status==ClashStatus.Resolved&&possible.Status==ClashStatus.Resolved&&surface.Status==ClashStatus.Active&&missing.Status==ClashStatus.Active,"Dashboard resolves only evaluated types and preserves other unverified rows");
                Check(RadarDataStore.Instance.ActiveCount==0,"Full Scan publication leaves Radar empty");
                string db=Path.Combine(_folder,"databases",key+".clash.db");
                long Count(string sql){using var conn=new System.Data.SQLite.SQLiteConnection("Data Source="+db+";Version=3;");conn.Open();using var command=conn.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
                Check(Count("SELECT COUNT(*) FROM Clashes WHERE ClashId LIKE 'PHASE2-%' AND TestType='ClearanceClash' AND Status='Active'")==739,"SQLite clearance statuses remain unchanged after hard-only reconciliation");
                var recurring=Row(800,ClashTestType.HardClash);var recurringPossible=Row(801,ClashTestType.Unverified,UnverifiedReason.SolidTest);recurringPossible.LevelName="L2";
                dashboard.MergeFullScan(new List<ClashResult>{recurring,recurringPossible},ScanMode.HardOnly,scope);
                Check(dashboard.GetVisibleClashes().Count==2&&Commands.Session.Clashes!.Count==2,"Hard-only default dashboard/export view is hard plus possible hard");
                dashboard.ShowWindow();window=dashboard.OpenWindow!;window.UpdateLayout();
                var checks=Descendants<CheckBox>(window).Where(c=>new[]{"Hard","Possible hard","Clearance","Unverified"}.Contains(c.Content as string)).ToList();
                Check(checks.Count==4&&checks.Single(c=>(string)c.Content=="Hard").IsChecked==true&&checks.Single(c=>(string)c.Content=="Clearance").IsChecked==false,"Dashboard displays four result-type controls with hard-only defaults");
                checks.Single(c=>(string)c.Content=="Clearance").IsChecked=true;
                Check(dashboard.GetVisibleClashes().Count==741&&Descendants<DataGrid>(window).Single().Items.Count==741,"Dashboard checkbox applies the same predicate as the export snapshot");
                CaptureLifecycleUi(window,"phase2-dashboard.png");
                var live=Row(800,ClashTestType.HardClash);live.Origin=ResultOrigin.Live;live.LiveSessionId=RadarDataStore.Instance.SessionId;var liveDto=new LiveClashDto(live,new LiveElementIdentity(live.ElementAId,"fixture-A",live.HostDocumentKey),new LiveElementIdentity(live.ElementBId,"fixture-B",live.HostDocumentKey),new LivePoint(0,0,0),"fixture");RadarDataStore.Instance.AddClashes(new[]{liveDto});
                radar=ClashRadarPanel.Instance;radar.Width=400;radar.Show();DrainUi(radar);radar.UpdateLayout();
                var radarChecks=Descendants<CheckBox>(radar).Where(c=>new[]{"Hard","Possible hard","Clearance","Unverified"}.Contains(c.Content as string)).ToList();
                Check(radarChecks.Count==4&&radarChecks.All(c=>c.TransformToAncestor(radar).Transform(new System.Windows.Point()).Y+c.ActualHeight<=radar.ActualHeight),"Narrow radar lays out all four result filters without clipping");
                radarChecks.Single(c=>(string)c.Content=="Unverified").IsChecked=true;DrainUi(radar);
                Check(dashboard.ViewFilter.Types==(ResultTypes.Hard|ResultTypes.PossibleHard|ResultTypes.Clearance)&&checks.Single(c=>(string)c.Content=="Unverified").IsChecked==false,"Radar filters leave Dashboard filters unchanged");
                RadarDataStore.Instance.IgnoreClash(liveDto);Check(recurring.Status!=ClashStatus.Ignored,"Radar Ignore does not change the same Full Scan pair");
                CaptureLifecycleUi(radar,"phase2-radar.png");
                dashboard.SetResultTypes(ResultTypes.Hard|ResultTypes.PossibleHard);dashboard.ViewFilter.Level="L1";dashboard.ViewChanged();
                var export=dashboard.GetVisibleClashes().Select(c=>c.ExportCopy()).ToList();
                Check(export.Count==1&&export[0].ClashId==recurring.ClashId&&Commands.Session.Clashes!.Select(c=>c.ClashId).SequenceEqual(export.Select(c=>c.ClashId)),"RFI/export snapshot honors dashboard level and result-type filters together");
                string excel=Reports.ExcelReportGenerator.Generate(export,"Phase2Filtered",_folder);
                using(var book=new ClosedXML.Excel.XLWorkbook(excel))Check(book.Worksheet("Clash Detail").LastRowUsed()!.RowNumber()==2&&book.Worksheet("Clash Detail").Cell(2,1).GetString()==recurring.ClashId,"Filtered Excel report contains only the visible clash");
                string word=Reports.WordReportGenerator.Generate(export,"Phase2Filtered",_folder);
                using(var document=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(word,false)){
                    string text=document.MainDocumentPart!.Document.InnerText;
                    Check(text.Contains(recurring.ClashId)&&!text.Contains(recurringPossible.ClashId),"Filtered Word report contains the visible clash and excludes the hidden possible-hard row");
                }
                var exporter=new Services.BcfExportService();string bcf=exporter.ExportClashes(export,"Phase2Filtered",_folder);
                Check(exporter.ImportBcf(bcf).Count==1,"Filtered BCF contains exactly the visible issue");
                Check(Descendants<Button>(window).Any(b=>(b.Content as string)=="Purge non-hard results")&&Descendants<Button>(radar).Any(b=>(b.Content as string)=="Purge non-hard results"),"Dashboard and radar expose the explicit purge action");
                int purged=dashboard.PurgeNonHard();
                Check(purged==742&&dashboard.Clashes.Count==1&&dashboard.Clashes[0].TestType==ClashTestType.HardClash&&RadarDataStore.Instance.ActiveCount==0,"Dashboard purge leaves independent ignored Radar result unchanged");
                Check(Count("SELECT COUNT(*) FROM Clashes WHERE ClashId LIKE 'PHASE2-%' AND TestType<>'HardClash'")==0,"Purged non-hard rows are removed from SQLite");
            } finally {
                radar?.Close();window?.Close();RadarDataStore.Instance.Clear();ClashDatabase.Instance.DeleteClashes(seeded.Select(c=>c.ClashId));dashboard.RestoreSession(saved,savedFilter);
            }
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {
            if(parent is T match)yield return match;
            foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())foreach(var item in Descendants<T>(child))yield return item;
        }
        private static void DrainUi(FrameworkElement element)=>element.Dispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));
        private static void CaptureLifecycleUi(FrameworkElement element,string name)
        {
            element.UpdateLayout();var image=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(element);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(_folder,name));encoder.Save(file);
        }
    }
}

