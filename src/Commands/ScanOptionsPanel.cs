using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClashResolveAI.Commands
{
    internal sealed class ScanOptionsPanel : StackPanel
    {
        internal readonly ComboBox FullMode, LiveMode;
        internal readonly CheckBox LinkToLink, HostToHost, HostToLinks, InsideLinks;
        internal ScanOptionsPanel(AppSettings settings,bool showLive)
        {
            ComboBox Mode(string label,ScanMode mode,bool visible){
                var text=new TextBlock {Text=label,Foreground=Brushes.LightGray,Margin=new Thickness(0,4,0,4)};
                var choice=new ComboBox {Height=28,Margin=new Thickness(0,0,0,10),ItemsSource=new[]{"Hard only","Hard + clearance"},SelectedIndex=mode==ScanMode.HardOnly?0:1};
                if(visible){Children.Add(text);Children.Add(choice);}return choice;
            }
            FullMode=Mode("Full Scan mode",settings.FullScanMode,true);
            LiveMode=Mode("Live Monitor mode",settings.LiveMode,showLive);
            CheckBox Relationship(string label,bool enabled) {
                var check=new CheckBox {Content=label,IsChecked=enabled,Foreground=Brushes.White,Margin=new Thickness(0,4,0,8)};
                Children.Add(check);return check;
            }
            HostToHost=Relationship("Host ↔ host",settings.ScanHostToHost);
            HostToLinks=Relationship("Host ↔ selected loaded links",settings.ScanHostToLinks);
            InsideLinks=Relationship("Inside selected linked models",settings.FullScanWithinLinks);
            LinkToLink=Relationship("Link ↔ link — explicitly selected pairs only",settings.IncludeLinkToLink);
            Children.Add(new TextBlock {Text="Internal-link scans are independent. Link-to-link never adds pairs automatically; select pairs in Full Scan.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,Margin=new Thickness(0,0,0,12)});
            SetLinksEnabled(settings.FullScanLinkedModels);
        }
        internal void SetLinksEnabled(bool enabled) {HostToLinks.IsEnabled=enabled;InsideLinks.IsEnabled=enabled;LinkToLink.IsEnabled=enabled;}
        internal ScanMode SelectedMode=>FullMode.SelectedIndex==0?ScanMode.HardOnly:ScanMode.HardAndClearance;
        internal bool IncludeLinkToLink=>LinkToLink.IsEnabled&&LinkToLink.IsChecked==true;
        internal void Apply(AppSettings settings,bool includeLive)
        {
            settings.FullScanMode=SelectedMode;
            if(includeLive)settings.LiveMode=LiveMode.SelectedIndex==0?ScanMode.HardOnly:ScanMode.HardAndClearance;
            settings.IncludeLinkToLink=LinkToLink.IsChecked==true;
            settings.ScanHostToHost=HostToHost.IsChecked==true;
            settings.ScanHostToLinks=HostToLinks.IsChecked==true;
            settings.FullScanWithinLinks=InsideLinks.IsChecked==true;
        }
    }
    internal sealed class ScanLinkChoice
    {
        public string InstanceId { get; }
        public string Label { get; }
        public ScanLinkChoice(string instanceId,string label) {InstanceId=instanceId;Label=label;}
        public override string ToString()=>Label;
    }
    internal sealed class FullScanOptionsDialog : Window
    {
        internal readonly ScanOptionsPanel Options;
        private readonly List<Tuple<ScanLinkChoice,CheckBox>> _links=new List<Tuple<ScanLinkChoice,CheckBox>>();
        private readonly List<Tuple<ScanModelPair,CheckBox>> _pairs=new List<Tuple<ScanModelPair,CheckBox>>();
        internal IReadOnlyList<string> SelectedLinkIds=>_links.Where(l=>l.Item2.IsChecked==true).Select(l=>l.Item1.InstanceId).ToArray();
        internal IReadOnlyList<ScanModelPair> SelectedLinkPairs=>_pairs.Where(p=>p.Item2.IsChecked==true).Select(p=>p.Item1).ToArray();
        internal FullScanOptionsDialog(AppSettings settings,string scope,string links,IEnumerable<ScanLinkChoice>? loadedLinks=null,
            Action<IReadOnlyList<string>,ScanMode>? reviewClassification=null)
        {
            Title="Full Scan";Width=620;MaxHeight=820;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.CanResize;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new SolidColorBrush(Color.FromRgb(15,20,32));
            var panel=new StackPanel {Margin=new Thickness(20)};
            panel.Children.Add(new TextBlock {Text=scope+"\nRule set: "+settings.RuleSetName+"\nLinked models: "+(settings.ScanLinkedModels?"included":"excluded"),Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            Options=new ScanOptionsPanel(settings,false);panel.Children.Add(Options);
            if(reviewClassification!=null) {
                var review=new Button {Content="Review classification and project mappings…",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,4,0,10)};
                review.Click+=(_,__)=>reviewClassification(SelectedLinkIds,Options.SelectedMode);panel.Children.Add(review);
            }
            var choices=(loadedLinks ?? Enumerable.Empty<ScanLinkChoice>()).ToArray();
            if(choices.Length>0) {
                panel.Children.Add(new TextBlock {Text="Selected loaded link instances",Foreground=Brushes.White});
                var linkList=new StackPanel();
                foreach(var choice in choices) {
                    var check=new CheckBox {Content=choice.Label,IsChecked=true,Foreground=Brushes.LightGray,Margin=new Thickness(0,3,0,3)};
                    _links.Add(Tuple.Create(choice,check));linkList.Children.Add(check);
                }
                panel.Children.Add(new ScrollViewer {Content=linkList,MaxHeight=140,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
                var pairPanel=new StackPanel {Margin=new Thickness(0,10,0,10)};
                panel.Children.Add(pairPanel);pairPanel.IsEnabled=Options.IncludeLinkToLink;
                Options.LinkToLink.Checked+=(_,__)=>pairPanel.IsEnabled=true;
                Options.LinkToLink.Unchecked+=(_,__)=>pairPanel.IsEnabled=false;
                pairPanel.Children.Add(new TextBlock {Text="Add only the link pairs required for this scan",Foreground=Brushes.White});
                var first=new ComboBox {ItemsSource=choices,SelectedIndex=0,Margin=new Thickness(0,4,0,4)};
                var second=new ComboBox {ItemsSource=choices,SelectedIndex=choices.Length>1?1:0,Margin=new Thickness(0,4,0,4)};
                var add=new Button {Content="Add pair",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,4,10,4)};
                pairPanel.Children.Add(first);pairPanel.Children.Add(second);pairPanel.Children.Add(add);
                var pairList=new StackPanel();pairPanel.Children.Add(pairList);
                add.Click+=(_,__)=> {
                    var a=first.SelectedItem as ScanLinkChoice;var b=second.SelectedItem as ScanLinkChoice;
                    if(a==null||b==null||a.InstanceId==b.InstanceId)return;
                    var existing=_pairs.FirstOrDefault(p=>p.Item1.Matches(a.InstanceId,b.InstanceId));
                    if(existing!=null){existing.Item2.IsChecked=true;return;}
                    var check=new CheckBox {Content=a.Label+" ↔ "+b.Label,IsChecked=true,Foreground=Brushes.LightGray,Margin=new Thickness(0,4,0,4)};
                    _pairs.Add(Tuple.Create(new ScanModelPair(a.InstanceId,b.InstanceId),check));pairList.Children.Add(check);
                };
            }
            panel.Children.Add(new TextBlock {Text=links,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,MaxHeight=180,Margin=new Thickness(0,0,0,12)});
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=new Button {Content="Cancel",IsCancel=true,Padding=new Thickness(16,6,16,6),Margin=new Thickness(4)};
            var start=new Button {Content="Start scan",IsDefault=true,Padding=new Thickness(16,6,16,6),Margin=new Thickness(4)};
            start.Click+=(_,__)=> {
                if(loadedLinks!=null) {
                    if(Options.IncludeLinkToLink && SelectedLinkPairs.Count==0){MessageBox.Show(this,"Select at least one explicit link pair, or turn off link-to-link.","Scan relationships");return;}
                    if(SelectedLinkPairs.Any(p=>!SelectedLinkIds.Contains(p.Source)||!SelectedLinkIds.Contains(p.Target)) && Options.IncludeLinkToLink){MessageBox.Show(this,"Both endpoints of each selected pair must be selected loaded links.","Scan relationships");return;}
                    if(Options.HostToHost.IsChecked!=true && !(SelectedLinkIds.Count>0 && ((Options.HostToLinks.IsEnabled && Options.HostToLinks.IsChecked==true)||(Options.InsideLinks.IsEnabled && Options.InsideLinks.IsChecked==true))) && !Options.IncludeLinkToLink){MessageBox.Show(this,"Select at least one scan relationship.","Scan relationships");return;}
                }
                DialogResult=true;
            };buttons.Children.Add(cancel);buttons.Children.Add(start);panel.Children.Add(buttons);
            Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        }
    }
}
