// Commands/Commands.cs  — v7.0
// v7.0: ResetInternal() call removed (no longer exists in LiveMonitorService).
//
// FIX v6.0 (LiveMonitorCommand):
//   • Re-wire panel when monitor running but panel was closed.
//   • System type breakdown added to Full Scan results dialog.

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.AutoResolve;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Engine;
using ClashResolveAI.Links;
using ClashResolveAI.LiveMonitor;
using ClashResolveAI.Rules;
using ClashResolveAI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using ClashEngineNS = ClashResolveAI.ClashEngine.ClashEngine;
using TextBox = System.Windows.Controls.TextBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

#if !REVIT_STUB_BUILD
namespace ClashResolveAI.Commands
{
    internal static class Session
    {
        public static List<ClashResult>? Clashes     { get; set; }
        public static List<ClashGroup>?  Groups      { get; set; }
        public static string             ProjectName { get; set; } = "Project";
        public static CoordinationZone?  ActiveZone  { get; set; }
        public static string             RuleSetName { get; set; } = "DefaultRules";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  1. FULL SCAN — with floor selection dialog
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DetectClashesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) { msg = "Open a project document first."; return Result.Cancelled; }
            try
            {
                var s = AppSettings.Load();
                DocumentSession.Activate(doc);
                Session.ProjectName = doc.Title;

                // ── STEP 1: Floor selection ────────────────────────────────
                var picker = new LevelPickerDialog(doc);
                picker.ShowDialog();
                if (!picker.Confirmed) return Result.Cancelled;

                string selectedLevelId   = picker.SelectedLevelId;    // "" = all floors
                string selectedLevelName = picker.SelectedLevelName;   // "All Floors" or level name

                // ── STEP 2: Pre-scan info dialog ───────────────────────────
                var linkMgr       = new LinkedModelManager(doc);
                string linkInfo   = linkMgr.GetLinksSummary();
                string scopeLabel = string.IsNullOrEmpty(selectedLevelId)
                    ? "All Floors (full model)"
                    : $"Floor: {selectedLevelName} only";

                var options=new FullScanOptionsDialog(s,scopeLabel,linkInfo);
                if(options.ShowDialog()!=true)return Result.Cancelled;
                options.Options.Apply(s,false);AppSettings.Save(s);

                var progress = new ProgressDialog($"Scanning {scopeLabel}…");
                progress.Show();
                progress.Closed += (_,__) => ScanCoordinator.Cancel();
                ScanCoordinator.Start(doc, selectedLevelId, Session.ActiveZone, (clashes, stats) => {
                    int retained=ClashDashboard.Instance.MergeFullScan(clashes,stats.Mode,stats.Scope);
                    progress.Close();
                    TaskDialog.Show("Scan complete",
                        $"Hard clashes: {clashes.Count(c => c.TestType == ClashTestType.HardClash)}\n" +
                        $"Possible hard: {clashes.Count(c => c.TestType == ClashTestType.Unverified&&c.UnverifiedReason==UnverifiedReason.SolidTest)}\n" +
                        $"Confirmed clearance violations: {clashes.Count(c => c.TestType == ClashTestType.ClearanceClash)}\n" +
                        $"Unverified candidates: {clashes.Count(c => c.TestType == ClashTestType.Unverified)}\n\n" +
                        retained+" clearance rows retained from earlier scans, not re-tested.\n\n"+stats.Summary + "\n\nUnverified candidates require review; they are not confirmed clashes.");
                }, progress.SetMessage, reason => { progress.Close(); Diagnostics.Log(reason); },options.Options.SelectedMode,options.Options.IncludeLinkToLink);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return Result.Failed;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  2. GENERATE RFIs
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateRFIsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try
            {
                var activeDoc = data.Application.ActiveUIDocument?.Document;
                if (activeDoc == null || activeDoc.IsFamilyDocument) return Result.Cancelled;
                DocumentSession.Activate(activeDoc);
                var visible=ClashDashboard.Instance.GetVisibleClashes();
                if (!visible.Any())
                {
                    TaskDialog.Show("ClashResolve AI", "No results match the current dashboard filters. Run a scan or adjust the filters.");
                    return Result.Cancelled;
                }

                if(ScanCoordinator.Busy||ScanCoordinator.FullResultsStale||visible.Any(ScanCoordinator.IsStale)){TaskDialog.Show("Full Scan required","Run Full Scan to update Dashboard results before exporting. Live Monitor checks update Clash Radar only.");return Result.Cancelled;}
                var s  = AppSettings.Load();
                var td = new TaskDialog("Generate RFIs & Reports")
                {
                    MainInstruction = "Select outputs to generate:",
                    MainContent =
                        "BCF 2.1 — Navisworks / Solibri compatible\n" +
                        "Excel coordination report\n" +
                        "Word RFI document\n" +
                        $"AI suggestions: {(string.IsNullOrEmpty(s.OpenAiApiKey) ? "No API key" : "Ready")}\n\n" +
                        $"Clashes in current view: {visible.Count}  Groups: {new ClashGroupingEngine().GroupClashes(visible).Count}",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
                };
                if (td.Show() == TaskDialogResult.Cancel) return Result.Cancelled;

                var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select output folder" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
                string outFolder = dlg.SelectedPath;

                var snapshot=visible.Select(c=>c.ExportCopy()).ToList();
                string project=Session.ProjectName;
                var window=new ProgressDialog("Generating reports — Revit remains available");
                window.Show();
                GenerateReports(snapshot,project,outFolder,s,window);
                return Result.Succeeded;
            }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
        private static async void GenerateReports(List<ClashResult> clashes,string project,string folder,AppSettings settings,ProgressDialog window)
        {
            try {
                var generated=await Task.Run(async ()=>{
                    var files=new List<string>();
                    if(!string.IsNullOrEmpty(settings.OpenAiApiKey)) {
                        using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var service=new OpenAI.OpenAIService(settings.OpenAiApiKey,settings.OpenAiModel);
                        try {
                            foreach(var c in clashes.Where(c=>c.TestType==ClashTestType.HardClash).Take(20)) {
                                var answer=await service.AnalyseAsync(c,timeout.Token).ConfigureAwait(false);
                                c.AiSuggestion=answer.suggestion;c.RfiText=answer.rfi;
                            }
                        } catch(Exception ex){files.Add("AI: "+ex.Message);}
                    }
                    files.Add(new BcfExportService().ExportClashes(clashes,project,folder));
                    files.Add(Reports.ExcelReportGenerator.Generate(clashes,project,folder));
                    files.Add(Reports.WordReportGenerator.Generate(clashes,project,folder));
                    return files;
                });
                window.Close();
                MessageBox.Show(string.Join("\n",generated),"Reports generated from current result snapshot");
            }catch(Exception ex){window.Close();Diagnostics.Log("Report generation",ex);MessageBox.Show(ex.Message,"Report error");}
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  3. LIVE MONITOR
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LiveMonitorCommand : IExternalCommand
    {
        // FIX v6.0 (Bug 4):
        //   ON  → Start() opens ClashRadarPanel + fires initial scan toast.
        //   ON (panel re-open) → RewirePanel() re-attaches live ExternalEvents
        //     to a fresh panel singleton (user closed and clicked again while
        //     monitor was already running).
        //   OFF → Stop() marks panel as idle; user keeps the list visible.
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try
            {
                var svc = LiveMonitorService.Instance;

                if (!App.MonitorActive)
                {
                    // Start fresh monitor session
                    svc.Start(data.Application);
                    App.MonitorActive = true;
                    App.RefreshMonitorButton();
                }
                else
                {
                    // Monitor already running — check if panel is visible
                    if (!ClashRadarPanel.IsVisible)
                    {
                        // Panel was closed — just re-wire and show it, no restart needed
                        svc.RewirePanel();
                        // Don't toggle MonitorActive; session continues
                    }
                    else
                    {
                        // Panel visible and button clicked again — stop the monitor
                        svc.Stop();
                        App.MonitorActive = false;
                        App.RefreshMonitorButton();
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  4. DASHBOARD
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    // ══════════════════════════════════════════════════════════════════════
    //  4. DASHBOARD
    // ══════════════════════════════════════════════════════════════════════
    public class DashboardCommand : IExternalCommand
    {
        // FIX v6.1: Create ExternalEvent handlers here (on the Revit API thread)
        // and wire them to the dashboard window as Action<ClashResult> delegates.
        // This ensures Show 2D / Show 3D navigate correctly within Revit's context.
        private static DashboardShow2DHandler? _show2DHandler;
        private static DashboardShow3DHandler? _show3DHandler;
        private static ExternalEvent?          _show2DEvent;
        private static ExternalEvent?          _show3DEvent;

        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try
            {
                // Create ExternalEvent handlers once (lazy init — persists for session)
                if (_show2DEvent == null)
                {
                    _show2DHandler = new DashboardShow2DHandler();
                    _show2DEvent   = ExternalEvent.Create(_show2DHandler);
                }
                if (_show3DEvent == null)
                {
                    _show3DHandler = new DashboardShow3DHandler();
                    _show3DEvent   = ExternalEvent.Create(_show3DHandler);
                }

                // Wire actions into the dashboard singleton
                ClashDashboard.Instance.Show2DAction = (clash) =>
                {
                    _show2DHandler!.Pending = clash;
                    _show2DEvent!.Raise();
                };
                ClashDashboard.Instance.Show3DAction = (clash) =>
                {
                    _show3DHandler!.Pending = clash;
                    _show3DEvent!.Raise();
                };

                ClashDashboard.Instance.ShowWindow();
                return Result.Succeeded;
            }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  5. SETTINGS
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try { new SettingsWindow().ShowDialog(); return Result.Succeeded; }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  LEVEL PICKER DIALOG  — Issue 1 fix
    //  Shows all levels from the document. User picks one or selects "All Floors".
    // ══════════════════════════════════════════════════════════════════════

    internal class LevelPickerDialog : Window
    {
        private static WpfBrush BgMain  = B(15, 20, 32);
        private static WpfBrush BgCard  = B(22, 30, 48);
        private static WpfBrush AccBlue = B(41, 128, 185);
        private static WpfBrush TextW   = B(236, 240, 241);
        private static WpfBrush TextG   = B(127, 140, 141);

        private System.Windows.Controls.ListBox _listBox = null!;

        public bool   Confirmed         { get; private set; }
        public string SelectedLevelId   { get; private set; } = "";
        public string SelectedLevelName { get; private set; } = "All Floors";

        private List<(string id, string name, double elev)> _levels;

        public LevelPickerDialog(Document doc)
        {
            Title  = "ClashResolve AI — Select Floor to Scan";
            Width  = 420; Height = 480;
            Background = BgMain;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            _levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (l.Id.Value.ToString(), l.Name,
                    UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Meters)))
                .ToList();

            BuildUI();
        }

        private void BuildUI()
        {
            var root = new WpfGrid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new StackPanel
            {
                Background = B(10, 15, 26),
                Margin     = new Thickness(0, 0, 0, 0)
            };
            header.Children.Add(new TextBlock
            {
                Text       = "Select Floor to Scan",
                FontSize   = 16, FontWeight = FontWeights.Bold,
                Foreground = TextW, Margin = new Thickness(20, 16, 20, 4)
            });
            header.Children.Add(new TextBlock
            {
                Text       = "Select a specific floor, or scan all floors at once.",
                FontSize   = 11, Foreground = TextG,
                Margin     = new Thickness(20, 0, 20, 14)
            });
            WpfGrid.SetRow(header, 0);
            root.Children.Add(header);

            // Level list
            _listBox = new System.Windows.Controls.ListBox
            {
                Background = BgCard,
                Foreground = TextW,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(12),
                FontSize = 12
            };

            // "All Floors" option always at top
            _listBox.Items.Add(new ListBoxItem
            {
                Content = "   All Floors  (full model scan)",
                Tag     = ("", "All Floors"),
                FontWeight = FontWeights.Bold,
                Foreground = new WpfBrush(WpfColor.FromRgb(41, 174, 128)),
                Padding    = new Thickness(8, 6, 8, 6)
            });

            // One entry per level
            foreach (var (id, name, elev) in _levels)
            {
                _listBox.Items.Add(new ListBoxItem
                {
                    Content = $"   {name}   ({elev:F2} m)",
                    Tag     = (id, name),
                    Padding = new Thickness(8, 5, 8, 5)
                });
            }

            _listBox.SelectedIndex = 0;
            WpfGrid.SetRow(_listBox, 1);
            root.Children.Add(_listBox);

            // Buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 90, Height = 34,
                Background = B(44, 62, 80), Foreground = TextW,
                BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s, e) => { Confirmed = false; Close(); };

            var okBtn = new Button
            {
                Content = "Scan Selected Floor", Width = 160, Height = 34,
                Background = AccBlue, Foreground = TextW,
                BorderThickness = new Thickness(0), FontWeight = FontWeights.Bold
            };
            okBtn.Click += (s, e) =>
            {
                var item = _listBox.SelectedItem as ListBoxItem;
                if (item?.Tag is ValueTuple<string, string> tag)
                {
                    SelectedLevelId   = tag.Item1;
                    SelectedLevelName = tag.Item2;
                }
                Confirmed = true;
                Close();
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            WpfGrid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            Content = root;
        }

        private static WpfBrush B(byte r, byte g, byte b) =>
            new WpfBrush(WpfColor.FromRgb(r, g, b));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PROGRESS DIALOG
    // ══════════════════════════════════════════════════════════════════════

    internal class ProgressDialog : Window
    {
        private readonly TextBlock _tb;
        private readonly System.Windows.Controls.ProgressBar _bar;
        public ProgressDialog(string title)
        {
            Title=title;Width=580;Height=235;MinWidth=450;ResizeMode=ResizeMode.CanResize;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;
            Background=new WpfBrush(WpfColor.FromRgb(15,20,32));
            var root=new System.Windows.Controls.Grid { Margin=new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            root.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});
            root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            _bar=new System.Windows.Controls.ProgressBar { Height=6,Minimum=0,Maximum=100,IsIndeterminate=true,Margin=new Thickness(0,0,0,12) };root.Children.Add(_bar);
            _tb=new TextBlock {Text="Preparing model snapshots…",FontSize=12,Foreground=new WpfBrush(WpfColor.FromRgb(236,240,241)),TextWrapping=TextWrapping.Wrap};System.Windows.Controls.Grid.SetRow(_tb,1);root.Children.Add(_tb);
            var cancel=new System.Windows.Controls.Button {Content="Cancel scan",Width=115,Height=30,HorizontalAlignment=HorizontalAlignment.Right};cancel.Click+=(_,__)=>Close();System.Windows.Controls.Grid.SetRow(cancel,2);root.Children.Add(cancel);
            Content=root;
        }
        public void SetMessage(string message)
        {
            var stats=ScanCoordinator.CurrentStatistics;
            _tb.Text=message;
            _bar.IsIndeterminate=stats==null||stats.TotalSources==0;
            _bar.Value=stats?.ProgressPercent??0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SETTINGS WINDOW
    // ══════════════════════════════════════════════════════════════════════

    internal class SettingsWindow : Window
    {
        internal Action SaveValues {get;private set;}=null!;
        public SettingsWindow()
        {
            Title  = "ClashResolve AI v4.1 — Settings";
            Width  = 560; Height = 480;
            Background = new WpfBrush(WpfColor.FromRgb(15, 20, 32));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s  = AppSettings.Load();
            var sp = new StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(Lbl("Settings", 18, true));
            sp.Children.Add(new TextBlock { Height = 12 });

            sp.Children.Add(Lbl("OpenAI API Key", 11));
            var apiBox = new PasswordBox { Password = s.OpenAiApiKey, Height = 28,
                Margin = new Thickness(0, 4, 0, 12) };
            sp.Children.Add(apiBox);

            sp.Children.Add(Lbl("Active Rule Set", 11));
            var ruleCombo = new WpfComboBox { Height = 28, Margin = new Thickness(0, 4, 0, 12) };
            foreach (var rs in RulesEngine.GetAvailableRuleSets()) ruleCombo.Items.Add(rs);
            ruleCombo.SelectedItem = s.RuleSetName;
            sp.Children.Add(ruleCombo);

            var linksCheck = new CheckBox
            {
                Content = "Scan Linked Models", IsChecked = s.ScanLinkedModels,
                Foreground = W(), Margin = new Thickness(0, 0, 0, 12)
            };
            sp.Children.Add(linksCheck);
            var scanOptions=new ScanOptionsPanel(s,true);sp.Children.Add(scanOptions);
            linksCheck.Checked+=(_,__)=>scanOptions.LinkToLink.IsEnabled=true;
            linksCheck.Unchecked+=(_,__)=>scanOptions.LinkToLink.IsEnabled=false;
            TextBox Number(string label,double value){sp.Children.Add(Lbl(label));var box=new TextBox{Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture),Height=26,Margin=new Thickness(0,2,0,8)};sp.Children.Add(box);return box;}
            CheckBox Flag(string label,bool value){var box=new CheckBox{Content=label,IsChecked=value,Foreground=W(),Margin=new Thickness(0,2,0,8)};sp.Children.Add(box);return box;}
            var overlap=Number("Minimum confirmed overlap (mm³)",s.MinimumOverlapMM3);
            var insulation=Number("Minimum clearance / insulation allowance (mm)",s.InsulationMM);
            var maintenance=Number("Maintenance clearance (mm)",s.MaintenanceMM);
            var slice=Number("Live work slice target (5–40 ms)",s.LiveSliceMilliseconds);
            var debounce=Number("Delay after modeling change (100–2000 ms)",s.LiveDebounceMilliseconds);
            var within=Flag("Scan internal clashes within linked models",s.ScanWithinLinks);
            var edited=Flag("Include edited elements in the live session",s.TrackEditedElements);
            var generic=Flag("Include generic models and specialty equipment",s.IncludeGenericModels);
            var insulated=Flag("Include insulation and lining geometry",s.IncludeInsulation);
            var supports=Flag("Exclude families by support/hanger name (may hide collisions)",s.ExcludeNamedSupports);
            var joints=Flag("Exclude directly connected joints",s.ExcludeConnectedJoints);
            var structural=Flag("Include structure",s.IncludeStructural);
            var toast=Flag("Show live notifications",s.ShowToast);


            sp.Children.Add(Lbl("Rule Sets Folder:", 10));
            string rulesFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClashResolveAI", "Rules");
            sp.Children.Add(new TextBlock
            {
                Text = rulesFolder, Foreground = G(), FontSize = 10,
                Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap
            });
            var openFolderBtn = new Button
            {
                Content = "Open Rules Folder", Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 16)
            };
            openFolderBtn.Click += (o, e) => System.Diagnostics.Process.Start(rulesFolder);
            sp.Children.Add(openFolderBtn);

            var saveBtn = new Button
            {
                Content = "Save Settings", Height = 36,
                Background = new WpfBrush(WpfColor.FromRgb(41, 128, 185)),
                Foreground = W(), BorderThickness = new Thickness(0)
            };
            SaveValues = () =>
            {
                double Read(TextBox box)=>double.Parse(box.Text,System.Globalization.CultureInfo.InvariantCulture);
                    s.MinimumOverlapMM3=Math.Max(0.001,Read(overlap));s.InsulationMM=Math.Max(0,Read(insulation));s.MaintenanceMM=Math.Max(0,Read(maintenance));
                    s.LiveSliceMilliseconds=(int)Math.Max(5,Math.Min(40,Read(slice)));s.LiveDebounceMilliseconds=(int)Math.Max(100,Math.Min(2000,Read(debounce)));
                s.ScanWithinLinks=within.IsChecked==true;s.IncludeGenericModels=generic.IsChecked==true;s.IncludeInsulation=insulated.IsChecked==true;
                s.TrackEditedElements=edited.IsChecked==true;
                s.ExcludeNamedSupports=supports.IsChecked==true;s.ExcludeConnectedJoints=joints.IsChecked==true;s.IncludeStructural=structural.IsChecked==true;s.ShowToast=toast.IsChecked==true;
                s.RuleSetName=ruleCombo.SelectedItem as string??"DefaultRules";
                s.OpenAiApiKey     = apiBox.Password;
                s.ScanLinkedModels = linksCheck.IsChecked == true;
                scanOptions.Apply(s,true);
                AppSettings.Save(s);
                if (ruleCombo.SelectedItem is string rs) Session.RuleSetName = rs;
            };
            saveBtn.Click += (o,e) => {
                try {SaveValues();}catch(Exception ex){MessageBox.Show("Unable to save settings: "+ex.Message);return;}
                MessageBox.Show("Settings saved.", "ClashResolve AI",
                    MessageBoxButton.OK);
                Close();
            };
            sp.Children.Add(saveBtn);
            Content = new ScrollViewer { Content = sp };
        }

        private static TextBlock Lbl(string t, int sz = 11, bool bold = false) =>
            new TextBlock
            {
                Text = t, FontSize = sz,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = G(), Margin = new Thickness(0, 0, 0, 2)
            };

        private static WpfBrush W() => new WpfBrush(WpfColor.FromRgb(236, 240, 241));
        private static WpfBrush G() => new WpfBrush(WpfColor.FromRgb(127, 140, 141));
    }
}

#endif
