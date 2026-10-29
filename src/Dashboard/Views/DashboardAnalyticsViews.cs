using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClashResolveAI.Dashboard.Application;
namespace ClashResolveAI.Dashboard
{
    public partial class DashboardWindow
    {
        private readonly StackPanel _analytics=new StackPanel();
        private UIElement BuildAnalytics()=>new ScrollViewer {Content=_analytics,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        private void RefreshAnalytics()
        {
            _analytics.Children.Clear();var rows=_vm.Visible();var history=_vm.AnalyticsHistory();var data=DashboardAnalyticsService.Calculate(_vm.Snapshot,rows,history,DateTime.UtcNow);
            _analytics.Children.Add(AnalyticsText(data.Context+"\nFilters: "+FilterContext()+"\n"+data.Resolution));
            _analytics.Children.Add(AnalyticsText($"Actionable open: {data.Metrics.Open} · confirmed critical: {data.Metrics.Critical} · observed unverified: {data.Metrics.Unverified}. Click a bar to inspect exactly its identities. Clear issue filters to restore the full scope."));
            var trend=Table();Columns(trend,"Version","Baseline","Open","New","Resolved","Reopened","Coverage");trend.Columns.Last().Width=new DataGridLength(1,DataGridLengthUnitType.Star);trend.Height=200;trend.ItemsSource=data.Trend;_analytics.Children.Add(AnalyticsText("Saved scan trend for identities in the selected filtered cohort; each row compares consecutive saved versions. Coverage warnings remain part of every comparison."));_analytics.Children.Add(trend);
            var browse=Button("Browse selected trend version",()=>{if(trend.SelectedItem is ScanTrend point){_vm.ScanId="snapshot:"+point.ScanId;_vm.ComparisonId="";_vm.DrillIds=null;_vm.Filters.Clear();_vm.Search="";_vm.GroupId="";_vm.QuickFilter="";_vm.ChangedOnly=false;_vm.ResultTypes=Core.ResultTypes.All;Refresh();SelectIssues();}});_analytics.Children.Add(browse);
            foreach(var chart in data.Charts){_analytics.Children.Add(AnalyticsText(chart.Key));if(chart.Value.Count==0){_analytics.Children.Add(AnalyticsText("No matching evidence."));continue;}
                int max=chart.Value.Max(b=>b.Count);foreach(var bucket in chart.Value.Take(30)){
                    var panel=new Grid();panel.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(450)});panel.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
                    panel.Children.Add(new TextBlock {Text=bucket.Label+" · "+bucket.Count,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=bucket.Label,Margin=new Thickness(5)});
                    var bar=new Border {Height=18,Width=Math.Max(3,500.0*bucket.Count/max),Background=new SolidColorBrush(Color.FromRgb(59,149,170)),HorizontalAlignment=HorizontalAlignment.Left};Grid.SetColumn(bar,1);panel.Children.Add(bar);
                    var button=Button("",()=>{_vm.DrillIds=new HashSet<string>(bucket.Ids);ApplyFilters();SelectIssues();});button.Content=panel;_analytics.Children.Add(button);
                }if(chart.Value.Count>30)_analytics.Children.Add(AnalyticsText($"Showing the largest 30 of {chart.Value.Count} buckets; table/export scope still includes all issues."));
            }
        }
        private static TextBlock AnalyticsText(string text)=>new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12),Foreground=Brushes.White};
        private string FilterContext()=>"Search="+_vm.Search+"; group="+_vm.GroupId+"; quick="+_vm.QuickFilter+"; types="+_vm.ResultTypes+"; changedOnly="+_vm.ChangedOnly+"; identity subset="+(_vm.DrillIds?.Count.ToString()??"all")+"; "+string.Join("; ",_vm.Filters.Where(f=>f.Value!="").Select(f=>f.Key+"="+f.Value));
        public void SelectAnalytics(){_tabs.SelectedIndex=4;RefreshAnalytics();}
    }
}
