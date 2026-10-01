// LiveMonitor/ClashRadarPanel.cs  — v5.0
//
// Floating WPF panel — real-time Clash Radar.
//
// Matches the UI from the ClashRadar video:
//   • "Clash Radar" header with live pulse indicator
//   • "Active Clashes" list:  # | Time | Category A | Category B | ID B | Location
//   • Category filter ComboBox (top-right of sub-header)
//   • Per-row severity colour bar (left edge: red / orange / yellow / blue)
//   • Action buttons: Show 3D | Show 2D | Ignore  |  Refresh | Export
//   • Interactive geometry inspector with pop-out and comparison views
//
// Thread safety:
//   DataChanged fires on Revit thread → dispatched to WPF UI thread here.
//   All Revit API calls go through ExternalEvent handlers (never direct).

using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using ComboBox = System.Windows.Controls.ComboBox;

namespace ClashResolveAI.LiveMonitor
{
    public partial class ClashRadarPanel : Window
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static ClashRadarPanel? _instance;
        public static ClashRadarPanel  Instance  => _instance ??= new ClashRadarPanel();
        public static new bool             IsVisible => _instance?.IsLoaded == true
                                                 && _instance.Visibility == Visibility.Visible;

        // ── Revit integration (injected by LiveMonitorService) ─────────
        // IMPORTANT: the handler instances stored here MUST be the same ones
        // that were passed to ExternalEvent.Create() in LiveMonitorService.
        // Otherwise mutating Target/Mode here has no effect on the raised event.
        private ExternalEvent?     _navEvent;
        private ExternalEvent?     _refreshEvent;
        private ClashNavHandler?     _navHandler;
        private ClashRefreshHandler? _refreshHandler;

        // ── Data ───────────────────────────────────────────────────────
        private List<ClashResult>                    _displayed     = new List<ClashResult>();
        private ClashResult?                         _selected;
        private string                               _catFilter     = AllCats;
        private const string                         AllCats        = "All Categories";

        // ── UI controls (populated in BuildUI) ────────────────────────
        private TextBlock    _badgeCount  = null!;
        private ComboBox     _catCombo    = null!;
        private readonly Dictionary<ResultTypes,CheckBox> _resultChecks=new Dictionary<ResultTypes,CheckBox>();
        private DataGrid   _rowsPanel   = null!;
        private string _lastSnapClashId="";
        private TextBlock    _statusLabel = null!;
        private Ellipse      _pulse       = null!;
        private Button       _btn3D       = null!;
        private Button       _btn2D       = null!;
        private Button       _btnIgnore   = null!;
        private Button       _btnRefresh  = null!;
        private Button       _btnExport   = null!;

        // FIX v7.0 (Plan Phase 4): Store DataChanged handler as a named field so
        // it can be properly unsubscribed when the panel closes, preventing the
        // subscription leak that caused double-refresh after reopen cycles.
        private EventHandler? _dataChangedHandler;

        // ── Pulse animation timer ──────────────────────────────────────
        private readonly DispatcherTimer _pulseTimer;
        private bool _pulseState;

        // ══════════════════════════════════════════════════════════════
        //  COLOUR PALETTE
        // ══════════════════════════════════════════════════════════════

        private static SolidColorBrush Bg       = SB(10,  15, 26);
        private static SolidColorBrush BgCard   = SB(14,  21, 36);
        private static SolidColorBrush BgHdr    = SB( 8,  12, 22);
        private static SolidColorBrush BgRow    = SB(17,  25, 42);
        private static SolidColorBrush BgSel    = SB(22,  42, 76);
        private static SolidColorBrush BgHover  = SB(20,  32, 56);
        private static SolidColorBrush Bdr      = SB(30,  45, 72);
        private static SolidColorBrush AccBlue  = SB(41, 128,185);
        private static SolidColorBrush AccOrang = SB(230,126, 34);
        private static SolidColorBrush AccGreen = SB( 39,174, 96);
        private static SolidColorBrush AccRed   = SB(192, 57, 43);
        private static SolidColorBrush AccYel   = SB(241,196, 15);
        private static SolidColorBrush TxtW     = SB(236,240,241);
        private static SolidColorBrush TxtG     = SB(127,140,141);
        private static SolidColorBrush Transp   = new SolidColorBrush(Colors.Transparent);

        private static SolidColorBrush SB(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        // ══════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════

        public ClashRadarPanel()
        {
            Title               = "Clash Radar";
            Width               = 480; Height = 820;
            MinWidth            = 360; MinHeight = 600;
            Background          = Bg;
            BorderBrush         = Bdr;
            BorderThickness     = new Thickness(1);
            WindowStyle         = WindowStyle.None;
            AllowsTransparency  = false;
            ResizeMode          = ResizeMode.CanResizeWithGrip;
            Topmost             = false;
            ShowInTaskbar       = false;

            var wa = SystemParameters.WorkArea;
            Left = wa.Right  - Width  - 14;
            Top  = wa.Top    + 40;

            BuildUI();

            // FIX v7.0: Store handler reference so it can be unsubscribed on Close.
            _dataChangedHandler = (s,e)=>{
                if(_refreshQueued)return;
                _refreshQueued=true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{_refreshQueued=false;RefreshList();}));
            };
            RadarDataStore.Instance.DataChanged += _dataChangedHandler;

            // Pulse animation
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _pulseTimer.Tick += (s, e) =>
            {
                _pulseState = !_pulseState;
                _pulse.Opacity = _pulseState ? 1.0 : 0.35;
            };
            _pulseTimer.Start();

            Closed += (s, e) =>
            {
                // FIX v7.0: Unsubscribe DataChanged before nulling _instance.
                // Without this, the closed Window keeps receiving events, causing
                // silent Dispatcher exceptions and double-refresh on re-open.
                if (_dataChangedHandler != null)
                {
                    RadarDataStore.Instance.DataChanged -= _dataChangedHandler;
                    _dataChangedHandler = null;
                }
                _popout?.Close();SaveLayout();
                _instance = null;
                _pulseTimer.Stop();
            };
        }

        // ── Revit wiring (called by LiveMonitorService after ExternalEvent creation) ──
        public void SetRevitEvents(ExternalEvent navEvent, ExternalEvent refreshEvent,
                                   ClashNavHandler navHandler, ClashRefreshHandler refreshHandler)
        {
            _navEvent       = navEvent;
            _refreshEvent   = refreshEvent;
            _navHandler     = navHandler;
            _refreshHandler = refreshHandler;
        }

        // ══════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            BuildInspectorLayout();
        }

        // ── Header ─────────────────────────────────────────────────────

        private Border MakeHeader()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            // Pulse dot
            _pulse = new Ellipse { Width = 10, Height = 10, Fill = AccGreen,
                                   VerticalAlignment = VerticalAlignment.Center,
                                   HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(_pulse, 0);

            // Title
            var title = new TextBlock { Text = "Clash Radar",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = TxtW, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);

            // Status label (shows "ACTIVE" or "IDLE")
            _statusLabel = new TextBlock
            {
                Text = "● ACTIVE", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = AccGreen,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(_statusLabel, 2);

            // Close button
            var closeBtn = Btn("✕", Transp, TxtG, 24, 24);
            closeBtn.FontSize = 11;
            closeBtn.Cursor   = Cursors.Hand;
            closeBtn.Click   += (s, e) => Hide();
            Grid.SetColumn(closeBtn, 2);

            // Stack status + close
            var rightGrid = new Grid();
            rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            Grid.SetColumn(_statusLabel, 0);
            Grid.SetColumn(closeBtn, 1);
            rightGrid.Children.Add(_statusLabel);
            rightGrid.Children.Add(closeBtn);
            Grid.SetColumn(rightGrid, 2);

            g.Children.Add(_pulse);
            g.Children.Add(title);
            g.Children.Add(rightGrid);

            var border = new Border
            {
                Background      = BgHdr,
                BorderBrush     = Bdr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(10, 0, 8, 0),
                Child           = g
            };
            border.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            return border;
        }

        // ── Sub-header (badge + filter) ─────────────────────────────────

        private FrameworkElement MakeSubHeader()
        {
            var g = new Grid { Background = BgCard };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // "Active Clashes" + badge
            var leftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            leftStack.Children.Add(new TextBlock
            {
                Text = "Active Clashes", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = TxtG, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _badgeCount = new TextBlock
            {
                Text = "0", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = TxtW, Background = AccBlue,
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            leftStack.Children.Add(_badgeCount);
            Grid.SetColumn(leftStack, 0);

            // Category filter
            _catCombo = new ComboBox
            {
                Width = 148, Height = 26,
                Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.Black, BorderBrush = Bdr,
                FontSize = 10, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _catCombo.Items.Add(AllCats);
            _catCombo.SelectedIndex = 0;
            _catCombo.SelectionChanged += (s, e) =>
            {
                if (_refreshing) return;
                _catFilter = _catCombo.SelectedItem as string ?? AllCats;
                RefreshList();
            };
            Grid.SetColumn(_catCombo, 1);

            g.Children.Add(leftStack);
            g.Children.Add(_catCombo);

            // Bottom separator
            var bdr = new Border { BorderBrush = Bdr, BorderThickness = new Thickness(0,0,0,1) };
            var stack=new StackPanel();stack.Children.Add(g);
            var filters=new WrapPanel {Margin=new Thickness(8,5,8,5)};
            foreach(var item in new[]{(ResultTypes.Hard,"Hard"),(ResultTypes.PossibleHard,"Possible hard"),(ResultTypes.Clearance,"Clearance"),(ResultTypes.Unverified,"Unverified")}){
                var type=item.Item1;var check=new CheckBox {Content=item.Item2,Foreground=TxtW,Margin=new Thickness(4),IsChecked=(RadarDataStore.Instance.Types&type)!=0};
                check.Checked+=(_,__)=>ChangeResultTypes();check.Unchecked+=(_,__)=>ChangeResultTypes();_resultChecks[type]=check;filters.Children.Add(check);
            }
            var purge=new Button {Content="Purge non-hard results",Margin=new Thickness(4),Padding=new Thickness(6,2,6,2)};
            purge.Click+=(_,__)=>{if(System.Windows.MessageBox.Show("Purge non-hard results (including possible hard) from this project's live history?", "Clash Radar",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)RadarDataStore.Instance.PurgeNonHard();};filters.Children.Add(purge);
            _thisSession=new CheckBox {Content="This session",Foreground=TxtW,Margin=new Thickness(4),IsChecked=RadarDataStore.Instance.ThisSession};
            _thisSession.Checked+=(_,__)=>{if(!_refreshing)RadarDataStore.Instance.ThisSession=true;};
            _thisSession.Unchecked+=(_,__)=>{if(!_refreshing)RadarDataStore.Instance.ThisSession=false;};filters.Children.Add(_thisSession);
            _inputsBanner=new TextBlock {Text="Model inputs changed, run Full Scan to re-verify",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Orange,Margin=new Thickness(8,4,8,4),Visibility=ScanCoordinator.InputsStale?Visibility.Visible:Visibility.Collapsed};
            stack.Children.Add(filters);stack.Children.Add(_inputsBanner);bdr.Child = stack;
            return bdr;
        }

        private FrameworkElement MakeList()
        {
            return MakeClashGrid();
        }

        private void ChangeResultTypes()
        {
            if(_refreshing)return;
            var types=_resultChecks.Where(x=>x.Value.IsChecked==true).Aggregate(ResultTypes.None,(value,x)=>value|x.Key);
            RadarDataStore.Instance.Types=types;RefreshList();
        }
        private CheckBox _thisSession=null!;
        private TextBlock _inputsBanner=null!;
        private bool _refreshQueued;
        private bool _refreshing;
        private void RefreshList()
        {
            if (_refreshing) return;
            _refreshing = true;
            // CRASH FIX v8.1: This method is invoked asynchronously via
            // Dispatcher.BeginInvoke from RadarDataStore.DataChanged. By the
            // time it runs, ElementA/ElementB on cached ClashResults may be
            // stale (deleted/undone, or from an unloaded linked model).
            // An unhandled exception here terminates Revit, so the whole
            // body is guarded. Category lookups use SafeCategoryName/
            // SafeElementId instead of raw Element.Category/.Id access.
            try
            {
            // Refresh category filter dropdown
            var cats = RadarDataStore.Instance.GetCategories();
            var prev = _catCombo.SelectedItem as string ?? AllCats;
            _catCombo.Items.Clear();
            _catCombo.Items.Add(AllCats);
            foreach (var c in cats) _catCombo.Items.Add(c);
            _catCombo.SelectedItem = _catCombo.Items.Contains(prev) ? prev : AllCats;
            _catFilter = _catCombo.SelectedItem as string ?? AllCats;

            // Get filtered data
            foreach(var entry in _resultChecks)entry.Value.IsChecked=(RadarDataStore.Instance.Types&entry.Key)!=0;
            _thisSession.IsChecked=RadarDataStore.Instance.ThisSession;
            _inputsBanner.Visibility=ScanCoordinator.InputsStale?Visibility.Visible:Visibility.Collapsed;
            var all = RadarDataStore.Instance.GetVisible();
            _displayed = _catFilter == AllCats
                ? all
                : all.Where(c =>
                    c.CategoryNameA.Equals(_catFilter, StringComparison.OrdinalIgnoreCase) ||
                    c.CategoryNameB.Equals(_catFilter, StringComparison.OrdinalIgnoreCase))
                  .ToList();

            // Update count badge
            _badgeCount.Text = _displayed.Count.ToString();

            _rowsPanel.ItemsSource=_displayed;

            // Restore selection if still present
            var previousKey=_selected?.NormalizedKey;
            _selected=_displayed.FirstOrDefault(c=>c.NormalizedKey==previousKey);
            _rowsPanel.SelectedItem=_selected;
            SelectClashPreview(_selected);UpdateActionButtons();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Radar list refresh", ex);
            }
            finally { _refreshing = false; }
        }

        private void SelectClash(ClashResult clash)
        {
            _selected=clash;
            SelectClashPreview(clash);
            UpdateActionButtons();
        }

        private void SelectClashPreview(ClashResult? clash)
        {
            UpdateDetails(clash);
            if(clash==null){ClearPreviewImages();return;}
            if(ScanCoordinator.IsStale(clash)){_lastSnapClashId="";_inspector.Clear("Elements changed · re-check this session or run Full Scan");return;}
            string key=clash.NormalizedKey+":"+clash.GeometryRevision;
            if(_lastSnapClashId==key)return;
            _lastSnapClashId=key;
            _inspector.Clear("Loading geometry…");
            LiveMonitorService.Instance.RequestInspection(clash);
        }
        private void ClearPreviewImages()
        { _lastSnapClashId="";_inspector.Clear();UpdateDetails(null); }

        private void UpdateActionButtons()
        {
            bool has = _selected != null;
            double alpha = has ? 1.0 : 0.45;
            foreach (var b in new[] { _btn3D, _btn2D, _btnIgnore })
                b.Opacity = alpha;
        }

        // ══════════════════════════════════════════════════════════════
        //  BUTTON HANDLERS
        // ══════════════════════════════════════════════════════════════

        private void OnShow3D(object s, RoutedEventArgs e)
        {
            if (_selected == null || _navEvent == null || _navHandler == null) return;
            _navHandler.Target = _selected;
            _navHandler.Mode   = NavMode.View3D;
            _navEvent.Raise();
        }

        private void OnShow2D(object s, RoutedEventArgs e)
        {
            if (_selected == null || _navEvent == null || _navHandler == null) return;
            _navHandler.Target = _selected;
            _navHandler.Mode   = NavMode.View2D;
            _navEvent.Raise();
        }

        private void OnIgnore(object s, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RadarDataStore.Instance.IgnoreClash(_selected);
            _selected = null;
            ClearPreviewImages();
            UpdateActionButtons();
        }

        public void SetScanStatus(string text,bool complete)
        { _btnRefresh.IsEnabled=complete; _btnRefresh.Content=complete?$"↺ Re-check drawn ({LiveSessionLedger.Count})":"Checking…"; _scanText.Text=text;_scanText.ToolTip=text;_cancelScan.IsEnabled=!complete; }
        public void UpdateLedgerCount(){if(_btnRefresh.IsEnabled)_btnRefresh.Content=$"↺ Re-check drawn ({LiveSessionLedger.Count})";}
        public void NotifyGeometryChanged()=>Dispatcher.BeginInvoke(new Action(()=>{_inputsBanner.Visibility=ScanCoordinator.InputsStale?Visibility.Visible:Visibility.Collapsed;if(_selected!=null&&ScanCoordinator.IsStale(_selected))SelectClashPreview(_selected);}));
        private void RequestScanAction(ClashRefreshHandler.Request action)
        {
            if(!LiveMonitorService.Instance.IsRunning||_refreshHandler==null||_refreshEvent==null){SetScanStatus("Start Live Monitor first",true);return;}
            _refreshHandler.Action=action;_refreshEvent.Raise();
        }
        private void OnRefresh(object s, RoutedEventArgs e)
        {
            _btnRefresh.IsEnabled = false;
            _btnRefresh.Content   = "…";
            RequestScanAction(ClashRefreshHandler.Request.Ledger);

        }

        private void OnExport(object s, RoutedEventArgs e)
        {
            RadarExporter.ExportToCsv(_displayed.ToList());
        }

        // ══════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════

        private static Grid EqualGrid(Button[] buttons, SolidColorBrush bg,
                                       Thickness borderThickness)
        {
            var g = new Grid { Background = bg };
            for (int i = 0; i < buttons.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Margin = new Thickness(4, 6, 4, 6);
                Grid.SetColumn(buttons[i], i);
                g.Children.Add(buttons[i]);
            }
            return g;
        }

        private static Button Btn(string text, SolidColorBrush bg, SolidColorBrush fg,
                                   double? w = null, double? h = null)
        {
            var b = new Button
            {
                Content         = text,
                Background      = bg,
                Foreground      = fg,
                BorderThickness = new Thickness(0),
                FontSize        = 10,
                FontWeight      = FontWeights.SemiBold,
                Height          = h ?? 28,
                Cursor          = Cursors.Hand
            };
            if (w.HasValue) b.Width = w.Value;
            return b;
        }

        private static Color CategoryColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return Color.FromRgb(127,140,141);
            string l = category.ToLowerInvariant();
            if (l.Contains("struct") || l.Contains("wall") || l.Contains("column") || l.Contains("beam") || l.Contains("floor"))
                return Color.FromRgb(41,128,185);
            if (l.Contains("mech") || l.Contains("hvac") || l.Contains("duct") || l.Contains("vent"))
                return Color.FromRgb(142,68,173);
            if (l.Contains("plumb") || l.Contains("pipe") || l.Contains("drain"))
                return Color.FromRgb(39,174,96);
            if (l.Contains("cable") || l.Contains("tray") || l.Contains("conduit") || l.Contains("elec"))
                return Color.FromRgb(230,126,34);
            if (l.Contains("fire"))
                return Color.FromRgb(192,57,43);
            return Color.FromRgb(127,140,141);
        }

        private static Color SeverityColor(ClashSeverity s) => s switch
        {
            ClashSeverity.Critical  => Color.FromRgb(192, 57, 43),
            ClashSeverity.Hard      => Color.FromRgb(230,126, 34),
            ClashSeverity.Soft      => Color.FromRgb(241,196, 15),
            ClashSeverity.Clearance => Color.FromRgb( 41,128,185),
            _                       => Color.FromRgb(127,140,141)
        };

        private static string TruncateCat(string s) =>
            s.Length > 14 ? s.Substring(0, 13) + "…" : s;

        // ── Public state control (called by LiveMonitorService) ──────────

        public void SetLiveStatus(string message,bool checking=false)
        {
            _liveText.Text=message;_liveText.ToolTip=message;
            _statusLabel.Text=checking?"● CHECKING":"● WATCHING";
            _statusLabel.ToolTip="Live checks follow edits and selection. Re-check drawn checks only the recorded session.";
        }
        public void SetActiveState(bool active)
        {
            _statusLabel.Text       = active ? "● WATCHING" : "● IDLE";
            _statusLabel.Foreground = active ? AccGreen : TxtG;
            _pulse.Fill             = active ? AccGreen : TxtG;
            _liveText.Text=active?"Watching edits and selection · Re-check uses the session":"Live monitoring stopped";
            UpdateLedgerCount();
            if (active) _pulseTimer.Start(); else _pulseTimer.Stop();
        }
    }
}
