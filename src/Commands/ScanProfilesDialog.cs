using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WpfGrid=System.Windows.Controls.Grid;
using WpfBinding=System.Windows.Data.Binding;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using Control=System.Windows.Controls.Control;

namespace ClashResolveAI.Commands
{
    internal sealed class WorkloadPairRow : INotifyPropertyChanged
    {
        private bool _included=true;
        internal ScanModelPair Pair=null!;
        internal Action<WorkloadPairRow>? InclusionChanged;
        public string Label { get; set; }="";
        public string Relationships { get; set; }="";
        public string Candidates { get; set; }="";
        public string Duration { get; set; }="";
        public string Confidence { get; set; }="";
        public bool Included { get=>_included;set{if(_included==value)return;_included=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Included)));InclusionChanged?.Invoke(this);}}
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    internal sealed class ScanProfilesDialog : Window
    {
        internal static Action<ScanProfilesDialog>? VerificationOpened;
        private sealed class Choice
        {
            public string Id="",Name="";public override string ToString()=>Name;
        }
        private readonly Document _doc;
        private readonly AppSettings _settings;
        private readonly List<LinkedModel> _links;
        private readonly IReadOnlyList<ScanFloor> _floors;
        private readonly ComboBox _profile,_scope,_mode,_ruleSet,_pairA,_pairB;
        private readonly ListBox _floorList,_zones,_pairList;
        private readonly CheckBox _host,_hostLinks,_inside,_cross,_insulation,_generic,_supports,_structure,_joints;
        private readonly List<Tuple<Choice,CheckBox>> _linkChoices=new List<Tuple<Choice,CheckBox>>();
        private readonly List<Tuple<Discipline,CheckBox>> _disciplines=new List<Tuple<Discipline,CheckBox>>();
        private readonly List<Tuple<long,CheckBox>> _categories=new List<Tuple<long,CheckBox>>();
        private readonly List<Tuple<string,CheckBox>> _rules=new List<Tuple<string,CheckBox>>();
        private readonly List<ScanModelPair> _selectedPairs=new List<ScanModelPair>();
        private readonly List<ScanModelPair> _excluded=new List<ScanModelPair>();
        private readonly TextBox _overlap,_allowance,_maintenance,_areaRadius;
        private readonly TextBlock _profileInfo,_status,_summary,_warnings;
        private readonly Button _start,_calculate,_cancelEstimate;
        private readonly TabControl _tabs;
        private readonly StackPanel _ruleList;
        private readonly FrameworkElement _advancedRules,_advancedJobs;
        private readonly DataGrid _models,_workload;
        private readonly DispatcherTimer _timer;
        private readonly Func<ScanPlan,ScanEstimateJob> _estimateFactory;
        private ScanEstimateJob? _job;
        private ScanPlan? _estimatedPlan;
        private ScanEstimate? _estimate;
        private bool _initializing=true,_advancing;
        internal ScanPlan? ApprovedPlan { get; private set; }
        internal ScanEstimate? CurrentEstimate=>_estimate;
        internal bool CanStart=>_start.IsEnabled;
        internal ScanProfile SelectedProfile {get=>(ScanProfile)_profile.SelectedIndex;set=>_profile.SelectedIndex=(int)value;}
        private static Brush Bg=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(15,20,32));
        private static Brush Card=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(24,32,48));
        private static Brush Ink=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(235,240,248));
        internal ScanProfilesDialog(UIApplication app,AppSettings settings,Func<ScanPlan,ScanEstimateJob>? estimateFactory=null)
        {
            _doc=app.ActiveUIDocument.Document;_settings=settings.ScanSnapshot();_settings.MigrateScanConfiguration();
            _links=RevitScanPlanFactory.Links(_doc);_floors=RevitScanEstimate.Floors(_doc);
            _estimateFactory=estimateFactory??(plan=>RevitScanEstimate.Create(_doc,plan));
            Title="Full Scan — coordination profiles";Width=1160;Height=850;MinWidth=940;MinHeight=650;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Bg;
            var root=new DockPanel {Margin=new Thickness(18)};Content=root;
            var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
            header.Children.Add(Text("Full Scan",23));
            _profile=new ComboBox {ItemsSource=new[]{"Quick coordination — recommended","Detailed coordination","Advanced"},SelectedIndex=0,Height=32,Margin=new Thickness(0,4,0,4)};header.Children.Add(_profile);
            _profileInfo=Text("");header.Children.Add(_profileInfo);
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            _status=Text("Choose scope and loaded links, then calculate the estimate.");footer.Children.Add(_status);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);
            _cancelEstimate=Button("Stop estimate",()=>{CancelEstimate();_status.Text="Estimate stopped. Recalculate before starting.";});_cancelEstimate.IsEnabled=false;actions.Children.Add(_cancelEstimate);
            _calculate=Button("Calculate estimate",()=>CalculateEstimate());actions.Children.Add(_calculate);
            _start=Button("Start reviewed scan",()=>AcceptEstimate(true));_start.IsEnabled=false;actions.Children.Add(_start);
            actions.Children.Add(Button("Cancel",()=>Close()));
            _tabs=new TabControl {Background=Card,Margin=new Thickness(0,8,0,8)};root.Children.Add(_tabs);

            var scopePanel=Panel();Tab("1  Scope",scopePanel);
            scopePanel.Children.Add(Text("Choose the coordination area",17));
            _scope=new ComboBox {ItemsSource=new[]{"Floor selection","Selected zones","Entire model — Advanced"},SelectedIndex=0,Margin=new Thickness(0,6,0,8)};scopePanel.Children.Add(_scope);
            _floorList=new ListBox {ItemsSource=_floors,SelectionMode=SelectionMode.Single,Height=185,Background=Card,Foreground=Ink};scopePanel.Children.Add(_floorList);
            var current=app.ActiveUIDocument.ActiveView?.GenLevel?.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _floorList.SelectedItem=_floors.FirstOrDefault(f=>f.Id==current)??_floors.FirstOrDefault();
            scopePanel.Children.Add(Text("Quick uses one floor. Detailed and Advanced can select multiple floors with Ctrl/Shift. Zones use a floor, workset and optional linked model."));
            _zones=new ListBox {SelectionMode=SelectionMode.Multiple,Height=120,Background=Card,Foreground=Ink};scopePanel.Children.Add(_zones);
            if(Session.ActiveZone!=null)_zones.Items.Add(new ScanZone(Session.ActiveZone));
            var zoneActions=new StackPanel {Orientation=Orientation.Horizontal};scopePanel.Children.Add(zoneActions);
            zoneActions.Children.Add(Button("Add zone…",()=>AddZone()));
            zoneActions.Children.Add(Button("Remove selected zones",()=>{foreach(var z in _zones.SelectedItems.Cast<object>().ToArray())_zones.Items.Remove(z);Invalidate();}));

            var modelPanel=Panel();Tab("2  Models & relationships",modelPanel);
            modelPanel.Children.Add(Text("Scan relationships",17));
            _host=Flag(modelPanel,"Host ↔ host",true);_hostLinks=Flag(modelPanel,"Host ↔ selected loaded links",true);_inside=Flag(modelPanel,"Inside selected linked models",false);
            var advancedJobs=new StackPanel();modelPanel.Children.Add(advancedJobs);_advancedJobs=advancedJobs;
            _cross=Flag(advancedJobs,"Link ↔ link — separate job, explicit pairs only",false);
            _insulation=Flag(advancedJobs,"Insulation clearance — dedicated pass",false);
            modelPanel.Children.Add(Text("Loaded link instances",15));
            foreach(var link in _links) {
                var choice=new Choice {Id=link.Instance.UniqueId,Name=link.FileName+" [instance "+link.Instance.Id.Value+"]"};
                var check=Flag(modelPanel,choice.Name+(link.IsLoaded?"":" — unloaded"),link.IsLoaded&&_settings.FullScanLinkedModels);check.IsEnabled=link.IsLoaded;
                _linkChoices.Add(Tuple.Create(choice,check));
            }
            var pairPanel=new StackPanel {Margin=new Thickness(0,12,0,0)};advancedJobs.Children.Add(pairPanel);
            pairPanel.Children.Add(Text("Explicit link pairs",15));
            var loadedChoices=_linkChoices.Where(c=>c.Item2.IsEnabled).Select(c=>c.Item1).ToArray();
            _pairA=new ComboBox {ItemsSource=loadedChoices,SelectedIndex=loadedChoices.Length>0?0:-1,Margin=new Thickness(0,4,0,4)};
            _pairB=new ComboBox {ItemsSource=loadedChoices,SelectedIndex=loadedChoices.Length>1?1:loadedChoices.Length-1,Margin=new Thickness(0,4,0,4)};
            pairPanel.Children.Add(_pairA);pairPanel.Children.Add(_pairB);
            pairPanel.Children.Add(Button("Add selected pair",()=>AddPair()));
            _pairList=new ListBox {Height=90,Background=Card,Foreground=Ink};pairPanel.Children.Add(_pairList);
            pairPanel.Children.Add(Button("Remove selected pair",()=>{int index=_pairList.SelectedIndex;if(index>=0){_selectedPairs.RemoveAt(index);_pairList.Items.RemoveAt(index);Invalidate();}}));

            var rulesPanel=Panel();Tab("3  Rules & classification",rulesPanel);
            rulesPanel.Children.Add(Text("Rule set",16));
            _ruleSet=new ComboBox {ItemsSource=RulesEngine.GetAvailableRuleSets().Where(n=>n!="SystemKeywords").Concat(new[]{_settings.FullScanSnapshot().RuleSetName}).Distinct().OrderBy(n=>n).ToArray(),SelectedItem=_settings.FullScanSnapshot().RuleSetName,Margin=new Thickness(0,4,0,8)};rulesPanel.Children.Add(_ruleSet);
            rulesPanel.Children.Add(Button("Review classification and project mappings…",()=>ReviewClassification()));
            rulesPanel.Children.Add(Text("Clearance rules",16));_ruleList=new StackPanel();rulesPanel.Children.Add(_ruleList);
            _areaRadius=Number(rulesPanel,"Detailed: radius around each confirmed hard-clash area (mm, maximum 50000)",2000);
            rulesPanel.Children.Add(Text("Detailed saves hard clashes first, then checks clearance only for elements touching those confirmed areas. No hard clashes means the clearance stage is skipped. Use Advanced for a broader clearance job."));
            var advancedRules=new StackPanel {Margin=new Thickness(0,12,0,0)};rulesPanel.Children.Add(advancedRules);_advancedRules=advancedRules;
            advancedRules.Children.Add(Text("Advanced test and candidate options",16));
            _mode=new ComboBox {ItemsSource=new[]{"Hard only","Hard + selected clearance"},SelectedIndex=0,Margin=new Thickness(0,4,0,8)};advancedRules.Children.Add(_mode);
            _generic=Flag(advancedRules,"Include explicitly mapped generic models",false);_supports=Flag(advancedRules,"Exclude construction aids and support families",true);
            _structure=Flag(advancedRules,"Include structural framing and columns",true);_joints=Flag(advancedRules,"Exclude directly connected joints",true);
            _overlap=Number(advancedRules,"Minimum overlap (mm³)",_settings.FullScanMinimumOverlapMM3);
            _allowance=Number(advancedRules,"Additional minimum clearance / insulation allowance (mm)",_settings.FullScanInsulationMM);
            _maintenance=Number(advancedRules,"Maintenance clearance (mm)",_settings.FullScanMaintenanceMM);
            advancedRules.Children.Add(Text("Disciplines — selecting a subset does not enable unsupported pairs",15));
            var discPanel=new WrapPanel();advancedRules.Children.Add(discPanel);
            foreach(Discipline d in Enum.GetValues(typeof(Discipline)))if(d!=Discipline.Unknown){var box=Flag(discPanel,d.ToString(),true);box.Margin=new Thickness(4,5,14,5);_disciplines.Add(Tuple.Create(d,box));}
            advancedRules.Children.Add(Text("Categories — walls/floors still require applicable rules",15));
            var cats=Enum.GetValues(typeof(Discipline)).Cast<Discipline>().SelectMany(ElementCollector.GetCategoriesForDiscipline)
                .Concat(new[]{BuiltInCategory.OST_GenericModel,BuiltInCategory.OST_SpecialityEquipment,BuiltInCategory.OST_PipeInsulations,BuiltInCategory.OST_DuctInsulations,BuiltInCategory.OST_DuctLinings}).Distinct().ToArray();
            var catPanel=new WrapPanel();advancedRules.Children.Add(catPanel);
            foreach(var cat in cats) {string name=_doc.Settings.Categories.get_Item(cat)?.Name??cat.ToString();var check=Flag(catPanel,name,true);check.Margin=new Thickness(4,5,14,5);_categories.Add(Tuple.Create((long)cat,check));}

            var estimatePanel=new DockPanel {Margin=new Thickness(12)};Tab("4  Estimate & start",estimatePanel,false);
            var overview=new StackPanel();DockPanel.SetDock(overview,Dock.Top);estimatePanel.Children.Add(overview);
            _summary=Text("Calculate an estimate to review this exact configuration.",14);overview.Children.Add(_summary);
            _warnings=Text("");_warnings.Foreground=new SolidColorBrush(System.Windows.Media.Color.FromRgb(255,209,128));overview.Children.Add(_warnings);
            var tables=new WpfGrid();tables.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});tables.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1.4,GridUnitType.Star)});estimatePanel.Children.Add(tables);
            _models=Table();_models.Columns.Add(Column("Model / instance","Name",270));_models.Columns.Add(Column("State","Status",145));_models.Columns.Add(Column("All non-type elements","ElementCount",145));
            _models.Columns.Add(Column("Policy candidates","EligibleCount",125));_models.Columns.Add(Column("Unknown","UnknownCount",100));_models.Columns.Add(Column("Excluded by policy","ExcludedCount",125));tables.Children.Add(_models);
            _workload=Table();_workload.IsReadOnly=false;_workload.Margin=new Thickness(0,12,0,0);WpfGrid.SetRow(_workload,1);tables.Children.Add(_workload);
            _workload.Columns.Add(new DataGridCheckBoxColumn {Header="Include",Binding=new WpfBinding("Included") {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=60});
            _workload.Columns.Add(Column("Model relationship","Label",270));_workload.Columns.Add(Column("Source relationships","Relationships",135));
            _workload.Columns.Add(Column("Estimated box pairs","Candidates",145));_workload.Columns.Add(Column("Duration range","Duration",190));_workload.Columns.Add(Column("Basis","Confidence",150));
            foreach(var column in _workload.Columns.Skip(1))column.IsReadOnly=true;

            _timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};_timer.Tick+=(_,__)=>AdvanceEstimate();
            _profile.SelectionChanged+=(_,__)=>ApplyProfile();
            _scope.SelectionChanged+=(_,__)=>{UpdateScope();Invalidate();};_floorList.SelectionChanged+=(_,__)=>Invalidate();_zones.SelectionChanged+=(_,__)=>Invalidate();
            _ruleSet.SelectionChanged+=(_,__)=>{FillRules();Invalidate();};_mode.SelectionChanged+=(_,__)=>{UpdateTests();Invalidate();};
            _cross.Checked+=(_,__)=>JobChanged(_cross);_cross.Unchecked+=(_,__)=>JobChanged(null);_insulation.Checked+=(_,__)=>JobChanged(_insulation);_insulation.Unchecked+=(_,__)=>JobChanged(null);
            Closing+=(_,__)=>CancelEstimate();
            Loaded+=(_,__)=>{if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_SCANESTIMATE")=="1")VerificationOpened?.Invoke(this);};
            _initializing=false;FillRules();ApplyProfile();
        }
        private StackPanel Panel()=>new StackPanel {Margin=new Thickness(12)};
        private void Tab(string name,FrameworkElement content,bool scroll=true)=>_tabs.Items.Add(new TabItem {Header=name,Content=scroll?new ScrollViewer {Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}:content});
        private static TextBlock Text(string text,double size=12)=>new TextBlock {Text=text,FontSize=size,Foreground=Ink,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,6)};
        private Button Button(string text,Action action){var button=new Button {Content=text,Margin=new Thickness(3),Padding=new Thickness(12,6,12,6)};button.Click+=(_,__)=>Run(action);return button;}
        private CheckBox Flag(System.Windows.Controls.Panel panel,string label,bool value)
        {
            var check=new CheckBox {Content=label,IsChecked=value,Foreground=Ink,Margin=new Thickness(0,5,0,5)};panel.Children.Add(check);
            check.Checked+=(_,__)=>Invalidate();check.Unchecked+=(_,__)=>Invalidate();return check;
        }
        private TextBox Number(StackPanel panel,string label,double value)
        {
            panel.Children.Add(Text(label));var box=new TextBox {Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture),Width=180,HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(box);box.TextChanged+=(_,__)=>Invalidate();return box;
        }
        private static DataGrid Table()
        {
            var grid=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,
                Background=Card,RowBackground=Card,Foreground=Ink,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HeadersVisibility=DataGridHeadersVisibility.Column};
            var style=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));style.Setters.Add(new Setter(Control.BackgroundProperty,Card));style.Setters.Add(new Setter(Control.ForegroundProperty,Ink));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(6)));grid.ColumnHeaderStyle=style;return grid;
        }
        private static DataGridTextColumn Column(string header,string field,double width)
        {
            var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));style.Setters.Add(new Setter(TextBlock.MarginProperty,new Thickness(5,4,5,4)));
            return new DataGridTextColumn {Header=header,Binding=new WpfBinding(field),Width=width,ElementStyle=style};
        }
        private void Run(Action action){try {action();}catch(Exception ex){Diagnostics.Log("Full Scan setup",ex);_status.Text=ex.Message;if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_SCANESTIMATE")=="1")throw;}}
        private void ApplyProfile()
        {
            if(_initializing)return;_initializing=true;bool quick=SelectedProfile==ScanProfile.QuickCoordination,advanced=SelectedProfile==ScanProfile.Advanced;
            _host.IsChecked=true;_hostLinks.IsChecked=true;_inside.IsChecked=false;_cross.IsChecked=false;_insulation.IsChecked=false;
            _host.IsEnabled=!quick;_hostLinks.IsEnabled=!quick;_inside.IsEnabled=!quick;_mode.SelectedIndex=quick||advanced?0:1;
            _generic.IsChecked=false;_supports.IsChecked=true;_structure.IsChecked=true;_joints.IsChecked=true;
            _advancedRules.Visibility=advanced?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;_advancedJobs.Visibility=advanced?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
            if(quick&&_floorList.SelectedItems.Count>1){var first=_floorList.SelectedItems[0];_floorList.SelectedItems.Clear();_floorList.SelectedItem=first;}
            _floorList.SelectionMode=quick?SelectionMode.Single:SelectionMode.Extended;
            if(!advanced&&_scope.SelectedIndex==2)_scope.SelectedIndex=0;
            _profileInfo.Text=quick?"Hard clashes · host and selected loaded links · one floor or scoped zones · standard discipline pairs · generic models and insulation excluded":
                advanced?"Explicit categories, disciplines and relationships. Link-to-link and insulation run as separate reviewed jobs.":
                "Hard clashes plus selected clearance rules · selected floors/zones · optional scans inside selected links";
            _excluded.Clear();_initializing=false;UpdateScope();UpdateTests();Invalidate();
        }
        private void UpdateScope()
        {
            _floorList.IsEnabled=_scope.SelectedIndex==0;_zones.IsEnabled=_scope.SelectedIndex==1;
        }
        private void UpdateTests(){_ruleList.IsEnabled=SelectedProfile!=ScanProfile.QuickCoordination&&(SelectedProfile==ScanProfile.DetailedCoordination||_mode.SelectedIndex==1||_insulation.IsChecked==true);_mode.IsEnabled=_insulation.IsChecked!=true;}
        private void JobChanged(CheckBox? enabled)
        {
            if(_initializing)return;_initializing=true;
            if(enabled==_cross)_insulation.IsChecked=false;
            if(enabled==_insulation)_cross.IsChecked=false;
            bool cross=_cross.IsChecked==true;
            if(cross){_host.IsChecked=false;_hostLinks.IsChecked=false;_inside.IsChecked=false;}
            else if(_host.IsChecked!=true&&_hostLinks.IsChecked!=true){_host.IsChecked=true;_hostLinks.IsChecked=true;}
            _host.IsEnabled=!cross;_hostLinks.IsEnabled=!cross;_inside.IsEnabled=!cross;
            if(_insulation.IsChecked==true)_mode.SelectedIndex=1;
            _initializing=false;UpdateTests();Invalidate();
        }
        private ScanPlan Basis()
        {
            _settings.FullScanRuleSetName=_ruleSet.SelectedItem as string??_settings.FullScanSnapshot().RuleSetName;
            double Read(TextBox box){if(!double.TryParse(box.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)||double.IsNaN(value)||double.IsInfinity(value)||value<0)throw new ArgumentException("Enter non-negative finite overlap and clearance values.");return value;}
            _settings.FullScanMinimumOverlapMM3=Read(_overlap);_settings.FullScanInsulationMM=Read(_allowance);_settings.FullScanMaintenanceMM=Read(_maintenance);
            _settings.FullScanIncludeStructural=_structure.IsChecked==true;_settings.FullScanExcludeConnectedJoints=_joints.IsChecked==true;
            return RevitScanPlanFactory.Create(_doc,_settings,_links.Where(l=>l.IsLoaded),true,selectedLinks:_linkChoices.Where(c=>c.Item2.IsChecked==true).Select(c=>c.Item1.Id));
        }
        private void FillRules()
        {
            if(_ruleSet==null||_ruleList==null)return;
            var name=_ruleSet.SelectedItem as string??_settings.FullScanSnapshot().RuleSetName;var ruleSet=JsonConvert.DeserializeObject<RuleSet>(ScanSessionCache.Rules(_doc,name).SnapshotContent)!;
            _rules.Clear();_ruleList.Children.Clear();
            foreach(var item in ruleSet.Rules.Select(p=>Tuple.Create(p.Key,p.Value)).Concat(ruleSet.DisciplinePairs.Select(p=>Tuple.Create("DiscPair:"+p.Key,p.Value))).Concat(ruleSet.CategoryPairs.Select(p=>Tuple.Create("CatPair:"+p.Key,p.Value))).Where(p=>!p.Item2.Ignore&&!p.Item2.Severity.Equals("Ignore",StringComparison.OrdinalIgnoreCase))) {
                var check=Flag(_ruleList,item.Item1+" — "+item.Item2.ClearanceMM.ToString("0.#")+" mm",true);_rules.Add(Tuple.Create(item.Item1,check));
            }
        }
        internal ScanPlan BuildPlan()
        {
            if(_scope.SelectedIndex==2&&SelectedProfile!=ScanProfile.Advanced)throw new ArgumentException("Entire-model scope is available only in Advanced.");
            var discs=_disciplines.Where(d=>d.Item2.IsChecked==true).Select(d=>d.Item1).ToList();var cats=_categories.Where(c=>c.Item2.IsChecked==true).Select(c=>c.Item1).ToList();
            var selection=new ScanProfileSelection {Profile=SelectedProfile,Stage=_cross.IsChecked==true?ScanStage.LinkToLink:_insulation.IsChecked==true?ScanStage.InsulationClearance:ScanStage.Coordination,
                Mode=_mode.SelectedIndex==0?ScanMode.HardOnly:ScanMode.HardAndClearance,HostToHost=_host.IsChecked==true,HostToLinks=_hostLinks.IsChecked==true,InsideLinks=_inside.IsChecked==true,
                IncludeGenericModels=_generic.IsChecked==true,ExcludeSupports=_supports.IsChecked==true,
                CoordinationRadiusMM=double.Parse(_areaRadius.Text,System.Globalization.CultureInfo.InvariantCulture),
                Links=_linkChoices.Where(c=>c.Item2.IsChecked==true).Select(c=>c.Item1.Id).ToList(),LinkPairs=_selectedPairs.ToList(),
                LevelIds=_scope.SelectedIndex==0?_floorList.SelectedItems.Cast<ScanFloor>().Select(f=>f.Id).ToList():new List<string>(),
                Zones=_scope.SelectedIndex==1?_zones.SelectedItems.Cast<ScanZone>().ToList():new List<ScanZone>(),
                Disciplines=discs,Categories=cats,RestrictDisciplines=discs.Count!=_disciplines.Count,RestrictCategories=cats.Count!=_categories.Count,
                ClearanceRules=_rules.Where(r=>r.Item2.IsChecked==true).Select(r=>r.Item1).ToList(),ExcludedRelationships=_excluded.ToList()};
            return ScanProfiles.Create(Basis(),selection);
        }
        private void AddPair()
        {
            if(!(_pairA.SelectedItem is Choice a)||!(_pairB.SelectedItem is Choice b)||a.Id==b.Id)throw new ArgumentException("Choose two different loaded links.");
            if(_selectedPairs.Any(p=>p.Matches(a.Id,b.Id)))return;
            _selectedPairs.Add(new ScanModelPair(a.Id,b.Id));_pairList.Items.Add(a.Name+" ↔ "+b.Name);Invalidate();
        }
        private void AddZone()
        {
            var worksets=_doc.IsWorkshared?new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Select(w=>w.Name).ToArray():Array.Empty<string>();
            var editor=new ScanZoneEditor(_floors,worksets,_links.Where(l=>l.IsLoaded).Select(l=>l.FileName).Distinct()) {Owner=this};
            if(editor.ShowDialog()==true){_zones.Items.Add(editor.Result!);_zones.SelectedItems.Add(editor.Result!);_scope.SelectedIndex=1;Invalidate();}
        }
        private void ReviewClassification()
        {
            CancelEstimate();var plan=BuildPlan();var review=new ClassificationReviewWindow(plan,new ProjectClassificationStore(),()=>RevitClassificationInventory.Collect(_doc,plan).GetEnumerator(),()=>ScanCoordinator.MappingsChanged(DocumentSession.Key(_doc))) {Owner=this};
            review.ShowDialog();Invalidate();
        }
        private void Invalidate()
        {
            if(_initializing)return;CancelEstimate();_estimate=null;_estimatedPlan=null;ApprovedPlan=null;_start.IsEnabled=false;
            _summary.Text="Configuration changed. Displayed rows describe the previous estimate; recalculate to update totals.";
            _status.Text="Configuration changed. Recalculate the estimate before starting.";
        }
        private void CancelEstimate(){_timer?.Stop();_job?.Dispose();_job=null;if(_cancelEstimate!=null)_cancelEstimate.IsEnabled=false;if(_calculate!=null)_calculate.IsEnabled=true;}
        internal void CalculateEstimate()
        {
            CancelEstimate();_estimatedPlan=BuildPlan();_estimate=null;_start.IsEnabled=false;_tabs.SelectedIndex=3;
            _job=_estimateFactory(_estimatedPlan);_calculate.IsEnabled=false;_cancelEstimate.IsEnabled=true;_timer.Start();
        }
        private void AdvanceEstimate()
        {
            if(_advancing||_job==null)return;_advancing=true;
            try {
                bool done=_job.Advance(25);_status.Text=_job.Progress;if(!done)return;
                _estimate=_job.Result!;_job.Dispose();_job=null;_timer.Stop();_calculate.IsEnabled=true;_cancelEstimate.IsEnabled=false;
                _models.ItemsSource=_estimate.Models;
                var previous=(_workload.ItemsSource as IEnumerable<WorkloadPairRow>)?.ToList()??new List<WorkloadPairRow>();
                var rows=_estimate.Pairs.Select(p=>new WorkloadPairRow {Pair=p.Pair,Label=p.Label,Relationships=p.PossibleRelationships.ToString("N0"),Candidates=p.CandidateRange,Duration=p.Duration,Confidence=p.Confidence}).ToList();
                rows.AddRange(previous.Where(r=>_excluded.Any(e=>e.Matches(r.Pair.Source,r.Pair.Target))));
                foreach(var row in rows){row.InclusionChanged=null;row.Included=!_excluded.Any(p=>p.Matches(row.Pair.Source,row.Pair.Target));row.InclusionChanged=changed=>{if(changed.Included)_excluded.RemoveAll(p=>p.Matches(changed.Pair.Source,changed.Pair.Target));else if(!_excluded.Any(p=>p.Matches(changed.Pair.Source,changed.Pair.Target)))_excluded.Add(changed.Pair);Invalidate();};}
                _workload.ItemsSource=rows;
                string scope=_estimatedPlan!.WholeModelScope?"Entire model":string.Join(", ",_estimatedPlan.LevelIds.Select(id=>_floors.First(f=>f.Id==id).Name).Concat(_estimatedPlan.Zones.Select(z=>z.Name)));
                _summary.Text=$"Loaded links: {_estimate.Models.Count(m=>m.Id!=""&&m.Loaded)} · selected links: {_estimatedPlan.SelectedLinkInstanceIds.Count}\nScope: {scope}\nSource elements: {_estimate.SourceElements:N0} · candidate discipline pairs: {_estimate.DisciplinePairs.Count}\nPossible source relationships: {_estimate.PossibleRelationships:N0}\nEstimated bounding-box pairs: {_estimate.Pairs.Sum(p=>p.BoundingBoxLow):N0}–{_estimate.Pairs.Sum(p=>p.BoundingBoxHigh):N0}\nEstimated duration: {_estimate.Duration} ({_estimate.CalibrationScans} comparable scans)\n"+string.Join(", ",_estimate.DisciplinePairs);
                _warnings.Text=string.Join("\n",_estimate.Warnings)+"\nUncheck expensive model relationships below, then recalculate. Bounding-box candidates are not confirmed clashes.";
                bool clearance=SelectedProfile!=ScanProfile.DetailedCoordination||_estimate.Pairs.Any(p=>p.ClearancePartitions>0);
                _start.IsEnabled=_estimate.SourceElements>0&&clearance;_status.Text=_start.IsEnabled?"Estimate ready. Review the exact relationships before starting.":!clearance?"Selected clearance rules do not apply to these elements. Adjust the rules or use Quick.":"No eligible sources in this scope. Adjust selection or review classification.";
            } catch(Exception ex){CancelEstimate();_estimate=null;_start.IsEnabled=false;_status.Text=ex.Message;Diagnostics.Log("Scan estimate failed",ex);if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_SCANESTIMATE")=="1")throw;}
            finally {_advancing=false;}
        }
        internal bool AcceptEstimate(bool close)
        {
            if(!CanStart||_estimate==null||_estimatedPlan==null)throw new InvalidOperationException("Calculate and review a valid estimate first.");
            var current=BuildPlan();
            if(ScanProfiles.Fingerprint(current)!=_estimate.PlanFingerprint||RevitScanEstimate.Environment(_doc,current)!=_estimate.EnvironmentFingerprint)
            {Invalidate();throw new InvalidOperationException("The estimate expired because model or configuration inputs changed. Recalculate before starting.");}
            ApprovedPlan=_estimatedPlan;if(close)DialogResult=true;return true;
        }
        internal static void SaveFullPreferences(AppSettings settings,ScanPlan plan)
        {
            settings.FullScanRuleSetName=plan.RuleSetName;settings.FullScanMode=plan.Mode;settings.ScanHostToHost=plan.HostToHost;settings.ScanHostToLinks=plan.HostToLinks;
            settings.FullScanWithinLinks=plan.InsideLinks;settings.IncludeLinkToLink=plan.LinkToLink;settings.FullScanLinkedModels=plan.SelectedLinkInstanceIds.Count>0;
            settings.FullScanIncludeGenericModels=plan.CandidatePolicy.IncludeGenericModels;settings.FullScanIncludeInsulation=plan.CandidatePolicy.IncludeInsulation;
            settings.FullScanExcludeNamedSupports=plan.CandidatePolicy.ExcludeNamedSupports;settings.FullScanIncludeStructural=plan.CandidatePolicy.IncludeStructural;
            settings.FullScanExcludeConnectedJoints=plan.ExcludeConnectedJoints;settings.FullScanMinimumOverlapMM3=plan.MinimumOverlapMM3;
            settings.FullScanInsulationMM=plan.InsulationMM;settings.FullScanMaintenanceMM=plan.MaintenanceMM;
        }
    }
    internal sealed class ScanZoneEditor : Window
    {
        internal ScanZone? Result { get; private set; }
        internal ScanZoneEditor(IEnumerable<ScanFloor> floors,IEnumerable<string> worksets,IEnumerable<string> links)
        {
            Title="Coordination zone";Width=460;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var panel=new StackPanel {Margin=new Thickness(18)};Content=panel;
            TextBox Field(string label){panel.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,8,0,4)});var box=new TextBox();panel.Children.Add(box);return box;}
            var name=Field("Zone name");name.Text="Coordination zone";
            ComboBox Select(string label,IEnumerable<string> values){panel.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,8,0,4)});var choice=new ComboBox {ItemsSource=new[]{""}.Concat(values).ToArray(),SelectedIndex=0};panel.Children.Add(choice);return choice;}
            var level=Select("Floor (optional)",floors.Select(f=>f.Name));var workset=Select("Workset (optional)",worksets);var link=Select("Linked model (optional)",links);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};panel.Children.Add(buttons);
            buttons.Children.Add(new Button {Content="Cancel",IsCancel=true,Padding=new Thickness(12,5,12,5),Margin=new Thickness(4)});
            var add=new Button {Content="Add zone",IsDefault=true,Padding=new Thickness(12,5,12,5),Margin=new Thickness(4)};buttons.Children.Add(add);
            add.Click+=(_,__)=>{if(string.IsNullOrWhiteSpace(name.Text)||((string)level.SelectedItem==""&&(string)workset.SelectedItem=="")){MessageBox.Show(this,"Name the zone and select a floor or workset.");return;}Result=new ScanZone(new CoordinationZone {Name=name.Text,LevelName=(string)level.SelectedItem,WorksetName=(string)workset.SelectedItem,LinkedModel=(string)link.SelectedItem});DialogResult=true;};
        }
    }
}
