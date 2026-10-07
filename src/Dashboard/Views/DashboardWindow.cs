using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Core;

namespace ClashResolveAI.Dashboard
{
    public sealed class DashboardActions
    {
        public Action<DashboardExportRequest> ExportPackage {get;set;}=request=>{};
        public Action RunScan { get; set; }=()=>{};
        public Action<string,bool> Navigate { get; set; }=(id,three)=>{};
        public Action<IReadOnlyList<ClashObservation>,bool> Export { get; set; }=(rows,bcf)=>{};
        public Action Mutated { get; set; }=()=>{};
        public Action FilterChanged { get; set; }=()=>{};
        public Action<string,Action<ClashResolveAI.Inspection.InspectionScene?,string>> Preview {get;set;}=(id,done)=>done(null,"Preview unavailable.");
        public Action<string,ClashResolveAI.Inspection.InspectionScene,ClashResolveAI.Inspection.InspectorPreferences> PinPreview {get;set;}=(id,scene,preferences)=>{};
        public Action<string> Inspect { get; set; }=id=>{};
        public Action Purge { get; set; }=()=>{};
        public Action<ClashObservation> HistoricalNavigate {get;set;}=row=>{};
        public Action<GroupView> ExportGroup {get;set;}=group=>{};
    }
    public partial class DashboardWindow : Window
    {
        private readonly DashboardWorkspace _vm;
        private readonly DashboardActions _actions;
        private readonly ComboBox _exportScope=new ComboBox {ItemsSource=new[]{"Filtered issues (shown state)","Selected group (shown state)"},SelectedIndex=0,Width=210,Margin=new Thickness(4)};
        private readonly ComboBox _scan=new ComboBox {Width=210,Margin=new Thickness(6)},_compare=new ComboBox {Width=210,Margin=new Thickness(6)};
        private readonly TextBlock _context=new TextBlock {MaxHeight=110,TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12,4,12,10)};
        private readonly TabControl _tabs=new TabControl {Margin=new Thickness(12)};
        private readonly StackPanel _overview=new StackPanel();
        private TextBlock _queueTitle=new TextBlock();
        private readonly DataGrid _grid=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Extended,EnableRowVirtualization=true,EnableColumnVirtualization=true,CanUserAddRows=false,RowHeight=32};
        private readonly TabControl _details=new TabControl();
        private readonly ComboBox _next=new ComboBox {MinWidth=120};
        private readonly TextBox _reason=new TextBox {MinWidth=180,ToolTip="Reason for workflow change"},_owner=new TextBox(),_comment=new TextBox {AcceptsReturn=true,Height=65};
        private readonly DatePicker _due=new DatePicker();
        private readonly Button _apply=new Button(),_save=new Button(),_two=new Button(),_three=new Button(),_bcf=new Button(),_excel=new Button();
        private readonly Button _inspect=new Button {Content="Open geometry inspector"};
        private readonly Button _purge=new Button {Content="Purge non-hard results",Margin=new Thickness(4)};
        private readonly Image _storedSnapshot=new Image {MaxHeight=240,Stretch=Stretch.Uniform,Margin=new Thickness(8)};
        private readonly TextBlock _summary=Text(),_elements=Text(),_evidence=Text(),_history=Text(),_comments=Text(),_viewpoint=Text();
        private readonly Dictionary<string,ComboBox> _filters=new Dictionary<string,ComboBox>();
        private readonly Dictionary<ResultTypes,CheckBox> _types=new Dictionary<ResultTypes,CheckBox>();
        private readonly TextBox _search=new TextBox {Width=260,Margin=new Thickness(4)};
        private bool _refreshing;
        public IReadOnlyList<ClashObservation> VisibleObservations=>_vm.Snapshot.IsCurrent?_vm.Visible():Array.Empty<ClashObservation>();
        private static TextBlock Text()=>new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12),Foreground=Brushes.White};
        public DashboardWindow(DashboardWorkspace workspace,DashboardActions actions)
        {
            _vm=workspace;_actions=actions;Title="ClashResolve AI · Full Scan coordination";Width=1480;Height=900;MinWidth=1120;MinHeight=700;
            Background=new SolidColorBrush(Color.FromRgb(19,27,40));Foreground=Brushes.White;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            Resources[typeof(TextBlock)]=new Style(typeof(TextBlock)){Setters={new Setter(TextBlock.ForegroundProperty,Brushes.White)}};
            Resources[typeof(Button)]=new Style(typeof(Button)){Setters={new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(37,66,83))),new Setter(Control.ForegroundProperty,Brushes.White),new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(59,111,130))),new Setter(Control.PaddingProperty,new Thickness(10,6,10,6))}};
            var comboText=new FrameworkElementFactory(typeof(TextBlock));comboText.SetBinding(TextBlock.TextProperty,new Binding());comboText.SetValue(TextBlock.ForegroundProperty,Brushes.Black);
            Resources[typeof(ComboBox)]=new Style(typeof(ComboBox)){Setters={new Setter(ItemsControl.ItemTemplateProperty,new DataTemplate {VisualTree=comboText}),new Setter(Control.ForegroundProperty,Brushes.Black)}};
            _grid.Foreground=Brushes.Black;
            Resources[typeof(TabControl)]=new Style(typeof(TabControl)){Setters={new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(28,39,55))),new Setter(Control.ForegroundProperty,Brushes.White)}};
            Resources[typeof(TabItem)]=new Style(typeof(TabItem)){Setters={new Setter(Control.ForegroundProperty,Brushes.Black),new Setter(Control.PaddingProperty,new Thickness(16,9,16,9))}};
            var root=new DockPanel();Content=root;
            var toolbar=new WrapPanel {Margin=new Thickness(12,12,12,4)};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
            toolbar.Children.Add(new TextBlock {Text="FULL SCAN  /  COORDINATION",FontSize=20,FontWeight=FontWeights.Bold,Margin=new Thickness(0,6,18,6)});
            toolbar.Children.Add(Button("Run Full Scan",()=>_actions.RunScan()));toolbar.Children.Add(Button("Refresh",()=>Refresh()));
            _bcf.Content="Export filtered BCF";_bcf.Click+=(_,__)=>Export(true);toolbar.Children.Add(_bcf);
            _excel.Content="Export filtered Excel";_excel.Click+=(_,__)=>Export(false);toolbar.Children.Add(_excel);toolbar.Children.Add(Button("Export Word",()=>ExportFormat(DashboardExportFormat.Word)));toolbar.Children.Add(_exportScope);
            _purge.Click+=(_,__)=>Safe(()=>{if(!_vm.Snapshot.IsCurrent)throw new InvalidOperationException("Select the current view before purging.");_actions.Purge();Refresh();});toolbar.Children.Add(_purge);
            var versions=new WrapPanel {Margin=new Thickness(12,0,12,0)};DockPanel.SetDock(versions,Dock.Top);root.Children.Add(versions);
            versions.Children.Add(new TextBlock {Text="Saved scan",VerticalAlignment=VerticalAlignment.Center});versions.Children.Add(_scan);
            versions.Children.Add(new TextBlock {Text="Compare with",VerticalAlignment=VerticalAlignment.Center});versions.Children.Add(_compare);
            DockPanel.SetDock(_context,Dock.Top);root.Children.Add(_context);root.Children.Add(_tabs);
            _scan.SelectionChanged+=(_,__)=>{if(_refreshing)return;_vm.ScanId=(_scan.SelectedItem as VersionChoice)?.Id??"";_vm.ComparisonId="";Refresh();};
            _compare.SelectionChanged+=(_,__)=>{if(_refreshing)return;_vm.ComparisonId=(_compare.SelectedItem as VersionChoice)?.Id??"";Refresh();};
            _tabs.Items.Add(new TabItem {Header="Overview",Content=new ScrollViewer {Content=_overview,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            _tabs.Items.Add(new TabItem {Header="Issues",Content=BuildIssues()});
            _tabs.Items.Add(new TabItem {Header="Groups",Content=BuildGroups()});
            _tabs.Items.Add(new TabItem {Header="History",Content=BuildHistory()});
            _tabs.Items.Add(new TabItem {Header="Analytics",Content=BuildAnalytics()});
            _tabs.SelectionChanged+=(_,args)=>{if(ReferenceEquals(args.Source,_tabs)&&!_refreshing&&_tabs.SelectedIndex==4)RefreshAnalytics();};
            Refresh();
        }
        private Button Button(string title,Action action)
        {var button=new Button {Content=title,Margin=new Thickness(4),Padding=new Thickness(10,5,10,5)};button.Click+=(_,__)=>Safe(action);return button;}
        private void Safe(Action action){try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Dashboard action",MessageBoxButton.OK,MessageBoxImage.Information);}}
        private UIElement BuildIssues()
        {
            var root=new DockPanel();
            var filters=new WrapPanel {Margin=new Thickness(0,5,0,10)};DockPanel.SetDock(filters,Dock.Top);root.Children.Add(filters);
            filters.Children.Add(new TextBlock {Text="Search",VerticalAlignment=VerticalAlignment.Center});filters.Children.Add(_search);
            _search.TextChanged+=(_,__)=>{if(!_refreshing){_vm.Search=_search.Text;ApplyFilters();}};
            filters.Children.Add(Button("Clear filters",()=>{_vm.DrillIds=null;_vm.Filters.Clear();_vm.Search="";_vm.GroupId="";_vm.QuickFilter="";_vm.ChangedOnly=false;_vm.ResultTypes=ResultTypes.All;Refresh();}));
            foreach(var option in new[]{(ResultTypes.Hard,"Hard"),(ResultTypes.PossibleHard,"Possible hard"),(ResultTypes.Clearance,"Clearance"),(ResultTypes.Unverified,"Unverified")}){
                var check=new CheckBox {Content=option.Item2,Margin=new Thickness(8),Foreground=Brushes.White};_types[option.Item1]=check;filters.Children.Add(check);
                check.Checked+=(_,__)=>{if(!_refreshing){_vm.ResultTypes|=option.Item1;ApplyFilters();}};
                check.Unchecked+=(_,__)=>{if(!_refreshing){_vm.ResultTypes&=~option.Item1;ApplyFilters();}};
            }
            var sections=new Dictionary<string,WrapPanel>();
            foreach(string heading in new[]{"State and scan changes","Classification","Location and ownership"}){
                var section=new WrapPanel();sections[heading]=section;
                filters.Children.Add(new Expander {Header=heading,Content=section,Width=440,Margin=new Thickness(4),Foreground=Brushes.White});
            }
            foreach(var key in new[]{"Status","Change","Flags","TestType","ClashType","Severity","Priority","DisciplineA","DisciplineB","SystemTypeA","SystemTypeB","CategoryNameA","CategoryNameB","LevelName","GridRef","ZoneName","Linked model","Owner"})
            {
                string heading=new[]{"Status","Change","Flags","Severity","Priority"}.Contains(key)?"State and scan changes":new[]{"LevelName","GridRef","ZoneName","Linked model","Owner"}.Contains(key)?"Location and ownership":"Classification";
                var combo=new ComboBox {Width=135,Margin=new Thickness(3),ToolTip=key};_filters[key]=combo;
                var panel=new StackPanel();panel.Children.Add(new TextBlock {Text=key,FontSize=11});panel.Children.Add(combo);sections[heading].Children.Add(panel);
                combo.SelectionChanged+=(_,__)=>{if(_refreshing)return;_vm.Filters[key]=(combo.SelectedItem as string)??"";ApplyFilters();};
            }
            var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(480)});root.Children.Add(body);
            body.Children.Add(_grid);var side=new DockPanel();Grid.SetColumn(side,1);body.Children.Add(side);var preview=BuildPreviewPanel();DockPanel.SetDock(preview,Dock.Top);side.Children.Add(preview);side.Children.Add(_details);
            foreach(var col in new[]{"ChangeKind","ChangeFlags","Status","Priority","Severity","GroupId","DisciplineA","DisciplineB","ElementAId","ElementBId","LevelName","GridRef","TestType","GapMm","OverlapVolumeMm3","Owner","DueDate","LastSeenAtUtc"})
                _grid.Columns.Add(new DataGridTextColumn {Header=col=="LastSeenAtUtc"?"Last seen (local)":col,Binding=new Binding(col),Width=col=="GroupId"?110:DataGridLength.SizeToHeader});
            _grid.SelectionChanged+=(_,__)=>{if(!_refreshing)ShowDetail();};
            AddDetail("Summary",_summary);AddDetail("Elements",_elements);AddDetail("Evidence",_evidence);AddDetail("History",_history);
            var comments=new StackPanel();comments.Children.Add(_comments);comments.Children.Add(new TextBlock {Text="Owner"});comments.Children.Add(_owner);comments.Children.Add(new TextBlock {Text="Due date (local)"});comments.Children.Add(_due);comments.Children.Add(new TextBlock {Text="Add comment"});comments.Children.Add(_comment);
            _save.Content="Save assignment / comment";_save.Margin=new Thickness(4);_save.Click+=(_,__)=>Safe(()=>{_vm.SaveMetadata(SelectedIds(),_owner.Text,_due.SelectedDate,_comment.Text);_comment.Clear();_actions.Mutated();Refresh();});comments.Children.Add(_save);AddDetail("Comments",comments);
            var views=new StackPanel();views.Children.Add(_viewpoint);views.Children.Add(_storedSnapshot);_two.Content="Show 2D";_three.Content="Show 3D";
            _two.Click+=(_,__)=>Safe(()=>_actions.Navigate(_vm.SelectedId,false));_three.Click+=(_,__)=>Safe(()=>_actions.Navigate(_vm.SelectedId,true));views.Children.Add(_two);views.Children.Add(_three);AddDetail("Viewpoint",views);
            views.Children.Add(Button("Select current components",()=>_actions.HistoricalNavigate(rowForNavigation())));
            views.Children.Add(Button("Open current issue for editing",()=>OpenCurrentIssue(_vm.SelectedId)));
            _inspect.Click+=(_,__)=>Safe(()=>_actions.Inspect(_vm.SelectedId));views.Children.Add(_inspect);
            var workflow=new WrapPanel {Margin=new Thickness(4)};DockPanel.SetDock(workflow,Dock.Bottom);root.Children.Insert(1,workflow);
            workflow.Children.Add(new TextBlock {Text="Selected issues: next action",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5)});workflow.Children.Add(_next);workflow.Children.Add(_reason);
            _apply.Content="Apply to selection";_apply.Margin=new Thickness(5);_apply.Click+=(_,__)=>Safe(()=>{if(_next.SelectedItem is ClashStatus target){_vm.ChangeStatus(SelectedIds(),target,_reason.Text);_reason.Clear();_actions.Mutated();Refresh();}});workflow.Children.Add(_apply);
            return root;
        }
        private void AddDetail(string title,UIElement element)=>_details.Items.Add(new TabItem {Header=title,Content=new ScrollViewer {Content=element,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        private List<string> SelectedIds()=>_grid.SelectedItems.Cast<IssueRow>().Select(r=>r.Data.ClashId).ToList();
        private void ApplyFilters()
        {
            var selected=_vm.SelectedId;_refreshing=true;_grid.ItemsSource=_vm.Visible().Select(c=>new IssueRow(c)).ToList();
            _grid.SelectedItem=_grid.Items.Cast<IssueRow>().FirstOrDefault(r=>r.Data.ClashId==selected);foreach(var item in _grid.Items.Cast<IssueRow>().Where(r=>_vm.SelectedIds.Contains(r.Data.ClashId)))if(!_grid.SelectedItems.Contains(item))_grid.SelectedItems.Add(item);_refreshing=false;ShowDetail();_actions.FilterChanged();
        }
        public void SelectIssues(){_tabs.SelectedIndex=1;}
        public void Refresh(List<ClashResult> rows,List<ClashGroup> groups)=>Refresh(); // Legacy controller publication adapter.
        public void Refresh(bool latest=false)
        {
            _vm.Refresh(latest);_refreshing=true;var snapshot=_vm.Snapshot;
            var choices=snapshot.Versions.Select(v=>new VersionChoice(v.ScanId,$"V{v.SequenceNumber:D3} · {v.EndedAtUtc?.ToLocalTime():g}")).ToList();
            _scan.ItemsSource=choices;_scan.SelectedItem=choices.FirstOrDefault(v=>v.Id==snapshot.Selected?.ScanId);
            var comparisons=snapshot.Versions.Where(v=>v.SequenceNumber<(snapshot.Selected?.SequenceNumber??0)).Select(v=>new VersionChoice(v.ScanId,$"V{v.SequenceNumber:D3} · {v.EndedAtUtc?.ToLocalTime():g}")).ToList();
            _compare.ItemsSource=comparisons;_compare.SelectedItem=comparisons.FirstOrDefault(v=>v.Id==snapshot.Comparison?.PreviousScanId);
            _search.Text=_vm.Search;
            foreach(var type in _types)type.Value.IsChecked=(_vm.ResultTypes&type.Key)!=0;
            foreach(var item in _filters){var options=new[]{""}.Concat(snapshot.Rows.SelectMany(r=>item.Key=="Flags"?DashboardWorkspace.Field(r,item.Key).Split(',').Select(v=>v.Trim()):new[]{DashboardWorkspace.Field(r,item.Key)}).Where(v=>v!="").Distinct().OrderBy(v=>v)).ToList();item.Value.ItemsSource=options;item.Value.SelectedItem=_vm.Filters.TryGetValue(item.Key,out var filter)?filter:"";}
            _bcf.IsEnabled=_excel.IsEnabled=snapshot.Selected!=null&&snapshot.Rows.Count>0&&(!snapshot.IsCurrent||(snapshot.Metrics.Stale==0&&!snapshot.RequiresFullScan));
            _purge.IsEnabled=snapshot.IsCurrent&&snapshot.Rows.Count>0;
            var scan=snapshot.Selected;
            _context.Text=scan==null?"No completed Full Scan. Imported results have no verified comparison baseline.":
                $"{scan.Capture.DisplayName} · V{scan.SequenceNumber:D3} · {scan.EndedAtUtc?.ToLocalTime():g} · {scan.Capture.ScanMode} · {scan.Capture.RuleSetName} · {scan.Completeness}\n"+
                (snapshot.IsCurrent?$"Current workflow · {snapshot.Metrics.Stale} stale issue(s). {(snapshot.RequiresFullScan?"Model changes require Full Scan. ":"")} ":"Historical snapshot · read-only. ")+
                (snapshot.Comparison?.InitialBaseline==true?"Initial baseline. ":$"Compared with {(_compare.SelectedItem as VersionChoice)?.Label}. ")+snapshot.Comparison?.CoverageNotice+
                $"\nScope: {scan.Capture.ScopeJson} · Configuration: {scan.Capture.ConfigurationFingerprint.Substring(0,Math.Min(12,scan.Capture.ConfigurationFingerprint.Length))}";
            BuildOverview();RefreshCoordination();_refreshing=false;ApplyFilters();if(latest)_tabs.SelectedIndex=0;
        }
        private void Drill(string field,string value){_vm.ChangedOnly=false;_vm.DrillIds=null;_vm.Filters.Clear();_vm.Search="";_vm.GroupId="";_vm.QuickFilter="";_vm.ResultTypes=ResultTypes.All;if(field!="")_vm.Filters[field]=value;Refresh();SelectIssues();}
        private void Quick(string value){_vm.ChangedOnly=false;_vm.DrillIds=null;_vm.Filters.Clear();_vm.Search="";_vm.GroupId="";_vm.QuickFilter=value;_vm.ResultTypes=ResultTypes.All;Refresh();SelectIssues();}
        private void BuildOverview()
        {
            _overview.Children.Clear();var m=_vm.Snapshot.Metrics;
            var cards=new UniformGrid {Columns=4,Margin=new Thickness(0,8,0,18)};_overview.Children.Add(cards);
            void Card(string title,string value,Action action,string tip="")
            {var content=new StackPanel {Margin=new Thickness(14)};content.Children.Add(new TextBlock {Text=title,FontSize=14});content.Children.Add(new TextBlock {Text=value,FontSize=30,FontWeight=FontWeights.Bold});var b=Button("",action);b.Content=content;b.ToolTip=tip;cards.Children.Add(b);}
            Card("Actionable open",m.Open.ToString(),()=>Quick("Open"));
            Card("Confirmed critical",m.Critical.ToString(),()=>Quick("Critical"));
            Card("New since baseline",m.New.ToString(),()=>Drill("Change","New"));Card("Reopened",m.Reopened.ToString(),()=>Drill("Change","Reopened"));Card("Resolved this comparison",m.Resolved.ToString(),()=>Drill("Change","Resolved"));Card("Observed unverified",m.Unverified.ToString(),()=>Quick("Unverified"));
            Card("Actionable groups",m.ActionableGroups.ToString(),()=>_queueTitle.BringIntoView());Card("Verified absence health",m.Health.HasValue?$"{m.Health:0}%":"—",()=>Quick("Health"),$"Weighted verified absence / confirmed eligible evidence; critical weight 3, other weight 1. {m.HealthBasis} eligible issues. Approved/Ignored excluded; manual resolution does not prove physical absence.");
            _overview.Children.Add(new TextBlock {Text=$"Health basis: {m.HealthBasis} confirmed eligible issues · critical weight 3, other weight 1 · weighted verified absence / total eligible weight. Manual status changes do not establish physical absence.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8,0,8,12)});
            _queueTitle=new TextBlock {Text="PRIORITY QUEUE · actionable groups",FontSize=18,FontWeight=FontWeights.Bold,Margin=new Thickness(6,10,6,10)};_overview.Children.Add(_queueTitle);
            var ranked=DashboardMetricPolicy.Rank(_vm.Snapshot.Rows.Where(r=>LifecycleCommandService.Actionable(r.Status))).GroupBy(r=>r.GroupId==""?r.ClashId:r.GroupId).Take(20);
            foreach(var group in ranked){var saved=_vm.Snapshot.Groups.FirstOrDefault(g=>g.GroupKey==group.Key);var first=group.First();string key=first.GroupId;_overview.Children.Add(Button($"{first.Priority} · {saved?.Title??first.LevelName+" / "+first.DisciplineA+"–"+first.DisciplineB} · {group.Count()} actionable\n{saved?.Reason} {saved?.PrimaryOffender}",()=>{_vm.ChangedOnly=false;_vm.DrillIds=null;_vm.Filters.Clear();_vm.QuickFilter="";_vm.ResultTypes=ResultTypes.All;_vm.GroupId=key;_vm.Search=key==""?first.ClashId:"";Refresh();SelectIssues();}));}
            if(!ranked.Any())_overview.Children.Add(new TextBlock {Text="No actionable groups in this saved scan.",Margin=new Thickness(12)});
        }
        private void ShowDetail()
        {
            var row=(_grid.SelectedItem as IssueRow)?.Data;_vm.SelectedId=row?.ClashId??"";QueuePreview(row);var ids=SelectedIds();_vm.SelectedIds.Clear();_vm.SelectedIds.UnionWith(ids);_next.ItemsSource=_vm.Allowed(ids);_next.SelectedIndex=0;
            _apply.IsEnabled=_vm.Snapshot.IsCurrent&&_next.Items.Count>0;_save.IsEnabled=_vm.Snapshot.IsCurrent&&ids.Count>0;_two.IsEnabled=_three.IsEnabled=_vm.Snapshot.IsCurrent&&row!=null&&_vm.Snapshot.Metrics.Stale==0;
            _inspect.IsEnabled=_two.IsEnabled;
            if(row==null){_storedSnapshot.Source=null;foreach(var text in new[]{_summary,_elements,_evidence,_history,_comments,_viewpoint})text.Text="Select an issue to inspect it.";return;}
            _summary.Text=$"{row.ClashId}\n\nStatus: {row.Status}\nChange: {row.ChangeKind}\nFlags: {row.ChangeFlags}\n{row.EvaluationReason}\n\n{row.Priority} / {row.Severity}\nGroup: {row.GroupId}\n{row.LevelName} · {row.GridRef} · {row.ZoneName}\nFirst detected: {row.DetectedAt:g} ({row.TimestampKind})\nLast seen: {row.LastSeenAtUtc?.ToLocalTime():g}\nObserved scans: {row.SeenInScanCount} · consecutive: {row.ConsecutiveScanCount}\nResolved: {row.ResolvedAtUtc?.ToLocalTime():g}\nReopened: {row.ReopenedAtUtc?.ToLocalTime():g}";
            _elements.Text=$"A: {row.ElementAId}\n{row.ElementUniqueIdA}\n{row.CategoryNameA} / {row.FamilyTypeA}\n{row.DisciplineA} · {row.SystemTypeA}\nLink: {row.LinkFileA} / {row.LinkInstanceA}\n\nB: {row.ElementBId}\n{row.ElementUniqueIdB}\n{row.CategoryNameB} / {row.FamilyTypeB}\n{row.DisciplineB} · {row.SystemTypeB}\nLink: {row.LinkFileB} / {row.LinkInstanceB}";
            _evidence.Text=$"{row.ObservationKind} · {row.TestType}\nUnverified: {row.UnverifiedReason}\nGap: {row.GapMm:0.##} mm\nOverlap: {row.OverlapVolumeMm3:0.##} mm³\nRequired clearance: {row.RequiredClearanceMm:0.##} mm\nRule: {row.RuleApplied}\n\n{row.GeometryEvidence}\n\nGeometryChanged includes revision/signature changes; it does not prove a shape change.";
            _history.Text=_vm.History(row.ClashId);_comments.Text=DashboardWorkspace.Metadata(row,"Comments");_owner.Text=DashboardWorkspace.Metadata(row,"AssignedEngineer");
            _due.SelectedDate=DashboardWorkspace.DueLocal(row)?.Date;
            _storedSnapshot.Source=null;
            try {var path=DashboardWorkspace.Metadata(row,"SnapshotPath");if(System.IO.File.Exists(path)){var bitmap=new System.Windows.Media.Imaging.BitmapImage();bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(System.IO.Path.GetFullPath(path));bitmap.EndInit();bitmap.Freeze();_storedSnapshot.Source=bitmap;}}catch{ /* unavailable external media is not reconstructed */ }
            _viewpoint.Text="Navigate the current model through Revit's API queue. Historical geometry cannot be reconstructed from current elements.\n\nSnapshot file reference (if still available): "+DashboardWorkspace.Metadata(row,"SnapshotPath");
        }
        private ClashObservation rowForNavigation()=>_vm.Snapshot.Rows.Single(r=>r.ClashId==_vm.SelectedId);
        private void Export(bool bcf)=>ExportFormat(bcf?DashboardExportFormat.Bcf:DashboardExportFormat.Excel);
        private void ExportFormat(DashboardExportFormat format)=>Safe(()=>_actions.ExportPackage(DashboardExportService.Capture(_vm.Snapshot,_vm.Visible(),_exportScope.SelectedIndex==1?SelectedGroup():null,format,FilterContext())));
        private sealed class VersionChoice {public string Id {get;}public string Label {get;}public VersionChoice(string id,string label){Id=id;Label=label;}public override string ToString()=>Label;}
        private sealed class IssueRow
        {
            public ClashObservation Data {get;}public IssueRow(ClashObservation data){Data=data;}
            public string ChangeKind=>Data.ChangeKind?.ToString()??"";public string ChangeFlags=>Data.ChangeFlags.ToString();public string Status=>Data.Status;public string Priority=>Data.Priority;public string Severity=>Data.Severity;public string GroupId=>Data.GroupId;public string DisciplineA=>Data.DisciplineA;public string DisciplineB=>Data.DisciplineB;public long ElementAId=>Data.ElementAId;public long ElementBId=>Data.ElementBId;public string LevelName=>Data.LevelName;public string GridRef=>Data.GridRef;public string TestType=>Data.TestType;public double GapMm=>Data.GapMm;public double OverlapVolumeMm3=>Data.OverlapVolumeMm3;public string Owner=>DashboardWorkspace.Metadata(Data,"AssignedEngineer");public string DueDate=>DashboardWorkspace.DueLocal(Data)?.ToString("d")??"";public string LastSeenAtUtc=>Data.LastSeenAtUtc?.ToLocalTime().ToString("g")??"";
        }
    }
}
