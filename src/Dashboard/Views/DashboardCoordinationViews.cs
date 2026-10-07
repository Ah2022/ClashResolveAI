using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard.Application;
using ClashResolveAI.Dashboard.Domain;
namespace ClashResolveAI.Dashboard
{
    public partial class DashboardWindow
    {
        private readonly DataGrid _groupGrid=Table(),_members=Table(),_attemptGrid=Table(),_comparisonGrid=Table(),_timelineGrid=Table();
        private readonly TextBlock _groupInfo=Text(),_attemptInfo=Text(),_comparisonInfo=Text();
        private readonly TextBox _groupOwner=new TextBox(),_groupReason=new TextBox();
        private readonly DatePicker _groupDue=new DatePicker();private readonly ComboBox _groupNext=new ComboBox();
        private Button _assignGroup=null!,_statusGroup=null!,_exportGroup=null!;
        private string _historyDocument="";
        private readonly CheckBox _changed=new CheckBox {Content="Changed outcomes / flags only",Foreground=Brushes.White,Margin=new Thickness(8)};
        private static DataGrid Table()=>new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,Foreground=Brushes.Black,SelectionMode=DataGridSelectionMode.Single};
        private static void Columns(DataGrid grid,params string[] columns){foreach(var name in columns)grid.Columns.Add(new DataGridTextColumn {Header=name=="StartedAtUtc"?"Started (local)":name=="EndedAtUtc"?"Ended (local)":name,Binding=new Binding(name){Converter=name=="StartedAtUtc"||name=="EndedAtUtc"?new LocalScanTime():null},Width=name=="Title"?230:name=="Detail"?550:name=="GeometryEvidence"?240:name=="Reason"||name=="State"||name=="EffectiveOwners"?180:120});}
        private UIElement BuildGroups()
        {
            var root=new Grid();root.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});root.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(430)});
            Columns(_groupGrid,"Title","Reason","Count","Open","New","Resolved","State","Evidence","Owner","EffectiveOwners","Due","Oldest");root.Children.Add(_groupGrid);
            var detail=new DockPanel();Grid.SetColumn(detail,1);root.Children.Add(detail);var top=new StackPanel();var controls=new ScrollViewer {Content=top,MaxHeight=400,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};DockPanel.SetDock(controls,Dock.Top);detail.Children.Add(controls);top.Children.Add(_groupInfo);
            top.Children.Add(Button("Show members in Issues",()=>{var group=SelectedGroup();if(group.CurrentIds.Count==0){_vm.ScanId="snapshot:"+(_vm.Snapshot.Groups.Any(g=>g.GroupKey==group.Key)?_vm.Snapshot.Selected!.ScanId:(_vm.Snapshot.VersionComparison??_vm.Snapshot.Comparison)!.PreviousScanId);_vm.ComparisonId="";}_vm.Search="";_vm.QuickFilter="";_vm.DrillIds=null;_vm.Filters.Clear();_vm.GroupId=group.Key;_vm.ResultTypes=ResultTypes.All;_vm.ChangedOnly=false;Refresh();SelectIssues();}));
            top.Children.Add(new TextBlock {Text="Group defaults (manual issue overrides are preserved)"});top.Children.Add(_groupOwner);top.Children.Add(_groupDue);
            _assignGroup=Button("Save group defaults",()=>{_vm.AssignGroup(SelectedGroup().Key,_groupOwner.Text,_groupDue.SelectedDate);_actions.Mutated();Refresh();});top.Children.Add(_assignGroup);
            top.Children.Add(new TextBlock {Text="Next workflow action / reason"});top.Children.Add(_groupNext);top.Children.Add(_groupReason);_statusGroup=Button("Apply status to all current members",()=>{if(_groupNext.SelectedItem is ClashStatus state){_vm.GroupStatus(SelectedGroup().Key,state,_groupReason.Text);_actions.Mutated();Refresh();}});top.Children.Add(_statusGroup);
            _exportGroup=Button("Export selected group BCF",()=>_actions.ExportGroup(SelectedGroup()));top.Children.Add(_exportGroup);
            top.Children.Add(Button("Show member 2D (current model)",()=>_actions.Navigate(SelectedMember().ClashId,false)));top.Children.Add(Button("Show member 3D (current model)",()=>_actions.Navigate(SelectedMember().ClashId,true)));
            top.Children.Add(Button("Inspect member evidence",()=>_actions.Inspect(SelectedMember().ClashId)));
            Columns(_members,"ClashId","Status","ChangeKind","TestType","GeometryEvidence","LevelName");detail.Children.Add(_members);
            _groupGrid.SelectionChanged+=(_,__)=>{if(!_refreshing)GroupDetail();};return root;
        }
        private GroupView SelectedGroup()=>_groupGrid.SelectedItem as GroupView??throw new InvalidOperationException("Select a group first.");
        private ClashObservation SelectedMember()=>_members.SelectedItem as ClashObservation??throw new InvalidOperationException("Select a group member first.");
        private void GroupDetail()
        {
            var group=_groupGrid.SelectedItem as GroupView;_members.ItemsSource=group?.Members;bool editable=_vm.Snapshot.IsCurrent&&group!=null&&group.CurrentIds.Count>0;
            _assignGroup.IsEnabled=editable;_exportGroup.IsEnabled=editable&&!_vm.Snapshot.RequiresFullScan&&_vm.Snapshot.Metrics.Stale==0;
            _groupNext.ItemsSource=group==null?Array.Empty<ClashStatus>():_vm.Allowed(group.CurrentIds);_groupNext.SelectedIndex=0;_statusGroup.IsEnabled=editable&&_groupNext.Items.Count>0;
            _groupInfo.Text=group==null?"Select a group to coordinate its members.":$"{group.Title}\n{group.Reason}\n{group.State}\n{group.Evidence}\n{group.Offender}\nIdentity: {group.OffenderIdentity}\n{group.Lineage}\nArchived / baseline members retain captured state; actions apply to current members.\nOldest actionable: {group.Oldest?.ToLocalTime():g}\nDefault owner: {group.Owner}\nEffective owners: {group.EffectiveOwners}\n{Newtonsoft.Json.Linq.JObject.Parse(group.Revision.CoordinationJson)["Note"]}";
            _groupOwner.Text=group?.Owner??"";_groupDue.SelectedDate=group?.Due?.Date;
        }
        private UIElement BuildHistory()
        {
            var root=new Grid();root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(230)});root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            var top=new Grid();top.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});top.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(510)});root.Children.Add(top);
            Columns(_attemptGrid,"SequenceNumber","State","StartedAtUtc","EndedAtUtc","Completeness","FailureReason");top.Children.Add(_attemptGrid);var info=new StackPanel();var metadataPanel=new ScrollViewer {Content=info,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(metadataPanel,1);top.Children.Add(metadataPanel);info.Children.Add(_attemptInfo);
            info.Children.Add(Button("Browse this completed version",()=>{if(_attemptGrid.SelectedItem is ScanVersion v&&v.State==ScanVersionState.Completed){_vm.ScanId="snapshot:"+v.ScanId;_vm.ComparisonId="";_vm.DrillIds=null;_vm.Filters.Clear();_vm.QuickFilter="";_vm.Search="";_vm.GroupId="";_vm.ChangedOnly=false;Refresh();_tabs.SelectedIndex=3;}else throw new InvalidOperationException("Only completed versions contain published observations. Failed/cancelled attempts retain diagnostics only.");}));
            _attemptGrid.SelectionChanged+=(_,__)=>{if(_attemptGrid.SelectedItem is ScanVersion v)_attemptInfo.Text=$"V{v.SequenceNumber:D3} · {v.State}\nStarted: {v.StartedAtUtc.ToLocalTime():g}\nEnded: {v.EndedAtUtc?.ToLocalTime():g}\n{v.FailureReason}\nModel: {v.Capture.ModelFingerprintKind} / {v.Capture.ModelFingerprint}\nMode: {v.Capture.ScanMode} · Rules: {v.Capture.RuleSetName}\nConfiguration: {v.Capture.ConfigurationFingerprint}\nScope: {v.Capture.ScopeJson}\nLinks: {v.Capture.LinkedModelsJson}\nCoverage: {v.CoverageJson}\nStatistics: {v.StatisticsJson}";};
            var lower=new TabControl();Grid.SetRow(lower,1);root.Children.Add(lower);var comparison=new DockPanel();var bar=new StackPanel();DockPanel.SetDock(bar,Dock.Top);comparison.Children.Add(bar);bar.Children.Add(_comparisonInfo);bar.Children.Add(_changed);
            _changed.Checked+=(_,__)=>ComparisonRows();_changed.Unchecked+=(_,__)=>ComparisonRows();
            bar.Children.Add(Button("Show changed current-version issues",()=>{_vm.QuickFilter="";_vm.ChangedOnly=true;_vm.ResultTypes=ResultTypes.All;_vm.DrillIds=null;_vm.Filters.Clear();_vm.GroupId="";_vm.Search="";Refresh();SelectIssues();}));
            bar.Children.Add(Button("Inspect historical observation",()=>{if(_comparisonGrid.SelectedItem is IssueChange change){_vm.ScanId="snapshot:"+(_vm.Snapshot.ScanRows.Any(r=>r.ClashId==change.ClashId)?_vm.Snapshot.Selected!.ScanId:(_vm.Snapshot.VersionComparison??_vm.Snapshot.Comparison)!.PreviousScanId);_vm.SelectedId=change.ClashId;_vm.QuickFilter="";_vm.DrillIds=null;_vm.Filters.Clear();_vm.Search=change.ClashId;_vm.GroupId="";_vm.ChangedOnly=false;_vm.ResultTypes=ResultTypes.All;Refresh();SelectIssues();}else throw new InvalidOperationException("Select a comparison issue.");}));
            bar.Children.Add(Button("Select current components from history",()=>{var change=_comparisonGrid.SelectedItem as IssueChange??throw new InvalidOperationException("Select a comparison issue.");_actions.HistoricalNavigate(_vm.Snapshot.Rows.Concat(_vm.Snapshot.ScanRows).Concat(_vm.Snapshot.BaselineRows).First(r=>r.ClashId==change.ClashId));}));
            Columns(_comparisonGrid,"ClashId","Kind","Flags","Reason");comparison.Children.Add(_comparisonGrid);lower.Items.Add(new TabItem {Header="Version comparison",Content=comparison});
            Columns(_timelineGrid,"Lane","Time","Scan","Workflow","Change","Flags","Detail");lower.Items.Add(new TabItem {Header="Issue observations / workflow events",Content=_timelineGrid});
            _comparisonGrid.SelectionChanged+=(_,__)=>{_timelineGrid.ItemsSource=_comparisonGrid.SelectedItem is IssueChange change?_vm.Timeline(change.ClashId):null;};return root;
        }
        private void ComparisonRows(){var comparison=_vm.Snapshot.VersionComparison??_vm.Snapshot.Comparison;_comparisonGrid.ItemsSource=comparison?.Issues.Where(i=>_changed.IsChecked!=true||(i.Kind!=ClashChangeKind.Persistent&&i.Kind!=ClashChangeKind.UnchangedResolved)||i.Flags!=ClashChangeFlags.None).ToList();}
        private void RefreshCoordination()
        {
            if(_historyDocument!=_vm.Snapshot.DocumentKey){_historyDocument=_vm.Snapshot.DocumentKey;_changed.IsChecked=false;_attemptInfo.Text="";_timelineGrid.ItemsSource=null;}
            string key=(_groupGrid.SelectedItem as GroupView)?.Key??"";_groupGrid.ItemsSource=_vm.GroupViews();_groupGrid.SelectedItem=_groupGrid.Items.Cast<GroupView>().FirstOrDefault(g=>g.Key==key);GroupDetail();
            var selected=(_attemptGrid.SelectedItem as ScanVersion)?.ScanId;_attemptGrid.ItemsSource=_vm.Snapshot.Attempts;_attemptGrid.SelectedItem=_vm.Snapshot.Attempts.FirstOrDefault(v=>v.ScanId==selected)??_vm.Snapshot.Selected;
            _comparisonInfo.Text=(_vm.Snapshot.VersionComparison??_vm.Snapshot.Comparison)?.CoverageNotice??"No completed comparison baseline.";ComparisonRows();
        }
        private void OpenCurrentIssue(string id)
        {
            _vm.ScanId="";_vm.ComparisonId="";_vm.ChangedOnly=false;_vm.Refresh();
            if(!_vm.Snapshot.Rows.Any(r=>r.ClashId==id))throw new InvalidOperationException("This identity is archived or no longer current. Historical facts remain read-only.");
            _vm.SelectedId=id;_vm.QuickFilter="";_vm.Search=id;_vm.DrillIds=null;_vm.Filters.Clear();_vm.GroupId="";_vm.ResultTypes=ResultTypes.All;Refresh();SelectIssues();
        }
        private sealed class LocalScanTime:IValueConverter
        {
            public object Convert(object value,Type target,object parameter,System.Globalization.CultureInfo culture)=>value is DateTime date?date.ToLocalTime().ToString("g"):"";
            public object ConvertBack(object value,Type target,object parameter,System.Globalization.CultureInfo culture)=>throw new NotSupportedException();
        }        public void SelectGroups(){_tabs.SelectedIndex=2;}
        public void SelectHistory(){_tabs.SelectedIndex=3;}
    }
}
