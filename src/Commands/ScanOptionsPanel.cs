using ClashResolveAI.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClashResolveAI.Commands
{
    internal sealed class ScanOptionsPanel : StackPanel
    {
        internal readonly ComboBox FullMode, LiveMode;
        internal readonly CheckBox LinkToLink;
        internal ScanOptionsPanel(AppSettings settings,bool showLive)
        {
            ComboBox Mode(string label,ScanMode mode,bool visible){
                var text=new TextBlock {Text=label,Foreground=Brushes.LightGray,Margin=new Thickness(0,4,0,4)};
                var choice=new ComboBox {Height=28,Margin=new Thickness(0,0,0,10),ItemsSource=new[]{"Hard only","Hard + clearance"},SelectedIndex=mode==ScanMode.HardOnly?0:1};
                if(visible){Children.Add(text);Children.Add(choice);}return choice;
            }
            FullMode=Mode("Full Scan mode",settings.FullScanMode,true);
            LiveMode=Mode("Live Monitor mode",settings.LiveMode,showLive);
            LinkToLink=new CheckBox {Content="Include link-to-link",IsChecked=settings.IncludeLinkToLink,Foreground=Brushes.White,Margin=new Thickness(0,4,0,8)};
            Children.Add(LinkToLink);
            Children.Add(new TextBlock {Text="Off: host against host and loaded links. On: also scan linked sources, including internal link clashes when enabled.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,Margin=new Thickness(0,0,0,12)});
            LinkToLink.IsEnabled=settings.ScanLinkedModels;
        }
        internal ScanMode SelectedMode=>FullMode.SelectedIndex==0?ScanMode.HardOnly:ScanMode.HardAndClearance;
        internal bool IncludeLinkToLink=>LinkToLink.IsEnabled&&LinkToLink.IsChecked==true;
        internal void Apply(AppSettings settings,bool includeLive)
        {
            settings.FullScanMode=SelectedMode;
            if(includeLive)settings.LiveMode=LiveMode.SelectedIndex==0?ScanMode.HardOnly:ScanMode.HardAndClearance;
            settings.IncludeLinkToLink=LinkToLink.IsChecked==true;
        }
    }
    internal sealed class FullScanOptionsDialog : Window
    {
        internal readonly ScanOptionsPanel Options;
        internal FullScanOptionsDialog(AppSettings settings,string scope,string links)
        {
            Title="Full Scan";Width=500;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new SolidColorBrush(Color.FromRgb(15,20,32));
            var panel=new StackPanel {Margin=new Thickness(20)};
            panel.Children.Add(new TextBlock {Text=scope+"\nRule set: "+settings.RuleSetName+"\nLinked models: "+(settings.ScanLinkedModels?"included":"excluded"),Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            Options=new ScanOptionsPanel(settings,false);panel.Children.Add(Options);
            panel.Children.Add(new TextBlock {Text=links,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,MaxHeight=180,Margin=new Thickness(0,0,0,12)});
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=new Button {Content="Cancel",IsCancel=true,Padding=new Thickness(16,6,16,6),Margin=new Thickness(4)};
            var start=new Button {Content="Start scan",IsDefault=true,Padding=new Thickness(16,6,16,6),Margin=new Thickness(4)};
            start.Click+=(_,__)=>DialogResult=true;buttons.Children.Add(cancel);buttons.Children.Add(start);panel.Children.Add(buttons);Content=panel;
        }
    }
}
