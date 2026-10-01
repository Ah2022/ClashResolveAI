using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClashResolveAI.Inspection
{
    public sealed class ClashInspector : Grid
    {
        public readonly InspectorPreferences Preferences;
        private readonly Grid _views=new Grid();
        private readonly InspectionViewport[] _ports={new InspectionViewport(),new InspectionViewport(),new InspectionViewport()};
        private readonly TextBlock _notice=new TextBlock { Foreground=Brushes.LightGray,FontSize=10,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(7) };
        private readonly Dictionary<string,Button> _modes=new Dictionary<string,Button>();
        private readonly DispatcherTimer _save=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(350) };
        private InspectionScene? _scene;
        private ComboBox _comparison=null!;
        private bool _settingMode;
        public event Action? PopOutRequested;
        public event Action<InspectionScene>? PinRequested;
        public event Action<bool>? NavigateRequested;
        public string Mode => Preferences.Mode;
        public ClashInspector(InspectorPreferences preferences)
        {
            Preferences=preferences.Normalize();Background=Brushes.Transparent;
            RowDefinitions.Add(new RowDefinition { Height=new GridLength(Preferences.ControlsHeight),MinHeight=76,MaxHeight=116 });
            RowDefinitions.Add(new RowDefinition { Height=new GridLength(5) });
            RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star),MinHeight=80 });
            Children.Add(Controls());
            var splitter=new GridSplitter { Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brushes.SlateGray };
            splitter.DragCompleted+=(_,__)=>{Preferences.ControlsHeight=RowDefinitions[0].ActualHeight;SaveSoon();};
            Grid.SetRow(splitter,1);Children.Add(splitter);
            var body=new Grid();body.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });body.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            body.Children.Add(_notice);Grid.SetRow(_views,1);body.Children.Add(_views);Grid.SetRow(body,2);Children.Add(body);
            _save.Tick+=(_,__)=>{_save.Stop();Preferences.Save();};
            foreach(var port in _ports)
            {
                port.SetZoom(Preferences.Zoom);
                port.ZoomChanged+=zoom=>{Preferences.Zoom=zoom;foreach(var other in _ports)if(other!=port)other.SetZoom(zoom);SaveSoon();};
            }
            Unloaded+=(_,__)=>{_save.Stop();Preferences.Save();};
            SetNotice("Select a clash to inspect its geometry. Drag to pan; scroll to zoom.");ArrangeViews();
        }
        private void SaveSoon() { _save.Stop();_save.Start(); }
        private static Button Button(string text,Action action)
        {
            var b=new Button { Content=text,MinHeight=25,Padding=new Thickness(2,2,2,2),Margin=new Thickness(1),FontSize=10,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(35,51,73)),BorderThickness=new Thickness(0) };
            b.ToolTip=text=="Pin"?"Save the current framing as a permanent 3D Revit view":text=="Float"?"Open this inspector in a resizable floating window":text=="Revit"?"Open this clash in Revit":text=="Cut"?"Section view":text;b.Click+=(_,__)=>action();return b;
        }
        private UIElement Controls()
        {
            var root=new Grid { Margin=new Thickness(4) };
            root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(28) });
            var clusters=new Grid();foreach(double w in new[]{1.05,1.0,1.0})clusters.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(w,GridUnitType.Star) });
            void Cluster(int col,string title,UIElement child)
            {
                var box=new GroupBox { Header=title,Foreground=Brushes.LightSteelBlue,FontSize=9,Margin=new Thickness(1),Padding=new Thickness(2,0,2,1),BorderBrush=new SolidColorBrush(Color.FromRgb(48,65,89)),Content=child };
                Grid.SetColumn(box,col);clusters.Children.Add(box);
            }
            var modes=new UniformGrid { Rows=1,Columns=3 };
            foreach(var pair in new[]{("2d","Plan"),("3d","3D"),("section","Cut")})
            {string mode=pair.Item1;var b=Button(pair.Item2,()=>SetMode(mode));modes.Children.Add(b);_modes[mode]=b;}
            Cluster(0,"VIEW",modes);
            var adjust=new UniformGrid { Rows=1,Columns=3 };
            adjust.Children.Add(Button("Fit",()=>{foreach(var p in _ports)p.Fit();}));
            adjust.Children.Add(Button("Turn",()=>{Preferences.Corner=(Preferences.Corner+1)%4;Render();SaveSoon();}));
            var axis=Button("A/B",()=>{Preferences.AxisB=!Preferences.AxisB;Render();SaveSoon();});axis.ToolTip="Align section and isometric views to the other element";adjust.Children.Add(axis);
            Cluster(1,"ADJUST",adjust);
            var actions=new UniformGrid { Rows=2,Columns=1 };
            actions.Children.Add(Button("Float",()=>PopOutRequested?.Invoke()));
            actions.Children.Add(Button("Revit",()=>NavigateRequested?.Invoke(Preferences.Mode!="2d")));
            actions.Rows=1;actions.Columns=3;actions.Children.Add(Button("Pin",()=>{if(_scene!=null)PinRequested?.Invoke(_scene);}));
            Cluster(2,"OPEN / PIN",actions);root.Children.Add(clusters);
            var options=new Grid();options.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });options.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });options.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(65) });options.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            _comparison=new ComboBox { ItemsSource=new[]{"Single","2-up","3-up"},FontSize=10,Height=22,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,2,0),SelectedIndex=Mode=="triple"?2:Mode=="compare"?1:0 };
            _comparison.SelectionChanged+=(_,__)=>{if(!_settingMode)SetMode(_comparison.SelectedIndex==2?"triple":_comparison.SelectedIndex==1?"compare":"3d");};Grid.SetColumn(_comparison,2);options.Children.Add(_comparison);
            var context=new CheckBox { Content="Context",IsChecked=Preferences.Context,Foreground=Brushes.LightGray,FontSize=10,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3) };
            context.Click+=(_,__)=>{Preferences.Context=context.IsChecked==true;Render();SaveSoon();};options.Children.Add(context);
            var focus=new CheckBox { Content="Focus",IsChecked=Preferences.Focus,Foreground=Brushes.LightGray,FontSize=10,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3) };
            focus.Click+=(_,__)=>{Preferences.Focus=focus.IsChecked==true;Render();SaveSoon();};Grid.SetColumn(focus,1);options.Children.Add(focus);
            var slider=new Slider { Minimum=1,Maximum=20,Value=Preferences.PaddingFeet,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,4,0),ToolTip="Context / section depth" };
            slider.ValueChanged+=(_,__)=>{Preferences.PaddingFeet=slider.Value;slider.ToolTip=$"Context radius: {slider.Value*.3048:F1} m";Render();SaveSoon();};Grid.SetColumn(slider,3);options.Children.Add(slider);
            Grid.SetRow(options,1);root.Children.Add(options);return root;
        }
        public void SetMode(string mode) { Preferences.Mode=mode;Preferences.Normalize();_settingMode=true;_comparison.SelectedIndex=Mode=="triple"?2:Mode=="compare"?1:0;_settingMode=false;ArrangeViews();Render();SaveSoon(); }
        private void ArrangeViews()
        {
            _views.Children.Clear();_views.ColumnDefinitions.Clear();
            int count=Mode=="triple"?3:Mode=="compare"?2:1;
            for(int i=0;i<count;i++)
            {
                _views.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                var cell=new Grid { Margin=new Thickness(2) };
                cell.RowDefinitions.Add(new RowDefinition { Height=new GridLength(18) });cell.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
                cell.Children.Add(new TextBlock { Text=count>1?new[]{"PLAN · XY","3D","SECTION"}[i]:Mode.ToUpperInvariant(),Foreground=Brushes.LightSteelBlue,FontSize=9,Margin=new Thickness(5,1,0,0) });
                // Detach the actual viewport before moving between layout containers.
                if(_ports[i].Parent is Panel old)old.Children.Remove(_ports[i]);
                Grid.SetRow(_ports[i],1);cell.Children.Add(_ports[i]);Grid.SetColumn(cell,i);_views.Children.Add(cell);
            }
            foreach(var mode in _modes)mode.Value.Background=new SolidColorBrush(mode.Key==Mode?Color.FromRgb(34,111,155):Color.FromRgb(35,51,73));
        }
        public void Display(InspectionScene scene) { _scene=scene;SetNotice(scene.Notice);Render(); }
        public void SetNotice(string text) { _notice.Text=text;_notice.ToolTip=text;_notice.MaxHeight=32; }
        public void Clear(string message="Select a clash to inspect its geometry.") { _scene=null;foreach(var p in _ports)p.Clear();SetNotice(message); }
        private void Render()
        {
            if(_scene==null)return;
            int count=Mode=="triple"?3:Mode=="compare"?2:1;
            for(int i=0;i<count;i++)_ports[i].Display(_scene,count>1?new[]{"2d","3d","section"}[i]:Mode,Preferences.Corner,Preferences.AxisB,Preferences.Context,Preferences.Focus,Preferences.PaddingFeet);
        }
    }
}
