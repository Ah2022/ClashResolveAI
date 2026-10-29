// LiveMonitor/ClashRadarPanel.cs  — v5.0
//
// Revit dockable pane — immutable live Clash Radar.
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
    public partial class ClashRadarPanel : Page, Autodesk.Revit.UI.IDockablePaneProvider
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static ClashRadarPanel? _instance;
        public static ClashRadarPanel  Instance  => _instance ??= new ClashRadarPanel();
        public static new bool             IsVisible => _instance?.IsLoaded == true
                                                 && ((UIElement)_instance).IsVisible;

        // ── Data ───────────────────────────────────────────────────────
        private List<LiveClashDto> _displayed=>ViewModel.Rows.ToList();
        private LiveClashDto? _selected {get=>ViewModel.Selected;set=>ViewModel.Selected=value;}
        public ClashRadarViewModel ViewModel {get;}
        public static readonly Autodesk.Revit.UI.DockablePaneId PaneId=new Autodesk.Revit.UI.DockablePaneId(new Guid("E4C13235-A776-4EA6-B2DD-B32930CD05D8"));
        public void SetupDockablePane(Autodesk.Revit.UI.DockablePaneProviderData data){data.FrameworkElement=this;data.InitialState=new Autodesk.Revit.UI.DockablePaneState {DockPosition=Autodesk.Revit.UI.DockPosition.Right};}
        public void Show()=>LiveMonitorService.Instance.ShowRadar();
        public void Hide()=>ViewModel.HideCommand.Execute(null);
        public void Close()=>Hide();
        internal void ShowInApi(Autodesk.Revit.UI.UIApplication app,bool show){var pane=app.GetDockablePane(PaneId);if(show)pane.Show();else pane.Hide();}
        internal void Shutdown(){_closing=true;_popout?.Close();SaveLayout();_pulseTimer.Stop();ViewModel.Dispose();}
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
            MinWidth=280;Background=Bg;
            ViewModel=new ClashRadarViewModel(RadarDataStore.Instance,new RadarActions(),
                action=>Dispatcher.BeginInvoke(DispatcherPriority.Background,action));
            DataContext=ViewModel;
            BuildUI();
            ViewModel.SelectionChanged+=(_,__)=>{SelectClashPreview(ViewModel.Selected);UpdateActionButtons();};
            ViewModel.PropertyChanged+=(_,e)=>{
                if(e.PropertyName==nameof(ViewModel.Rows)||e.PropertyName==nameof(ViewModel.Categories)||e.PropertyName==nameof(ViewModel.RequiresFullScan))RefreshList();
                if(e.PropertyName==nameof(ViewModel.Mode)){
                    _statusLabel.Text=ViewModel.Mode.ToString().ToUpperInvariant();
                    _pulse.Fill=ViewModel.Mode==MonitorMode.Off?TxtG:AccGreen;
                    if(ViewModel.Mode==MonitorMode.Off)_pulseTimer?.Stop();else _pulseTimer?.Start();
                }
            };
            Loaded+=(_,__)=>{RefreshList();if(ViewModel.Running&&ViewModel.Mode!=MonitorMode.Off)_pulseTimer?.Start();};
            IsVisibleChanged+=(_,__)=>{if(((UIElement)this).IsVisible){RefreshList();SelectClashPreview(ViewModel.Selected);if(ViewModel.Running&&ViewModel.Mode!=MonitorMode.Off)_pulseTimer?.Start();}else{_pulseTimer?.Stop();_popout?.Close();}};
            Unloaded+=(_,__)=>{_pulseTimer?.Stop();_popout?.Close();SaveLayout();};
            // Pulse animation
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _pulseTimer.Tick += (s, e) =>
            {
                _pulseState = !_pulseState;
                _pulse.Opacity = _pulseState ? 1.0 : 0.35;
            };
            _pulseTimer.Start();


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
            closeBtn.Command=ViewModel.HideCommand;
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
                ViewModel.Category = _catCombo.SelectedItem as string ?? AllCats;
            };
            Grid.SetColumn(_catCombo, 1);

            g.Children.Add(leftStack);
            g.Children.Add(_catCombo);

            // Bottom separator
            var bdr = new Border { BorderBrush = Bdr, BorderThickness = new Thickness(0,0,0,1) };
            var stack=new StackPanel();stack.Children.Add(g);
            var filters=new WrapPanel {Margin=new Thickness(8,5,8,5)};
            foreach(var item in new[]{(ResultTypes.Hard,"Hard"),(ResultTypes.PossibleHard,"Possible hard"),(ResultTypes.Clearance,"Clearance"),(ResultTypes.Unverified,"Unverified")}){
                var type=item.Item1;var check=new CheckBox {Content=item.Item2,Foreground=TxtW,Margin=new Thickness(4),IsChecked=(ViewModel.Types&type)!=0};
                check.Checked+=(_,__)=>ChangeResultTypes();check.Unchecked+=(_,__)=>ChangeResultTypes();_resultChecks[type]=check;filters.Children.Add(check);
            }
            var purge=new Button {Content="Purge non-hard results",Margin=new Thickness(4),Padding=new Thickness(6,2,6,2)};
            purge.Command=ViewModel.PurgeCommand;filters.Children.Add(purge);
            _thisSession=new CheckBox {Content="This session",Foreground=TxtW,Margin=new Thickness(4),IsChecked=ViewModel.ThisSession};
            _thisSession.Checked+=(_,__)=>{if(!_refreshing)ViewModel.ThisSession=true;};
            _thisSession.Unchecked+=(_,__)=>{if(!_refreshing)ViewModel.ThisSession=false;};filters.Children.Add(_thisSession);
            _inputsBanner=new TextBlock {Text="Inputs changed; re-check this Radar session",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Orange,Margin=new Thickness(8,4,8,4),Visibility=Visibility.Collapsed};
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
            ViewModel.Types=types;
        }
        private CheckBox _thisSession=null!;
        private TextBlock _inputsBanner=null!;
        private bool _refreshing;
        private void RefreshList()
        {
            if (_refreshing) return;
            _refreshing = true;
            // Dispatcher refresh reads immutable DTO snapshots. Deleted,
            // undone or unloaded endpoints never require Revit API access here.
            // Keep the dispatcher boundary guarded against rendering failures.
            try
            {
            var prev=ViewModel.Category;
            _catCombo.Items.Clear();
            foreach(var category in ViewModel.Categories)_catCombo.Items.Add(category);
            _catCombo.SelectedItem=prev;
            foreach(var entry in _resultChecks)entry.Value.IsChecked=(ViewModel.Types&entry.Key)!=0;
            _thisSession.IsChecked=ViewModel.ThisSession;
            _inputsBanner.Visibility=ViewModel.RequiresFullScan?Visibility.Visible:Visibility.Collapsed;
            _badgeCount.Text=ViewModel.VisibleCount.ToString();
            _rowsPanel.ItemsSource=ViewModel.Rows;
            _rowsPanel.SelectedItem=ViewModel.Selected;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Radar list refresh", ex);
            }
            finally { _refreshing = false; }
        }

        private void SelectClash(LiveClashDto clash)
        {
            _selected=clash;
            SelectClashPreview(clash);
            UpdateActionButtons();
        }

        private void SelectClashPreview(LiveClashDto? clash)
        {
            UpdateDetails(clash);
            if(clash==null){ClearPreviewImages();return;}
            if(!((UIElement)this).IsVisible){_lastSnapClashId="";return;}
            if(!ViewModel.CanInspect){_lastSnapClashId="";_inspector.Clear("Session changed · re-check before inspection");return;}
            if(clash.Verification!=LiveVerificationState.Verified){_lastSnapClashId="";_inspector.Clear("Elements changed · re-check this Radar session");return;}
            string key=clash.NormalizedKey+":"+clash.GeometryRevision;
            if(_lastSnapClashId==key)return;
            _lastSnapClashId=key;
            _inspector.Clear("Loading geometry…");
            ViewModel.InspectCommand.Execute(null);
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

        private void OnShow3D(object s,RoutedEventArgs e)=>ViewModel.Show3DCommand.Execute(null);
        private void OnShow2D(object s,RoutedEventArgs e)=>ViewModel.Show2DCommand.Execute(null);
        public void SetScanStatus(string text,bool complete)=>ViewModel.SetMessage(text,complete);
        public void UpdateLedgerCount()=>ViewModel.Refresh();
        public void NotifyGeometryChanged()=>Dispatcher.BeginInvoke(new Action(()=>{ViewModel.Refresh();SelectClashPreview(ViewModel.Selected);}));
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

        public void SetLiveStatus(string message,bool checking=false)=>ViewModel.SetMessage(message,!checking);
        public void SetActiveState(bool active){if(active)_pulseTimer.Start();else _pulseTimer.Stop();ViewModel.Refresh();}
    }
}
