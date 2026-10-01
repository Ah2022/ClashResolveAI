// Dashboard/ClashDashboard.cs  — v8.0
//
// FIXES v8.0:
//   BUG FIX: BuildUI() was calling BuildHeader() twice — first call had
//   DockPanel.SetDock applied but the reference was immediately discarded
//   (never added to the panel). Second call created a fresh header WITHOUT
//   DockPanel positioning, so it rendered with default Dock.Left instead
//   of Dock.Top. Fixed: capture reference once, set dock, then add.
//
//   DASHBOARD REDESIGN:
//   - Professional dark-teal theme with accent bars per severity
//   - ColumnHeaderStyle restored (was incorrectly commented out)
//   - Color-coded severity pill column using DataGridTemplateColumn
//   - Color-coded row backgrounds via DataGrid.RowStyle + triggers
//   - KPI stat cards with left-side accent bars and bold numbers
//   - Detail panel renders all fields (PropRow no longer skips empty)
//   - Grid references now meaningful (GetGrid fix in ClashEngine)

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

        public IReadOnlyList<ClashResult> Clashes => _clashes.AsReadOnly();
        public IReadOnlyList<ClashGroup>  Groups  => _groups.AsReadOnly();

        public void AddClashes(IEnumerable<ClashResult> clashes)
        {
            var incoming=clashes.Where(c=>c.Origin==ResultOrigin.Full).ToList();
            ClashDatabase.Instance.RestoreLifecycles(incoming);
            var map=_clashes.ToDictionary(c=>c.NormalizedKey);
            foreach(var c in incoming)map[c.NormalizedKey]=c;
            _clashes.Clear();_clashes.AddRange(map.Values);
            ClashDatabase.Instance.BulkInsertClashes(incoming);
            Publish();
        }
        public ResultViewFilter ViewFilter { get; private set; }=new ResultViewFilter { Types=ResultViewFilter.Defaults(AppSettings.Load().FullScanMode) };
        private ScanMode? _viewMode;
        public List<ClashResult> GetVisibleClashes()=>_clashes.Where(ViewFilter.Matches).OrderBy(c=>(int)c.Severity).ThenByDescending(c=>c.OverlapVolumeMM3).ToList();
        public void RestoreSession(IEnumerable<ClashResult> rows,ResultViewFilter filter)
        { _clashes.Clear();_clashes.AddRange(rows.Where(c=>c.Origin==ResultOrigin.Full));ViewFilter=filter.Copy();_viewMode=null;Publish(); }
        public int MergeFullScan(List<ClashResult> found,ScanMode mode,ScanScope scope)
        {
            if(_viewMode!=mode){ViewFilter.Types=ResultViewFilter.Defaults(mode);_viewMode=mode;}
            return Merge(found.Where(c=>c.Origin==ResultOrigin.Full).ToList(),mode,scope.Contains);
        }
        private int Merge(List<ClashResult> found,ScanMode mode,Func<ClashResult,bool> scope)
        {
            ClashDatabase.Instance.RestoreLifecycles(found);
            var merged=ResultLifecycle.Merge(_clashes,found,mode,scope);
            // A verified clear pair also has current geometry. Without this stamp,
            // its retained Resolved row would permanently block filtered reports.
            foreach(var row in merged.Rows.Where(c=>c.Status==ClashStatus.Resolved&&ResultLifecycle.Evaluated(mode,c)&&scope(c))){
                row.GeometryRevision=ScanCoordinator.Revision;if(!merged.Touched.Contains(row))merged.Touched.Add(row);
            }
            ClashDatabase.Instance.BulkInsertClashes(merged.Touched);
            _clashes.Clear();_clashes.AddRange(merged.Rows);Publish();return merged.RetainedClearance;
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
            if(MessageBox.Show($"Purge {count} non-hard results from this document's current list and database? This includes clearance and all unverified rows, including possible hard. Saved history for these rows is removed. Future scans may find them again.",
                "Purge non-hard results",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes)PurgeNonHard();
        }
        public void UpdateClashStatus(string clashId,ClashStatus status,string author="",string comment="")
        {
            var c=_clashes.FirstOrDefault(x=>x.ClashId==clashId);
            if(c!=null)c.Status=status;
            ClashDatabase.Instance.UpdateClashStatus(clashId,status,author,comment);
            Publish();
        }
        public void UpdateStatuses(IEnumerable<ClashResult> items,ClashStatus status)
        {
            var list=items.ToList();foreach(var c in list)c.Status=status;
            ClashDatabase.Instance.BulkInsertClashes(list);Publish();
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
        private void RefreshWindow()
        {
            if(_refreshQueued)return;_refreshQueued=true;
            var dispatcher = App.UIDispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => {_refreshQueued=false;_window?.Refresh(_clashes, _groups);}));
        }

        public void ShowWindow()
        {
            if (_window == null || !_window.IsVisible)
            {
                _window = new DashboardWindow(_clashes, _groups, Show2DAction, Show3DAction);
                _window.Show();
            }
            else
            {
                _window.Activate();
            }
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
            return new DashboardStats
            {
                TotalClashes            = _clashes.Count,
                Critical                = _clashes.Count(c => c.Priority == "Critical"),
                Hard                    = _clashes.Count(c => c.Severity == ClashSeverity.Hard),
                Soft                    = _clashes.Count(c => c.Severity == ClashSeverity.Soft),
                ClearanceOnly           = _clashes.Count(c => c.Severity == ClashSeverity.Clearance),
                Resolved                = _clashes.Count(c => c.Status   == ClashStatus.Resolved),
                Open                    = _clashes.Count(c => c.Status   <= ClashStatus.InReview),
                GroupCount              = _groups.Count,
                CoordinationHealthScore = db.GetHealthScore(),
                LastScan                = DateTime.Now,
                RecentClashes           = _clashes.Take(20).ToList(),
                ActiveGroups            = _groups.Take(10).ToList(),
                Trends                  = db.GetWeeklyTrends()
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DASHBOARD WINDOW  — redesigned v8.0
    // ══════════════════════════════════════════════════════════════════════

    public class DashboardWindow : Window
    {
        // ── Theme ──────────────────────────────────────────────────────────
        // Dark navy base with teal accents — professional BIM tool aesthetic
        private static readonly WpfBrush BgMain    = B(14,  18,  30);
        private static readonly WpfBrush BgPanel   = B(20,  26,  44);
        private static readonly WpfBrush BgCard    = B(28,  35,  56);
        private static readonly WpfBrush BgCardHov = B(36,  44,  68);
        private static readonly WpfBrush BgStrip   = B(18,  24,  40);
        private static readonly WpfBrush BgRow     = B(24,  31,  50);
        private static readonly WpfBrush BgRowSel  = B(40,  80, 130);
        private static readonly WpfBrush AccCrit   = B(220,  50,  50);
        private static readonly WpfBrush AccHard   = B(220, 110,  30);
        private static readonly WpfBrush AccSoft   = B(220, 180,  20);
        private static readonly WpfBrush AccClear  = B( 60, 160, 200);
        private static readonly WpfBrush AccTeal   = B( 32, 178, 170);
        private static readonly WpfBrush AccGreen  = B( 39, 174,  96);
        private static readonly WpfBrush AccPurple = B(130,  80, 200);
        private static readonly WpfBrush TextMain  = B(230, 235, 245);
        private static readonly WpfBrush TextSub   = B(160, 170, 190);
        private static readonly WpfBrush TextMute  = B(100, 110, 130);
        private static readonly WpfBrush BorderCol = B( 38,  48,  72);
        private static readonly WpfBrush DividerCol= B( 30,  38,  60);

        // ── Data ───────────────────────────────────────────────────────────
        private List<ClashResult> _allClashes;
        private List<ClashGroup>  _allGroups;
        private List<ClashResult> _displayList;

        // ── Actions ────────────────────────────────────────────────────────
        private readonly Action<ClashResult>? _show2D;
        private readonly Action<ClashResult>? _show3D;

        // ── Stats text blocks ──────────────────────────────────────────────
        private TextBlock _tbTotal = null!, _tbCrit = null!, _tbHard  = null!;
        private TextBlock _tbSoft  = null!, _tbClear= null!, _tbHealth= null!;

        // ── Filter controls ────────────────────────────────────────────────
        private TextBox  _searchBox  = null!;
        private ComboBox _cbDisc     = null!;
        private ComboBox _cbSysType  = null!;
        private ComboBox _cbCategory = null!;
        private ComboBox _cbSeverity = null!;
        private ComboBox _cbStatus   = null!;
        private ComboBox _cbLevel    = null!;
        private readonly Dictionary<ResultTypes,CheckBox> _resultChecks=new Dictionary<ResultTypes,CheckBox>();
        private bool _syncingTypes;

        // ── Grid & detail ──────────────────────────────────────────────────
        private DataGrid   _grid        = null!;
        private StackPanel _detailProps = null!;
        private TextBlock  _detailTitle = null!;
        private TextBlock  _detailSubtitle = null!;

        // ── Filter state ───────────────────────────────────────────────────
        private string _fSearch="", _fDisc="", _fSys="", _fCat="";
        private string _fSev="",    _fStat="", _fLvl="";

        // ══════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════════

        public DashboardWindow(
            List<ClashResult> clashes, List<ClashGroup> groups,
            Action<ClashResult>? show2D = null, Action<ClashResult>? show3D = null)
        {
            _allClashes  = clashes ?? new List<ClashResult>();
            _allGroups   = groups  ?? new List<ClashGroup>();
            _displayList = new List<ClashResult>(_allClashes);
            _show2D      = show2D;
            _show3D      = show3D;

            Title  = "ClashResolve AI v8.0  ·  MEP Coordination Dashboard";
            Width  = 1400;
            Height = 860;
            MinWidth  = 1000;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BgMain;
            FontFamily = new FontFamily("Segoe UI");

            BuildUI();
            PopulateAllFilters();
            SyncControls();
            ApplyFilters();
        }

        // ══════════════════════════════════════════════════════════════════
        //  BUILD UI — 4-Zone Layout
        //
        //  FIX v8.0: BuildHeader() called ONCE. Reference captured, dock
        //  property set on that reference, then added to panel.
        //  Previous version discarded the SetDock result and added a
        //  fresh instance without dock positioning → header at Dock.Left.
        // ══════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            var dock = new DockPanel { Background = BgMain };

            // Zone 1 — Header  (FIX: single instance, dock set before add)
            var header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // Zone 2 — Filter Strip
            var strip = BuildFilterStrip();
            DockPanel.SetDock(strip, Dock.Top);
            dock.Children.Add(strip);

            // Zone 3+4 — Clash list + Detail panel (fills remaining space)
            dock.Children.Add(BuildMainArea());

            Content = dock;
        }

        // ══════════════════════════════════════════════════════════════════
        //  ZONE 1: HEADER
        // ══════════════════════════════════════════════════════════════════

        private FrameworkElement BuildHeader()
        {
            var root = new Border
            {
                Background      = BgPanel,
                BorderBrush     = BorderCol,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(20, 12, 20, 12)
            };

            var outer = new DockPanel();

            // ── Left: branding block ──────────────────────────────────────
            var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock
            {
                Text       = "ClashResolve AI",
                FontSize   = 18,
                FontWeight = FontWeights.Bold,
                Foreground = AccTeal
            });
            brand.Children.Add(new TextBlock
            {
                Text       = "MEP Coordination Dashboard  ·  v8.0",
                FontSize   = 10,
                Foreground = TextMute,
                Margin     = new Thickness(0, 1, 0, 0)
            });
            DockPanel.SetDock(brand, Dock.Left);
            outer.Children.Add(brand);

            // ── Right: KPI tiles + toolbar ────────────────────────────────
            var right = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _tbTotal  = KpiValue("—");
            _tbCrit   = KpiValue("—");
            _tbHard   = KpiValue("—");
            _tbSoft   = KpiValue("—");
            _tbClear  = KpiValue("—");
            _tbHealth = KpiValue("—");

            right.Children.Add(KpiCard("TOTAL",     _tbTotal,  AccTeal));
            right.Children.Add(KpiCard("CRITICAL",  _tbCrit,   AccCrit));
            right.Children.Add(KpiCard("HARD",      _tbHard,   AccHard));
            right.Children.Add(KpiCard("SOFT",      _tbSoft,   AccSoft));
            right.Children.Add(KpiCard("CLEARANCE", _tbClear,  AccClear));
            right.Children.Add(KpiCard("HEALTH",    _tbHealth, AccGreen));

            // Divider
            right.Children.Add(new Border
            {
                Width           = 1,
                Background      = BorderCol,
                Margin          = new Thickness(14, 6, 14, 6),
                VerticalAlignment = VerticalAlignment.Stretch
            });

            // Toolbar buttons
            right.Children.Add(TbBtn("↑  Export BCF",    AccTeal,    () => ExportBcf()));
            right.Children.Add(TbBtn("↑  Export Excel",  AccGreen,   () => ExportExcel()));
            right.Children.Add(TbBtn("✓  Resolve All",   AccGreen,   () => ResolveAll()));
            right.Children.Add(TbBtn("✕  Close",         B(55,62,82),() => Close()));

            DockPanel.SetDock(right, Dock.Right);
            outer.Children.Add(right);

            root.Child = outer;
            return root;
        }

        // ══════════════════════════════════════════════════════════════════
        //  ZONE 2: FILTER STRIP
        // ══════════════════════════════════════════════════════════════════

        private FrameworkElement BuildFilterStrip()
        {
            var root = new Border
            {
                Background      = BgStrip,
                BorderBrush     = BorderCol,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(16, 8, 16, 8)
            };

            var row = new WrapPanel { Orientation = Orientation.Horizontal };

            // Search
            _searchBox = new TextBox
            {
                Width = 210, Height = 27,
                Background = BgCard, Foreground = TextMain,
                BorderBrush = BorderCol, BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Text = "Search IDs, systems, levels…"
            };
            _searchBox.GotFocus  += (s,e) => { if (_searchBox.Text == "Search IDs, systems, levels…") _searchBox.Text = ""; };
            _searchBox.LostFocus += (s,e) => { if (string.IsNullOrWhiteSpace(_searchBox.Text)) _searchBox.Text = "Search IDs, systems, levels…"; };
            row.Children.Add(_searchBox);

            // Discipline
            _cbDisc = FCombo(170); row.Children.Add(FLabel("Discipline")); row.Children.Add(_cbDisc);
            // System Type
            _cbSysType = FCombo(170); row.Children.Add(FLabel("System Type")); row.Children.Add(_cbSysType);
            // Category
            _cbCategory = FCombo(140); row.Children.Add(FLabel("Category")); row.Children.Add(_cbCategory);
            // Severity
            _cbSeverity = FCombo(120);
            foreach (var s in new[]{"All Severities","Critical","Hard","Soft","Clearance"})
                _cbSeverity.Items.Add(s);
            _cbSeverity.SelectedIndex = 0;
            row.Children.Add(FLabel("Severity")); row.Children.Add(_cbSeverity);
            // Status
            _cbStatus = FCombo(110);
            foreach (var s in new[]{"All Status","New","Active","In Review","Approved","Resolved","Ignored"})
                _cbStatus.Items.Add(s);
            _cbStatus.SelectedIndex = 0;
            row.Children.Add(FLabel("Status")); row.Children.Add(_cbStatus);
            // Level
            _cbLevel = FCombo(155); row.Children.Add(FLabel("Level")); row.Children.Add(_cbLevel);

            // SEARCH / CLEAR
            row.Children.Add(FBtn("  SEARCH  ", AccTeal,      () => { ReadFilterState(); ApplyFilters(); }));
            row.Children.Add(FBtn("  CLEAR  ",  B(50,58,80),  () => { ClearFilters();    ApplyFilters(); }));

            foreach(var item in new[]{(ResultTypes.Hard,"Hard"),(ResultTypes.PossibleHard,"Possible hard"),(ResultTypes.Clearance,"Clearance"),(ResultTypes.Unverified,"Unverified")}){
                var type=item.Item1;var check=new CheckBox { Content=item.Item2,Foreground=TextMain,Margin=new Thickness(10,6,0,0),IsChecked=(ClashDashboard.Instance.ViewFilter.Types&type)!=0 };
                check.Checked+=(_,__)=>ChangeTypes();check.Unchecked+=(_,__)=>ChangeTypes();_resultChecks[type]=check;row.Children.Add(check);
            }
            row.Children.Add(FBtn("Purge non-hard results",B(90,45,45),()=>ClashDashboard.Instance.ConfirmPurgeNonHard()));
            root.Child = row;
            return root;
        }

        // ══════════════════════════════════════════════════════════════════
        //  ZONES 3 + 4: MAIN AREA (clash grid + detail panel)
        // ══════════════════════════════════════════════════════════════════

        private FrameworkElement BuildMainArea()
        {
            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });

            var gridBorder = new Border { Background = BgPanel };
            gridBorder.Child = BuildClashGrid();
            WpfGrid.SetColumn(gridBorder, 0);
            g.Children.Add(gridBorder);

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background          = DividerCol,
                Width               = 5
            };
            WpfGrid.SetColumn(splitter, 1);
            g.Children.Add(splitter);

            var detailBorder = new Border { Background = BgPanel };
            detailBorder.Child = BuildDetailPanel();
            WpfGrid.SetColumn(detailBorder, 2);
            g.Children.Add(detailBorder);

            return g;
        }

        // ── Zone 3: Clash DataGrid ─────────────────────────────────────────

        private FrameworkElement BuildClashGrid()
        {
            // ── DataGrid ──────────────────────────────────────────────────
            _grid = new DataGrid
            {
                AutoGenerateColumns      = false,
                CanUserAddRows           = false,
                CanUserDeleteRows        = false,
                IsReadOnly               = true,
                SelectionMode            = DataGridSelectionMode.Single,
                GridLinesVisibility      = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = DividerCol,
                AlternatingRowBackground = BgRow,
                Background               = BgPanel,
                Foreground               = TextMain,
                BorderThickness          = new Thickness(0),
                RowHeight                = 30,
                FontSize                 = 11,
                ColumnHeaderHeight       = 34,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
            };

            // ── Column header style (FIX: was incorrectly commented out) ──
            var hdrStyle = new Style(typeof(DataGridColumnHeader));
            hdrStyle.Setters.Add(new Setter(BackgroundProperty,               BgCard));
            hdrStyle.Setters.Add(new Setter(ForegroundProperty,               TextSub));
            hdrStyle.Setters.Add(new Setter(FontWeightProperty,               FontWeights.SemiBold));
            hdrStyle.Setters.Add(new Setter(FontSizeProperty,                 10.5));
            hdrStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            hdrStyle.Setters.Add(new Setter(PaddingProperty,                  new Thickness(8, 0, 8, 0)));
            hdrStyle.Setters.Add(new Setter(BorderBrushProperty,              DividerCol));
            hdrStyle.Setters.Add(new Setter(BorderThicknessProperty,          new Thickness(0, 0, 1, 1)));
            _grid.ColumnHeaderStyle = hdrStyle;

            // ── Row style with severity-based background triggers ──────────
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(BackgroundProperty, BgPanel));
            rowStyle.Setters.Add(new Setter(ForegroundProperty, TextMain));

            // Selected row highlight
            var selTrigger = new Trigger
            {
                Property = DataGridRow.IsSelectedProperty,
                Value    = true
            };
            selTrigger.Setters.Add(new Setter(BackgroundProperty, BgRowSel));
            selTrigger.Setters.Add(new Setter(ForegroundProperty, TextMain));
            rowStyle.Triggers.Add(selTrigger);
            _grid.RowStyle = rowStyle;

            // ── Columns ───────────────────────────────────────────────────

            // Severity pill (color-coded template column)
            var sevCol = new DataGridTemplateColumn
            {
                Header      = "Sev.",
                Width       = 64,
                CanUserSort = true,
                SortMemberPath = "Severity"
            };
            var sevTemplate = new DataTemplate();
            var sevFactory  = new FrameworkElementFactory(typeof(Border));
            sevFactory.SetValue(Border.CornerRadiusProperty,       new CornerRadius(3));
            sevFactory.SetValue(Border.MarginProperty,             new Thickness(4, 5, 4, 5));
            sevFactory.SetValue(Border.PaddingProperty,            new Thickness(5, 1, 5, 1));
            sevFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            // Bind background to severity via converter
            var sevBgBinding = new WpfBinding("Severity")
            {
                Converter = new SeverityToBrushConverter()
            };
            sevFactory.SetBinding(Border.BackgroundProperty, sevBgBinding);
            var sevText = new FrameworkElementFactory(typeof(TextBlock));
            sevText.SetBinding(TextBlock.TextProperty,      new WpfBinding("Severity"));
            sevText.SetValue(TextBlock.ForegroundProperty,  TextMain);
            sevText.SetValue(TextBlock.FontSizeProperty,    9.5);
            sevText.SetValue(TextBlock.FontWeightProperty,  FontWeights.SemiBold);
            sevText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            sevFactory.AppendChild(sevText);
            sevTemplate.VisualTree = sevFactory;
            sevCol.CellTemplate    = sevTemplate;
            _grid.Columns.Add(sevCol);

            // Priority
            _grid.Columns.Add(TextCol("Priority",      "Priority",     60));
            // Status
            _grid.Columns.Add(TextCol("Status",        "Status",       72));
            // Disc A
            _grid.Columns.Add(TextCol("Disc. A",       "DisciplineA",  90));
            // System Type A
            _grid.Columns.Add(TextCol("System A",      "SystemTypeA",  150));
            // Category A
            _grid.Columns.Add(TextCol("Cat. A",        "CategoryNameA",110));
            // Disc B
            _grid.Columns.Add(TextCol("Disc. B",       "DisciplineB",  90));
            // System Type B
            _grid.Columns.Add(TextCol("System B",      "SystemTypeB",  150));
            // Category B
            _grid.Columns.Add(TextCol("Cat. B",        "CategoryNameB",110));
            // Level
            _grid.Columns.Add(TextCol("Level",         "LevelName",    100));
            // Grid Ref
            _grid.Columns.Add(TextCol("Grid",          "GridRef",       60));
            // Gap / Overlap
            _grid.Columns.Add(TextCol("Physical result","TestType",115));
            _grid.Columns.Add(TextCol("Gap mm","GapDisplay",68));
            // Overlap Volume
            _grid.Columns.Add(TextCol("Vol mm³",       "OverlapVolumeMM3", 72, "F0"));

            // Action buttons column
            _grid.Columns.Add(BuildActionColumn());

            // Selection → detail panel
            _grid.SelectionChanged += (s, e) =>
            {
                if (_grid.SelectedItem is ClashResult sel) PopulateDetail(sel);
            };
            _grid.MouseDoubleClick += (s, e) =>
            {
                if (_grid.SelectedItem is ClashResult sel) _show2D?.Invoke(sel);
            };

            // ── Section header + scroll wrapper ──────────────────────────
            var wrapper = new DockPanel();

            var sectionHdr = new Border
            {
                Background      = BgCard,
                BorderBrush     = DividerCol,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(16, 7, 16, 7)
            };
            var hdrRow = new DockPanel();
            hdrRow.Children.Add(new TextBlock
            {
                Text       = "CLASH LIST",
                FontSize   = 10,
                FontWeight = FontWeights.Bold,
                Foreground = TextMute,
                VerticalAlignment = VerticalAlignment.Center
            });
            sectionHdr.Child = hdrRow;
            DockPanel.SetDock(sectionHdr, Dock.Top);
            wrapper.Children.Add(sectionHdr);
            wrapper.Children.Add(_grid);
            return wrapper;
        }

        private DataGridTemplateColumn BuildActionColumn()
        {
            var col = new DataGridTemplateColumn
            {
                Header      = "Actions",
                Width       = 120,
                CanUserSort = false
            };

            var tmpl    = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(StackPanel));
            factory.SetValue(StackPanel.OrientationProperty,           Orientation.Horizontal);
            factory.SetValue(StackPanel.HorizontalAlignmentProperty,   HorizontalAlignment.Center);
            factory.SetValue(StackPanel.VerticalAlignmentProperty,     VerticalAlignment.Center);

            factory.AppendChild(GridActionBtn("2D",  AccClear,  (c) => _show2D?.Invoke(c)));
            factory.AppendChild(GridActionBtn("3D",  AccPurple, (c) => _show3D?.Invoke(c)));
            factory.AppendChild(GridActionBtn("✓",   AccGreen,  (c) =>
            {
                ClashDashboard.Instance.UpdateClashStatus(c.ClashId, ClashStatus.Resolved, "User", "Resolved via dashboard");
                ApplyFilters();
            }));

            tmpl.VisualTree = factory;
            col.CellTemplate = tmpl;
            return col;
        }

        private FrameworkElementFactory GridActionBtn(string label, WpfBrush bg, Action<ClashResult> onClick)
        {
            var btn = new FrameworkElementFactory(typeof(Button));
            btn.SetValue(Button.ContentProperty,         label);
            btn.SetValue(Button.BackgroundProperty,      bg);
            btn.SetValue(Button.ForegroundProperty,      TextMain);
            btn.SetValue(Button.BorderThicknessProperty, new Thickness(0));
            btn.SetValue(Button.PaddingProperty,         new Thickness(6, 1, 6, 1));
            btn.SetValue(Button.MarginProperty,          new Thickness(2, 3, 2, 3));
            btn.SetValue(Button.FontSizeProperty,        9.5);
            btn.SetValue(Button.CursorProperty,          System.Windows.Input.Cursors.Hand);
            btn.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if ((s as Button)?.DataContext is ClashResult c) onClick(c);
            }));
            return btn;
        }

        // ── Zone 4: Detail Panel ──────────────────────────────────────────

        private FrameworkElement BuildDetailPanel()
        {
            var dock = new DockPanel();

            // Title bar
            var titleBar = new Border
            {
                Background      = BgCard,
                BorderBrush     = DividerCol,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(14, 10, 14, 10)
            };
            var titleStack = new StackPanel();
            _detailTitle = new TextBlock
            {
                Text         = "Select a clash",
                FontSize     = 12,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = TextMain,
                TextWrapping = TextWrapping.Wrap
            };
            _detailSubtitle = new TextBlock
            {
                Text       = "Click a row in the clash list to inspect it here.",
                FontSize   = 10,
                Foreground = TextMute,
                Margin     = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(_detailTitle);
            titleStack.Children.Add(_detailSubtitle);
            titleBar.Child = titleStack;
            DockPanel.SetDock(titleBar, Dock.Top);
            dock.Children.Add(titleBar);

            // Scroll area for property rows
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _detailProps = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
            scroll.Content = _detailProps;
            dock.Children.Add(scroll);

            return dock;
        }

        // ══════════════════════════════════════════════════════════════════
        //  FILTER LOGIC
        // ══════════════════════════════════════════════════════════════════

        private void PopulateAllFilters()
        {
            _cbDisc.Items.Clear();
            _cbDisc.Items.Add("All Disciplines");
            foreach (var d in _allClashes
                .SelectMany(c => new[]{ c.DisciplineA.ToString(), c.DisciplineB.ToString() })
                .Distinct().OrderBy(x => x))
                _cbDisc.Items.Add(d);
            _cbDisc.SelectedIndex = 0;

            _cbSysType.Items.Clear();
            _cbSysType.Items.Add("All System Types");
            foreach (var s in _allClashes
                .SelectMany(c => new[]{ c.SystemTypeA, c.SystemTypeB })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().OrderBy(x => x))
                _cbSysType.Items.Add(s);
            _cbSysType.SelectedIndex = 0;

            _cbCategory.Items.Clear();
            _cbCategory.Items.Add("All Categories");
            foreach (var c in _allClashes
                .SelectMany(c => new[]{ c.CategoryNameA, c.CategoryNameB })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().OrderBy(x => x))
                _cbCategory.Items.Add(c);
            _cbCategory.SelectedIndex = 0;

            _cbLevel.Items.Clear();
            _cbLevel.Items.Add("All Levels");
            foreach (var l in _allClashes
                .Select(c => c.LevelName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().OrderBy(x => x))
                _cbLevel.Items.Add(l);
            _cbLevel.SelectedIndex = 0;
        }

        private void ReadFilterState()
        {
            _fSearch = _searchBox.Text.Trim();
            if (_fSearch == "Search IDs, systems, levels…") _fSearch = "";
            _fDisc = _cbDisc.SelectedIndex     <= 0 ? "" : _cbDisc.SelectedItem?.ToString()     ?? "";
            _fSys  = _cbSysType.SelectedIndex  <= 0 ? "" : _cbSysType.SelectedItem?.ToString()  ?? "";
            _fCat  = _cbCategory.SelectedIndex <= 0 ? "" : _cbCategory.SelectedItem?.ToString() ?? "";
            _fSev  = _cbSeverity.SelectedIndex <= 0 ? "" : _cbSeverity.SelectedItem?.ToString() ?? "";
            _fStat = _cbStatus.SelectedIndex   <= 0 ? "" : _cbStatus.SelectedItem?.ToString()   ?? "";
            _fLvl  = _cbLevel.SelectedIndex    <= 0 ? "" : _cbLevel.SelectedItem?.ToString()    ?? "";
        }

        private void ChangeTypes()
        {
            if(_syncingTypes)return;
            var types=_resultChecks.Where(x=>x.Value.IsChecked==true).Aggregate(ResultTypes.None,(value,x)=>value|x.Key);
            ClashDashboard.Instance.SetResultTypes(types);ApplyFilters();
        }
        private void SyncControls()
        {
            var f=ClashDashboard.Instance.ViewFilter;
            _fSearch=f.Search;_fDisc=f.Discipline;_fSys=f.System;_fCat=f.Category;_fSev=f.Severity;_fStat=f.Status;_fLvl=f.Level;
            _searchBox.Text=f.Search==""?"Search IDs, systems, levels…":f.Search;
            void Select(ComboBox box,string value){if(value==""){box.SelectedIndex=0;return;}if(!box.Items.Contains(value))box.Items.Add(value);box.SelectedItem=value;}
            Select(_cbDisc,f.Discipline);Select(_cbSysType,f.System);Select(_cbCategory,f.Category);Select(_cbSeverity,f.Severity);Select(_cbStatus,f.Status);Select(_cbLevel,f.Level);
            _syncingTypes=true;foreach(var entry in _resultChecks)entry.Value.IsChecked=(f.Types&entry.Key)!=0;_syncingTypes=false;
        }
        private void ApplyFilters()
        {
            var filter=ClashDashboard.Instance.ViewFilter;
            filter.Search=_fSearch;filter.Discipline=_fDisc;filter.System=_fSys;filter.Category=_fCat;filter.Severity=_fSev;filter.Status=_fStat;filter.Level=_fLvl;
            _displayList=ClashDashboard.Instance.GetVisibleClashes();
            _grid.ItemsSource=null;_grid.ItemsSource=_displayList;RefreshStats(_displayList);
            ClashDashboard.Instance.ViewChanged();
        }

        private void ClearFilters()
        {
            _fSearch = _fDisc = _fSys = _fCat = _fSev = _fStat = _fLvl = "";
            _searchBox.Text        = "Search IDs, systems, levels…";
            _cbDisc.SelectedIndex     = 0;
            _cbSysType.SelectedIndex  = 0;
            _cbCategory.SelectedIndex = 0;
            _cbSeverity.SelectedIndex = 0;
            _cbStatus.SelectedIndex   = 0;
            _cbLevel.SelectedIndex    = 0;
        }

        // ══════════════════════════════════════════════════════════════════
        //  STATS REFRESH
        // ══════════════════════════════════════════════════════════════════

        private void RefreshStats(List<ClashResult> list)
        {
            _tbTotal.Text  = list.Count.ToString();
            _tbCrit.Text   = list.Count(c => c.Priority == "Critical").ToString();
            _tbHard.Text   = list.Count(c => c.Severity == ClashSeverity.Hard).ToString();
            _tbSoft.Text   = list.Count(c => c.Severity == ClashSeverity.Soft).ToString();
            _tbClear.Text  = list.Count(c => c.Severity == ClashSeverity.Clearance).ToString();
            double health  = _allClashes.Count == 0 ? 100.0
                : (double)_allClashes.Count(c => c.Status == ClashStatus.Resolved)
                  / _allClashes.Count * 100.0;
            _tbHealth.Text = $"{health:F0}%";
        }

        // ══════════════════════════════════════════════════════════════════
        //  DETAIL PANEL
        // ══════════════════════════════════════════════════════════════════

        private void PopulateDetail(ClashResult c)
        {
            _detailTitle.Text    = $"Clash {c.ClashId}";
            _detailSubtitle.Text = $"{c.Severity}  ·  Priority: {c.Priority}  ·  {c.Status}";

            // Color subtitle text by severity
            _detailSubtitle.Foreground = SeverityBrush(c.Severity);

            _detailProps.Children.Clear();

            // ── Element A ────────────────────────────────────────────────
            DetailSection("ELEMENT A");
            DetailRow("Discipline",  c.DisciplineA.ToString());
            DetailRow("System",      c.SystemTypeA,  fallback: "—");
            DetailRow("Category",    c.CategoryNameA, fallback: "—");
            DetailRow("Family/Type", c.FamilyTypeA,  fallback: "—");
            DetailRow("Element ID",  (c.ElementAId >= 0 ? c.ElementAId.ToString() : "—"));
            DetailRow("Source",      string.IsNullOrEmpty(c.LinkFileA) ? "Host Model" : c.LinkFileA);

            // ── Element B ────────────────────────────────────────────────
            DetailSection("ELEMENT B");
            DetailRow("Discipline",  c.DisciplineB.ToString());
            DetailRow("System",      c.SystemTypeB,  fallback: "—");
            DetailRow("Category",    c.CategoryNameB, fallback: "—");
            DetailRow("Family/Type", c.FamilyTypeB,  fallback: "—");
            DetailRow("Element ID",  (c.ElementBId >= 0 ? c.ElementBId.ToString() : "—"));
            DetailRow("Source",      string.IsNullOrEmpty(c.LinkFileB) ? "Host Model" : c.LinkFileB);

            // ── Clash Details ────────────────────────────────────────────
            DetailSection("CLASH DETAILS");
            DetailRow("Severity",    c.Severity.ToString(), highlight: SeverityBrush(c.Severity));
            DetailRow("Priority",    c.Priority);
            DetailRow("Overlap Vol", c.OverlapVolumeMM3 > 0 ? $"{c.OverlapVolumeMM3:F1} mm³" : "—");
            DetailRow("Gap",c.GapDisplay);
            DetailRow("Geometry evidence",c.GeometryEvidence);
            DetailRow("Overlap",c.OverlapVolumeMM3.ToString("F3")+" mm³");
            DetailRow("Test Type",   c.TestType.ToString());
            DetailRow("Level",       c.LevelName,    fallback: "—");
            DetailRow("Grid Ref",    c.GridRef,      fallback: "—");
            DetailRow("Location",    c.LocationText, fallback: "—");
            DetailRow("Move",        string.IsNullOrEmpty(c.MovingDiscipline) ? "—" : c.MovingDiscipline + " should relocate");
            DetailRow("Rule",        c.RuleApplied,  fallback: "—");
            DetailRow("Detected",    c.DetectedAt.ToString("dd MMM yyyy  HH:mm"));

            // ── Status ───────────────────────────────────────────────────
            DetailSection("STATUS & ASSIGNMENT");
            DetailRow("Status",      c.Status.ToString());
            DetailRow("Assigned To", c.Metadata?.AssignedEngineer ?? "Unassigned");
            DetailRow("Comments",    c.Metadata?.Comments ?? "—");

            // ── Action Buttons ───────────────────────────────────────────
            _detailProps.Children.Add(new Border { Height = 10 });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(14, 0, 14, 8)
            };
            btnRow.Children.Add(DetailBtn("👁  Show 2D", AccClear,  () => _show2D?.Invoke(c)));
            btnRow.Children.Add(DetailBtn("🎲  Show 3D", AccPurple, () => _show3D?.Invoke(c)));
            btnRow.Children.Add(DetailBtn("✓  Resolve",  AccGreen,  () =>
            {
                ClashDashboard.Instance.UpdateClashStatus(c.ClashId, ClashStatus.Resolved, "User", "");
                ApplyFilters();
            }));
            _detailProps.Children.Add(btnRow);
        }

        // ══════════════════════════════════════════════════════════════════
        //  PUBLIC REFRESH
        // ══════════════════════════════════════════════════════════════════

        public void Refresh(List<ClashResult> clashes, List<ClashGroup> groups)
        {
            _allClashes = clashes ?? new List<ClashResult>();
            _allGroups  = groups  ?? new List<ClashGroup>();
            PopulateAllFilters();
            SyncControls();
            ApplyFilters();
        }

        // ══════════════════════════════════════════════════════════════════
        //  TOOLBAR ACTIONS
        // ══════════════════════════════════════════════════════════════════

        private void ExportBcf()
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                    { Description = "Select output folder for BCF export" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var exporter = new Services.BcfExportService();
                string path  = exporter.ExportGroups(
                    new ClashGroupingEngine().GroupClashes(_displayList), "ClashResolveAI", dlg.SelectedPath);
                MessageBox.Show($"BCF exported:\n{path}", "BCF Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"BCF export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportExcel()
        {
            try
            {
                var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Title    = "Save Excel Report",
                    Filter   = "Excel Workbook|*.xlsx",
                    FileName = $"ClashReport_{DateTime.Now:yyyyMMdd}.xlsx"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var folder = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                string actual=Reports.ExcelReportGenerator.Generate(_displayList, "ClashReport", folder);
                if(!string.Equals(actual,dlg.FileName,StringComparison.OrdinalIgnoreCase)){System.IO.File.Copy(actual,dlg.FileName,true);System.IO.File.Delete(actual);}
                MessageBox.Show($"Excel report saved:\n{dlg.FileName}", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Excel export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResolveAll()
        {
            if (_displayList.Count == 0) return;
            if (MessageBox.Show(
                    $"Mark all {_displayList.Count} displayed issues as acknowledged/resolved? This does not change or verify model geometry.",
                    "Confirm Bulk Resolve",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ClashDashboard.Instance.UpdateStatuses(_displayList.ToList(),ClashStatus.Resolved);
            ApplyFilters();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI FACTORY HELPERS
        // ══════════════════════════════════════════════════════════════════

        // KPI card: accent left-bar, bold number, small label above
        private static FrameworkElement KpiCard(string label, TextBlock valueBlock, WpfBrush accent)
        {
            var card = new Border
            {
                Background      = BgCard,
                BorderBrush     = BorderCol,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(4, 0, 0, 0),
                MinWidth        = 72,
                Padding         = new Thickness(0)
            };

            var outer = new WpfGrid();
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accent_bar = new Border
            {
                Background   = accent,
                CornerRadius = new CornerRadius(3, 0, 0, 3)
            };
            WpfGrid.SetColumn(accent_bar, 0);
            outer.Children.Add(accent_bar);

            var inner = new StackPanel
            {
                Margin              = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            inner.Children.Add(new TextBlock
            {
                Text                = label,
                FontSize            = 7.5,
                FontWeight          = FontWeights.Bold,
                Foreground          = TextMute,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 1)
            });
            inner.Children.Add(valueBlock);
            WpfGrid.SetColumn(inner, 1);
            outer.Children.Add(inner);

            card.Child = outer;
            return card;
        }

        private static TextBlock KpiValue(string text) =>
            new TextBlock
            {
                Text                = text,
                FontSize            = 22,
                FontWeight          = FontWeights.Bold,
                Foreground          = TextMain,
                HorizontalAlignment = HorizontalAlignment.Center
            };

        private static Button TbBtn(string text, WpfBrush bg, Action onClick)
        {
            var b = new Button
            {
                Content         = text,
                Background      = bg,
                Foreground      = TextMain,
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(12, 6, 12, 6),
                Margin          = new Thickness(4, 0, 0, 0),
                FontSize        = 10,
                Cursor          = System.Windows.Input.Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private static TextBlock FLabel(string text) =>
            new TextBlock
            {
                Text              = text,
                Foreground        = TextMute,
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(10, 0, 4, 0)
            };

        private static ComboBox FCombo(double width) =>
            new ComboBox
            {
                Width           = width,
                Height          = 27,
                Background      = BgCard,
                Foreground      = TextMain,
                BorderBrush     = BorderCol,
                BorderThickness = new Thickness(1),
                FontSize        = 10
            };

        private static Button FBtn(string text, WpfBrush bg, Action onClick)
        {
            var b = new Button
            {
                Content         = text,
                Background      = bg,
                Foreground      = TextMain,
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(10, 4, 10, 4),
                Margin          = new Thickness(10, 0, 0, 0),
                Height          = 27,
                FontSize        = 10,
                FontWeight      = FontWeights.SemiBold,
                Cursor          = System.Windows.Input.Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private static DataGridTextColumn TextCol(
            string header, string path, double width, string? fmt = null)
        {
            var col = new DataGridTextColumn
            {
                Header      = header,
                Width       = width,
                CanUserSort = true
            };
            col.Binding = fmt != null
                ? new WpfBinding(path) { StringFormat = fmt }
                : new WpfBinding(path);
            return col;
        }

        // Detail panel helpers
        private void DetailSection(string title)
        {
            _detailProps.Children.Add(new Border
            {
                Background      = BgCard,
                Margin          = new Thickness(8, 8, 8, 2),
                Padding         = new Thickness(10, 4, 10, 4),
                CornerRadius    = new CornerRadius(2),
                Child = new TextBlock
                {
                    Text       = title,
                    FontSize   = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = TextMute
                }
            });
        }

        // FIX v8.0: PropRow/DetailRow now ALWAYS renders the row.
        // Previous version returned early on empty/null, making the
        // detail panel look sparse and unpredictable.
        // Use fallback param to show a placeholder for empty values.
        private void DetailRow(string label, string? value,
            string fallback = "", WpfBrush? highlight = null)
        {
            string display = string.IsNullOrWhiteSpace(value) ? fallback : value!;

            var row = new DockPanel { Margin = new Thickness(10, 2, 10, 2) };

            row.Children.Add(new TextBlock
            {
                Text              = label + ":",
                Width             = 88,
                FontSize          = 10,
                Foreground        = TextMute,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 1, 0, 0)
            });

            var val = new TextBlock
            {
                Text             = display,
                FontSize         = 10,
                Foreground       = highlight ?? (string.IsNullOrEmpty(display) ? TextMute : TextMain),
                TextWrapping     = TextWrapping.Wrap,
                VerticalAlignment= VerticalAlignment.Top
            };
            DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(val);

            _detailProps.Children.Add(row);
        }

        private static Button DetailBtn(string text, WpfBrush bg, Action onClick)
        {
            var b = new Button
            {
                Content         = text,
                Background      = bg,
                Foreground      = TextMain,
                BorderThickness = new Thickness(0),
                Padding         = new Thickness(10, 5, 10, 5),
                Margin          = new Thickness(0, 0, 8, 0),
                FontSize        = 10,
                Cursor          = System.Windows.Input.Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        // ── Severity colour helpers ───────────────────────────────────────

        private static WpfBrush SeverityBrush(ClashSeverity sev)
        {
            switch (sev)
            {
                case ClashSeverity.Critical:   return AccCrit;
                case ClashSeverity.Hard:       return AccHard;
                case ClashSeverity.Soft:       return AccSoft;
                case ClashSeverity.Clearance:  return AccClear;
                default:                       return TextMute;
            }
        }

        // ── Brush factory ────────────────────────────────────────────────
        private static WpfBrush B(byte r, byte g, byte b) =>
            new WpfBrush(WpfColor.FromRgb(r, g, b));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SEVERITY → BRUSH CONVERTER  (used by the severity pill template)
    // ══════════════════════════════════════════════════════════════════════

    public class SeverityToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            if (value is ClashSeverity sev)
            {
                switch (sev)
                {
                    case ClashSeverity.Critical:  return new WpfBrush(WpfColor.FromRgb(220,  50,  50));
                    case ClashSeverity.Hard:      return new WpfBrush(WpfColor.FromRgb(220, 110,  30));
                    case ClashSeverity.Soft:      return new WpfBrush(WpfColor.FromRgb(180, 140,  15));
                    case ClashSeverity.Clearance: return new WpfBrush(WpfColor.FromRgb( 30, 130, 170));
                    default:                      return new WpfBrush(WpfColor.FromRgb( 70,  80, 100));
                }
            }
            return new WpfBrush(WpfColor.FromRgb(70, 80, 100));
        }

        public object ConvertBack(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
#endif // !REVIT_STUB_BUILD
