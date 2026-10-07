// Full Scan controller and composition root. Workspace views receive copied snapshots and injected actions.

using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Dashboard.Persistence;
using ClashResolveAI.ClashEngine;

using WpfColor   = System.Windows.Media.Color;
using WpfBrush   = System.Windows.Media.SolidColorBrush;
using WpfGrid    = System.Windows.Controls.Grid;
using WpfBinding = System.Windows.Data.Binding;

#if !REVIT_STUB_BUILD
namespace ClashResolveAI.Dashboard
{
    // ══════════════════════════════════════════════════════════════════════
    //  CLASH DASHBOARD  — singleton coordinator (unchanged)
    // ══════════════════════════════════════════════════════════════════════

    public class ClashDashboard
    {
        private static ClashDashboard? _instance;
        public  static ClashDashboard   Instance =>
            _instance ?? (_instance = new ClashDashboard());

        private readonly List<ClashResult> _clashes = new List<ClashResult>();
        private readonly List<ClashGroup>  _groups  = new List<ClashGroup>();
        private readonly ClashGroupingEngine _grouper = new ClashGroupingEngine();
        private DashboardWindow?           _window;
        internal DashboardWindow? OpenWindow=>_window;

        public Action<ClashResult>? Show2DAction { get; set; }
        public Action<ClashResult>? Show3DAction { get; set; }
        public Action? RunScanAction { get; set; }
        public Action<ClashObservation>? HistoryNavigationAction {get;set;}
        public Action<ClashResult>? InspectAction { get; set; }

        public IReadOnlyList<ClashResult> Clashes => _clashes.AsReadOnly();
        public IReadOnlyList<ClashGroup>  Groups  => _groups.AsReadOnly();

        public void AddClashes(IEnumerable<ClashResult> clashes)
        {
            var incoming=clashes.Where(c=>c.Origin==ResultOrigin.Full).ToList();
            ClashDatabase.Instance.RestoreLifecycles(incoming);
            var map=_clashes.ToDictionary(c=>c.NormalizedKey);
            foreach(var c in incoming)map[c.NormalizedKey]=c;
            ClashDatabase.Instance.BulkInsertClashes(incoming);
            _clashes.Clear();_clashes.AddRange(map.Values);
            Publish();
        }
        public ResultViewFilter ViewFilter { get; private set; }=new ResultViewFilter { Types=ResultViewFilter.Defaults(AppSettings.Load().FullScanMode) };
        private ScanMode? _viewMode;
        public List<ClashResult> GetVisibleClashes()
        {
            if(_window!=null&&_window.IsVisible){var ids=_window.VisibleObservations.Select(c=>c.ClashId).ToHashSet();return _clashes.Where(c=>ids.Contains(c.ClashId)).ToList();}
            return _clashes.Where(ViewFilter.Matches).OrderBy(c=>(int)c.Severity).ThenByDescending(c=>c.OverlapVolumeMM3).ToList();
        }
        public void RestoreSession(IEnumerable<ClashResult> rows,ResultViewFilter filter)
        { _clashes.Clear();_clashes.AddRange(rows.Where(c=>c.Origin==ResultOrigin.Full));ViewFilter=filter.Copy();_viewMode=null;Publish(); }
        public int MergeFullScan(List<ClashResult> found,ScanMode mode,ScanScope scope)
        {
            if(!string.IsNullOrEmpty(scope.PublishedScanId))return scope.PublishedRetainedClearance;
            if(_viewMode!=mode){ViewFilter.Types=ResultViewFilter.Defaults(mode);_viewMode=mode;}
            return Merge(found.Where(c=>c.Origin==ResultOrigin.Full).ToList(),mode,scope);
        }
        // Compatibility fixtures use the same staging policy; production always commits saved versions.
        private int Merge(List<ClashResult> found,ScanMode mode,ScanScope scope)
        {
            var batch=Application.FullScanSnapshotService.Stage(_clashes,found,new ScanStatistics {Mode=mode,Scope=scope},ScanCoordinator.Revision);
            ClashDatabase.Instance.BulkInsertClashes(batch.Rows);
            _clashes.Clear();_clashes.AddRange(batch.Rows);Publish();return batch.RetainedClearance;
        }
        internal void CompleteFullScan(List<ClashResult> found,ScanStatistics stats)
        {
            var batch=new Application.FullScanDashboardService(ClashDatabase.Instance).Complete(_clashes,found,stats,ScanCoordinator.StartRevision);
            if(_viewMode!=stats.Mode){ViewFilter.Types=ResultViewFilter.Defaults(stats.Mode);_viewMode=stats.Mode;}
            _clashes.Clear();_clashes.AddRange(batch.Rows);
            stats.Scope.PublishedScanId=stats.ScanId;stats.Scope.PublishedRetainedClearance=batch.RetainedClearance;
            Publish();
        }
        public void SetResultTypes(ResultTypes types)
        {
            if(ViewFilter.Types==types)return;
            ViewFilter.Types=types;ViewChanged();RefreshWindow();
        }
        public void ViewChanged()
        {
            Commands.Session.Clashes=GetVisibleClashes();
            Commands.Session.Groups=_grouper.GroupClashes(Commands.Session.Clashes);
        }
        public int PurgeNonHard()
        {
            var removed=_clashes.Where(c=>c.TestType!=ClashTestType.HardClash).ToList();
            ClashDatabase.Instance.DeleteClashes(removed.Select(c=>c.ClashId));
            _clashes.RemoveAll(c=>c.TestType!=ClashTestType.HardClash);Publish();return removed.Count;
        }
        public void ConfirmPurgeNonHard()
        {
            int count=_clashes.Count(c=>c.TestType!=ClashTestType.HardClash);
            if(count==0)return;
            if(MessageBox.Show($"Purge {count} non-hard results from this document's current list? This includes clearance and all unverified rows, including possible hard. Saved scan history and lifecycle events are preserved. Future scans may find them again.",
                "Purge non-hard results",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes)PurgeNonHard();
        }
        public void UpdateClashStatus(string clashId,ClashStatus status,string author="",string comment="")
        {
            var c=_clashes.FirstOrDefault(x=>x.ClashId==clashId);
            ClashDatabase.Instance.UpdateClashStatus(clashId,status,author,comment);
            if(c!=null)c.Status=status;
            Publish();
        }
        public void UpdateStatuses(IEnumerable<ClashResult> items,ClashStatus status)
        {
            var list=items.ToList();ClashDatabase.Instance.UpdateClashStatuses(list.Select(c=>c.ClashId),status,"User","Bulk dashboard action");
            foreach(var c in list)c.Status=status;Publish();
        }
        private void Publish()
        {
            RebuildGroups();
            ViewChanged();
            RefreshWindow();
        }

        private void RebuildGroups()
        {
            _groups.Clear();
            _groups.AddRange(_grouper.GroupClashes(_clashes));
        }

        private bool _refreshQueued;
        internal void NotifyGeometryChanged(){if(_window?.IsVisible==true)RefreshWindow();}
        private void RefreshWindow()
        {
            if(_refreshQueued)return;_refreshQueued=true;
            var dispatcher = App.UIDispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => {_refreshQueued=false;try{_window?.Refresh(_clashes, _groups);}catch(Exception ex){Diagnostics.Log("Dashboard refresh",ex);MessageBox.Show("Dashboard refresh failed: "+ex.Message,"Dashboard");}}));
        }

        internal Action<ClashResult,Action<ClashResolveAI.Inspection.InspectionScene?,string>>? PreviewAction;
        internal Action<ClashResult,ClashResolveAI.Inspection.InspectionScene,ClashResolveAI.Inspection.InspectorPreferences>? PinPreviewAction;
        public void ShowWindow()
        {
            if (_window == null || !_window.IsVisible)
            {
                _window = CreateWorkspace();
                _window.Show();
                ViewChanged();
            }
            else
            {
                _window.Activate();
            }
        }

        public void ShowOverview()
        { ShowWindow();_window?.Refresh(true); }
        internal void CloseWindow(){_window?.Close();_window=null;}
        private DashboardWindow CreateWorkspace()
        {
            var database=ClashDatabase.Instance;
            var source=new DashboardDataSource(database,()=>DocumentSession.CurrentKey,
                id=>_clashes.FirstOrDefault(c=>c.ClashId==id) is ClashResult row&&ScanCoordinator.IsStale(row),()=>ScanCoordinator.FullResultsStale);
            var workspace=new Application.DashboardWorkspace(source,new Application.LifecycleCommandService(database),new Application.GroupCoordinationService(database));workspace.Refresh();
            if(workspace.Snapshot.Selected==null)workspace.ResultTypes=ViewFilter.Types;
            return new DashboardWindow(workspace,new DashboardActions {
                RunScan=()=>RunScanAction?.Invoke(),
                Preview=(id,done)=>{var row=_clashes.FirstOrDefault(c=>c.ClashId==id)??throw new InvalidOperationException("Issue is no longer current.");if(PreviewAction==null)done(null,"Open the dashboard command to initialize Revit preview.");else PreviewAction(row,done);},
                PinPreview=(id,scene,preferences)=>{var row=_clashes.FirstOrDefault(c=>c.ClashId==id)??throw new InvalidOperationException("Issue is no longer current.");PinPreviewAction?.Invoke(row,scene,preferences);},
                ExportPackage=request=>{
                    if(request.Current&&(ScanCoordinator.Busy||ScanCoordinator.FullResultsStale||request.Rows.Any(o=>!_clashes.Any(c=>c.ClashId==o.ClashId&&!ScanCoordinator.IsStale(c)))))throw new InvalidOperationException("Run Full Scan before exporting current issues.");
                    using var dialog=new System.Windows.Forms.FolderBrowserDialog {Description=request.Scope+" / "+request.Format};if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                    string folder=dialog.SelectedPath;
                    System.Threading.Tasks.Task.Run(()=>Application.DashboardExportService.Write(request,folder)).ContinueWith(task=>App.UIDispatcher?.BeginInvoke(new Action(()=>{if(task.IsFaulted)MessageBox.Show("Export failed: "+task.Exception!.GetBaseException().Message,"Dashboard export");else MessageBox.Show("Export saved: "+task.Result,"Dashboard export");})));
                },
                FilterChanged=()=>ViewChanged(),
                Purge=()=>ConfirmPurgeNonHard(),
                HistoricalNavigate=row=>HistoryNavigationAction?.Invoke(row),
                ExportGroup=group=>{
                    if(ScanCoordinator.Busy||ScanCoordinator.FullResultsStale||group.CurrentIds.Any(id=>_clashes.Any(c=>c.ClashId==id&&ScanCoordinator.IsStale(c))))throw new InvalidOperationException("Run Full Scan before exporting this group.");
                    using var dialog=new System.Windows.Forms.FolderBrowserDialog {Description="Export selected coordination group BCF"};if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                    var export=Application.DashboardExportProjection.GroupTopic(group);
                    MessageBox.Show("Export saved: "+new Services.BcfExportService().ExportGroups(new List<ClashGroup>{export},Commands.Session.ProjectName,dialog.SelectedPath),"Group export");
                },
                Inspect=id=>{
                    var row=_clashes.FirstOrDefault(c=>c.ClashId==id)??throw new InvalidOperationException("Issue is no longer current.");
                    if(ScanCoordinator.IsStale(row))throw new InvalidOperationException("Run Full Scan before inspecting this issue.");
                    InspectAction?.Invoke(row);
                },
                Mutated=()=>{
                    var stamps=_clashes.ToDictionary(c=>c.ClashId,c=>c.GeometryRevision);
                    var rows=database.LoadCurrentClashes();foreach(var row in rows)if(stamps.TryGetValue(row.ClashId,out var stamp))row.GeometryRevision=stamp;
                    _clashes.Clear();_clashes.AddRange(rows);Publish();
                },
                Navigate=(id,three)=>{
                    var row=_clashes.FirstOrDefault(c=>c.ClashId==id)??throw new InvalidOperationException("Issue is no longer current.");
                    if(ScanCoordinator.Busy||ScanCoordinator.IsStale(row))throw new InvalidOperationException("Run Full Scan to refresh this issue before navigation.");
                    if(three)Show3DAction?.Invoke(row);else Show2DAction?.Invoke(row);
                },
                Export=(observations,bcf)=>{
                    if(ScanCoordinator.Busy||ScanCoordinator.FullResultsStale||observations.Any(o=>!_clashes.Any(c=>c.ClashId==o.ClashId&&!ScanCoordinator.IsStale(c))))throw new InvalidOperationException("Run Full Scan to refresh the filtered issues before export.");
                    if(observations.Count==0)throw new InvalidOperationException("No issues match the filters.");
                    using var dialog=new System.Windows.Forms.FolderBrowserDialog {Description="Select export folder for the filtered current issues"};
                    if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                    var rows=observations.Select(ClashObservationAdapter.Restore).ToList();
                    string file=bcf?new Services.BcfExportService().ExportGroups(Application.DashboardExportProjection.IssueTopics(observations),Commands.Session.ProjectName,dialog.SelectedPath):Reports.ExcelReportGenerator.Generate(rows,Commands.Session.ProjectName,dialog.SelectedPath);
                    MessageBox.Show("Export saved: "+file,"Dashboard export");
                }
            });
        }

        public void Clear()
        {
            _clashes.Clear();
            _groups.Clear();
            RefreshWindow();
        }

        public DashboardStats GetStats()
        {
            var db = ClashDatabase.Instance;
            var metric=new DashboardDataSource(db,()=>DocumentSession.CurrentKey,id=>_clashes.Any(c=>c.ClashId==id&&ScanCoordinator.IsStale(c))).Read("","").Metrics;
            return new DashboardStats
            {
                TotalClashes            = _clashes.Count,
                Critical                = metric.Critical,
                Hard                    = _clashes.Count(c => c.Severity == ClashSeverity.Hard),
                Soft                    = _clashes.Count(c => c.Severity == ClashSeverity.Soft),
                ClearanceOnly           = _clashes.Count(c => c.Severity == ClashSeverity.Clearance),
                Resolved                = _clashes.Count(c => c.Status   == ClashStatus.Resolved),
                Open                    = metric.Open,
                GroupCount              = _groups.Count,
                CoordinationHealthScore = metric.Health??0,
                LastScan                = metric.LastScanUtc?.ToLocalTime()??DateTime.MinValue,
                RecentClashes           = _clashes.Take(20).ToList(),
                ActiveGroups            = _groups.Take(10).ToList(),
                Trends                  = db.GetWeeklyTrends()
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DASHBOARD WINDOW  — redesigned v8.0
    // ══════════════════════════════════════════════════════════════════════

}
#endif
