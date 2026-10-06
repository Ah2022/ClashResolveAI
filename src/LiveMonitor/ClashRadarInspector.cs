using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClashResolveAI.Core;
using ClashResolveAI.Inspection;

namespace ClashResolveAI.LiveMonitor
{
    public partial class ClashRadarPanel
    {
        private InspectorPreferences _preferences=null!;
        private ClashInspector _inspector=null!;
        private Border _inspectorHost=null!;
        private Window? _popout;
        private Grid _layout=null!;
        private Grid _details=null!;
        private Expander _detailsStrip=null!;
        private TextBlock _scanText=null!,_liveText=null!;
        private Button _cancelScan=null!;
        private DataGridColumn _statusColumn=null!,_idsColumn=null!;
        private bool _closing;

        private void BuildInspectorLayout()
        {
            _preferences=InspectorPreferences.Load();
            _layout=new Grid();
            _layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(44) });
            _layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(_preferences.ListRatio,GridUnitType.Star),MinHeight=85 });
            _layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(6) });
            _layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1-_preferences.ListRatio,GridUnitType.Star),MinHeight=230 });
            _layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            void Add(UIElement item,int row){Grid.SetRow(item,row);_layout.Children.Add(item);}
            Add(MakeHeader(),0);Add(MakeSubHeader(),1);
            _details=new Grid { Margin=new Thickness(8),MaxHeight=160 };
            _details.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(92) });
            _details.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            _detailsStrip=new Expander { Header="Clash details · select an issue",Foreground=TxtG,FontSize=10,Background=BgCard,Content=_details };
            Add(_detailsStrip,2);Add(MakeList(),3);
            var split=new GridSplitter { Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Bdr,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext };
            split.DragCompleted+=(_,__)=>SaveLayout();Add(split,4);
            _inspector=new ClashInspector(_preferences);
            _inspector.PopOutRequested+=TogglePopOut;
            _inspector.NavigateRequested+=threeD=>{if(threeD)OnShow3D(this,new RoutedEventArgs());else OnShow2D(this,new RoutedEventArgs());};
            _inspector.PinRequested+=scene=>ViewModel.PinCommand.Execute(new RadarPinRequest(scene,_preferences));
            _inspectorHost=new Border { Child=_inspector,Background=BgHdr };Add(_inspectorHost,5);
            // Existing navigation handlers remain the only native-view entry point.
            _btn3D=Btn("3D",AccBlue,TxtW);_btn3D.Command=ViewModel.Show3DCommand;_btn2D=Btn("2D",AccBlue,TxtW);_btn2D.Command=ViewModel.Show2DCommand;
            _btnIgnore=Btn("Ignore",BgCard,TxtG);_btnIgnore.Command=ViewModel.IgnoreCommand;
            _btnRefresh=Btn($"↺ Re-check drawn ({LiveSessionLedger.Count})",BgCard,TxtW);_btnRefresh.Command=ViewModel.CheckChangesCommand;
            _btnRefresh.SetBinding(ContentControl.ContentProperty,new Binding(nameof(ViewModel.CheckLabel)));
            _btnRefresh.ToolTip="Trigger checks accumulated changes. Live rechecks recorded session elements and pending changes.";
            _btnExport=Btn("Export",BgCard,TxtW);_btnExport.Command=ViewModel.ExportCommand;
            _cancelScan=Btn("Cancel scan",BgCard,TxtG);_cancelScan.Command=ViewModel.CancelCommand;
            var clear=Btn("Clear session",BgCard,TxtW);clear.Command=ViewModel.ClearCommand;
            var actions=new StackPanel();
            var modeRow=new DockPanel {Margin=new Thickness(6)};
            modeRow.Children.Add(new TextBlock {Text="Monitoring mode",Foreground=TxtW,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});
            var modeSelector=new System.Windows.Controls.ComboBox {ItemsSource=ViewModel.Modes,MinWidth=110,HorizontalAlignment=HorizontalAlignment.Right,ToolTip="Live checks edits automatically. Trigger waits for Check Changes. Off discards pending live work."};
            modeSelector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,new Binding(nameof(ViewModel.Mode)) {Mode=BindingMode.OneWay});
            modeSelector.SelectionChanged+=(_,__)=>{if(modeSelector.SelectedItem is MonitorMode mode&&mode!=ViewModel.Mode){ViewModel.ModeCommand.Execute(mode);modeSelector.SelectedItem=ViewModel.Mode;}};
            modeRow.Children.Add(modeSelector);actions.Children.Add(modeRow);actions.Children.Add(_btnRefresh);
            actions.Children.Add(EqualGrid(new[]{_btnIgnore,_btnExport,clear,_cancelScan},BgCard,new Thickness(0)));Add(actions,6);
            _scanText=new TextBlock { Text="Ready · geometry inspection",FontSize=10,Foreground=TxtG,Margin=new Thickness(8,4,8,0),TextTrimming=TextTrimming.CharacterEllipsis };
            _liveText=new TextBlock { Text="Watching edits and selection · Re-check uses the session",FontSize=10,Foreground=TxtG,Margin=new Thickness(8,2,8,0),TextTrimming=TextTrimming.CharacterEllipsis };
            var statuses=new StackPanel();statuses.Children.Add(_liveText);statuses.Children.Add(_scanText);
            _liveText.SetBinding(TextBlock.TextProperty,new Binding(nameof(ViewModel.ModeText)));
            _scanText.SetBinding(TextBlock.TextProperty,new Binding(nameof(ViewModel.StatusText)));
            var depth=new TextBlock {FontSize=10,Foreground=TxtG,Margin=new Thickness(8,2,8,4)};depth.SetBinding(TextBlock.TextProperty,new Binding(nameof(ViewModel.QueueText)));statuses.Children.Add(depth);
            var scopeText=new TextBlock {FontSize=10,Foreground=TxtG,Margin=new Thickness(8,0,8,4),TextWrapping=TextWrapping.Wrap};scopeText.SetBinding(TextBlock.TextProperty,new Binding(nameof(ViewModel.ScopeText)));scopeText.ToolTip="This checks affected sources under your current rules and categories. Unloaded links are excluded; whole-project completeness requires Full Scan.";statuses.Children.Add(scopeText);
            var diagnosticPanel=new StackPanel();var diagnosticText=new TextBlock {Foreground=TxtG,FontSize=10,Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};diagnosticText.SetBinding(TextBlock.TextProperty,new Binding(nameof(ViewModel.DiagnosticText)));diagnosticPanel.Children.Add(diagnosticText);
            var exportDiagnostics=Btn("Export diagnostics",BgCard,TxtW);exportDiagnostics.Command=ViewModel.ExportDiagnosticsCommand;diagnosticPanel.Children.Add(exportDiagnostics);
            statuses.Children.Add(new Expander {Header="Diagnostics",Foreground=TxtG,Content=diagnosticPanel,Margin=new Thickness(8,0,8,4)});Add(statuses,7);
            Content=_layout;

        }
        private DataGrid MakeClashGrid()
        {
            _rowsPanel=new DataGrid { AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,
                SelectionUnit=DataGridSelectionUnit.FullRow,Background=Bg,Foreground=TxtW,RowBackground=BgRow,AlternatingRowBackground=BgCard,BorderThickness=new Thickness(0),
                HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.None,RowHeight=30,FontSize=10,EnableRowVirtualization=true,EnableColumnVirtualization=true };
            var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,BgHdr));headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,TxtG));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5)));_rowsPanel.ColumnHeaderStyle=headerStyle;
            DataGridColumn Column(string title,string path,double width)
            {
                var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(5,4,2,2)));style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(path)));
                var c=new DataGridTextColumn { Header=title,Binding=new Binding(path),Width=new DataGridLength(width,DataGridLengthUnitType.Star),MinWidth=28,ElementStyle=style };_rowsPanel.Columns.Add(c);return c;
            }
            Column("Type","TestType",.85);Column("Element A","CategoryNameA",1.3);Column("Element B","CategoryNameB",1.3);
            Column("Verification","Verification",.85);Column("Level","LevelName",1);_idsColumn=Column("ID A","ElementAId",.8);_statusColumn=Column("Status","Status",.8);
            _rowsPanel.SizeChanged+=(_,__)=>{_statusColumn.Visibility=_rowsPanel.ActualWidth<600?Visibility.Collapsed:Visibility.Visible;_idsColumn.Visibility=_rowsPanel.ActualWidth<450?Visibility.Collapsed:Visibility.Visible;};
            _rowsPanel.SelectionChanged+=(_,__)=>{if(_refreshing)return;if(_rowsPanel.SelectedItem is LiveClashDto c)SelectClash(c);else{_selected=null;ClearPreviewImages();UpdateActionButtons();}};
            return _rowsPanel;
        }
        private void UpdateDetails(LiveClashDto? clash)
        {
            if(_details==null)return;
            _details.Children.Clear();_details.RowDefinitions.Clear();
            _detailsStrip.Header=clash==null?"Clash details · select an issue":$"{clash.TestType} · {clash.ElementAId} / {clash.ElementBId} · details";
            if(clash==null)return;
            var values=new[]{("Element A",$"{clash.CategoryNameA} · {clash.ElementAId} · {clash.LinkFileA}"),("Element B",$"{clash.CategoryNameB} · {clash.ElementBId} · {clash.LinkFileB}"),
                ("Location",$"{clash.LevelName} · {clash.GridRef}"),("Gap / required",$"{clash.GapMM:F1} / {clash.RequiredClearanceMM:F1} mm"),("Overlap",$"{clash.OverlapVolumeMM3:F1} mm³"),("Evidence",clash.GeometryEvidence),("Status",clash.Status.ToString())};
            for(int i=0;i<values.Length;i++)
            {
                _details.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                foreach(int col in new[]{0,1})
                {
                    var text=new TextBlock { Text=col==0?values[i].Item1:values[i].Item2,Foreground=col==0?TxtG:TxtW,Margin=new Thickness(0,1,5,1),TextTrimming=TextTrimming.CharacterEllipsis };
                    text.ToolTip=text.Text;Grid.SetRow(text,i);Grid.SetColumn(text,col);_details.Children.Add(text);
                }
            }
        }
        public void AcceptInspection(LiveClashDto clash,InspectionScene? scene,string error)
        {
            if(_selected?.NormalizedKey!=clash.NormalizedKey||_selected.GeometryRevision!=clash.GeometryRevision)return;
            if(!ViewModel.CanInspect)return;
            if(!ViewModel.CanInspect||clash.Verification!=LiveVerificationState.Verified){_inspector.Clear("Elements changed · re-check before inspection");_lastSnapClashId="";return;}
            if(scene==null){_inspector.Clear(error);_lastSnapClashId="";return;}
            _inspector.Display(scene);
        }
        private void TogglePopOut()
        {
            if(_popout!=null){_popout.Activate();return;}
            _inspectorHost.Child=null;
            var dock=Btn("Inspector is floating · Dock here",BgCard,TxtW);dock.Click+=(_,__)=>_popout?.Close();_inspectorHost.Child=dock;
            _popout=new Window { Title="Clash inspector · close to dock",Width=1050,Height=760,MinWidth=380,MinHeight=400,Background=Bg,Content=_inspector,Owner=Window.GetWindow(this),ShowInTaskbar=false };
            _popout.Closed+=(_,__)=>{var window=_popout;_popout=null;if(window!=null)window.Content=null;if(!_closing)_inspectorHost.Child=_inspector;};
            _popout.Show();
        }
        private void SaveLayout()
        {
            if(_layout==null)return;
            double sum=_layout.RowDefinitions[3].ActualHeight+_layout.RowDefinitions[5].ActualHeight;
            if(sum>0)_preferences.ListRatio=_layout.RowDefinitions[3].ActualHeight/sum;
            _preferences.Save();
        }
        internal void VerifyInspectorLayout(LiveClashDto clash,InspectionScene scene,string folder)
        {
            // Opt-in Revit verification calls this on disposable fixture projects.
            double width=Width,height=Height;string mode=_preferences.Mode;
            _preferences.PersistenceEnabled=false;
            try
            {
                _refreshing=true;_selected=clash;_rowsPanel.SelectedItem=clash;_refreshing=false;
                UpdateDetails(clash);_inspector.Display(scene);Width=380;Height=800;UpdateLayout();
                if(_statusColumn.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Narrow status column was not hidden.");
                if(_inspector.ActualHeight<230)throw new InvalidOperationException("Inspector lost its available height.");
                void Capture(FrameworkElement element,string name)
                {
                    element.UpdateLayout();
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using(var file=System.IO.File.Create(System.IO.Path.Combine(folder,name)))encoder.Save(file);
                }
                Capture(this,"radar-narrow.png");
                _inspector.SetMode("triple");TogglePopOut();_popout!.UpdateLayout();Capture(_inspector,"inspector-floating.png");
                _popout.Close();
                if(!ReferenceEquals(_inspectorHost.Child,_inspector))throw new InvalidOperationException("Floating inspector failed to redock.");
                Width=900;UpdateLayout();
                if(_statusColumn.Visibility!=Visibility.Visible)throw new InvalidOperationException("Wide status column did not return.");
            }
            finally
            {
                _refreshing=false;_popout?.Close();Width=width;Height=height;_inspector.SetMode(mode);_preferences.PersistenceEnabled=true;
            }
        }
    }
}
