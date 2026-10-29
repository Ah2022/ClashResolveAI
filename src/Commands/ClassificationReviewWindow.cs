using ClashResolveAI.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClashResolveAI.Commands
{
    internal sealed class ClassificationReviewWindow : Window
    {
        internal ClassificationReviewModel Model { get; }
        internal bool InventoryReady { get; private set; }
        private readonly DataGrid _review,_mappings;
        private readonly TextBlock _status;
        private readonly TextBox _search;
        private readonly CheckBox _onlyReview;
        private readonly Button _save;
        private readonly DispatcherTimer _timer;
        private IEnumerator<ClassificationReviewSample>? _inventory;
        private readonly Action? _saved;
        private bool _closing;
        private static Brush Light=>new SolidColorBrush(Color.FromRgb(232,237,245));
        private static Brush Panel=>new SolidColorBrush(Color.FromRgb(24,32,48));
        internal ClassificationReviewWindow(ScanPlan plan,ProjectClassificationStore store,Func<IEnumerator<ClassificationReviewSample>> inventory,Action? saved=null)
        {
            Model=new ClassificationReviewModel(plan,store);_saved=saved;
            Title="Classification review — project mappings";Width=1260;Height=780;MinWidth=900;MinHeight=520;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(15,20,32));
            var root=new DockPanel {Margin=new Thickness(16)};
            var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
            heading.Children.Add(Text("Review classifications before scanning",19));
            heading.Children.Add(Text("Unknown generic models need an explicit mapping. Suggestions do not change the current classification. Counts are unique elements in the selected source documents; repeated instances share metadata."));
            heading.Children.Add(Text("Project: "+plan.DocumentKey+"\nMappings: "+Model.StoragePath,11));
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            _status=Text("Reading copied classification metadata…");footer.Children.Add(_status);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);
            _save=ActionButton("Save project mappings",()=>Save());_save.IsEnabled=false;actions.Children.Add(_save);actions.Children.Add(ActionButton("Close",()=>Close()));
            var tabs=new TabControl {Margin=new Thickness(0,12,0,8),Background=Panel};root.Children.Add(tabs);
            var reviewPanel=new DockPanel {Background=Panel};tabs.Items.Add(new TabItem {Header="Classification review",Content=reviewPanel});
            var tools=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,8)};DockPanel.SetDock(tools,Dock.Top);reviewPanel.Children.Add(tools);
            _search=new TextBox {Width=230,Height=28,Margin=new Thickness(4),ToolTip="Search category, family/type or source model"};tools.Children.Add(_search);
            _onlyReview=new CheckBox {Content="Unknown / lower confidence",Foreground=Light,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,8,0)};tools.Children.Add(_onlyReview);
            tools.Children.Add(ActionButton("Confirm",()=>WithRow(row=>Model.Confirm(row))));tools.Children.Add(ActionButton("Remap…",()=>WithRow(row=>Edit(null,row))));
            tools.Children.Add(ActionButton("Exclude",()=>WithRow(row=>Model.Confirm(row,exclude:true))));
            _review=Table();reviewPanel.Children.Add(_review);
            Column(_review,"Category","Category",140);Column(_review,"Family/type","FamilyType",180);Column(_review,"Current classification","CurrentClassification",135);Column(_review,"Confidence","Confidence",110);
            var factory=new FrameworkElementFactory(typeof(Button));factory.SetBinding(ContentControl.ContentProperty,new Binding("Action"));factory.SetValue(Control.PaddingProperty,new Thickness(6,3,6,3));
            factory.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,args)=>{if((sender as FrameworkElement)?.DataContext is ClassificationReviewRow row)Run(()=>Act(row));}));
            _review.Columns.Add(new DataGridTemplateColumn {Header="Action",Width=140,CellTemplate=new DataTemplate {VisualTree=factory}});
            Column(_review,"Suggestion — confirm to apply","SuggestedClassification",200);Column(_review,"Elements","Count",70);Column(_review,"Included / exclusion reason","Inclusion",280);
            Column(_review,"Evidence","Evidence",290);Column(_review,"System type","SystemType",170);Column(_review,"System classification","SystemClassification",160);
            Column(_review,"Models / instances","Sources",240);Column(_review,"Review status","ReviewStatus",180);
            _search.TextChanged+=(_,__)=>Filter();_onlyReview.Checked+=(_,__)=>Filter();_onlyReview.Unchecked+=(_,__)=>Filter();
            var mappingPanel=new DockPanel {Background=Panel};tabs.Items.Add(new TabItem {Header="Project mappings",Content=mappingPanel});
            _mappings=Table();_mappings.ItemsSource=Model.Mappings;
            var mappingTools=new StackPanel {Orientation=Orientation.Horizontal};DockPanel.SetDock(mappingTools,Dock.Top);mappingPanel.Children.Add(mappingTools);
            mappingTools.Children.Add(ActionButton("Add mapping…",()=>Edit(null,null)));
            mappingTools.Children.Add(ActionButton("Edit mapping…",()=>{if(_mappings.SelectedItem is ClassificationMapping m)Edit(m,null);}));
            mappingTools.Children.Add(ActionButton("Remove from draft",()=>{if(_mappings.SelectedItem is ClassificationMapping m)Model.Remove(m);}));
            mappingTools.Children.Add(ActionButton("Reload saved",()=>{if(!Model.Dirty||MessageBox.Show(this,"Discard unsaved mapping edits and reload the saved project mappings?","Reload mappings",MessageBoxButton.YesNo)==MessageBoxResult.Yes)Model.Reload();}));
            mappingTools.Children.Add(ActionButton("Import draft…",()=>Import()));mappingTools.Children.Add(ActionButton("Export draft…",()=>Export()));
            var help=Text("Higher priority wins; then the more specific match. Equal conflicting matches remain Unknown. Imports replace the draft only. Save project mappings applies the reviewed changes.");DockPanel.SetDock(help,Dock.Bottom);mappingPanel.Children.Add(help);mappingPanel.Children.Add(_mappings);
            Column(_mappings,"Category ID (blank = any)","CategoryId",155);Column(_mappings,"Family/type condition","FamilyTypeContains",210);Column(_mappings,"Match","MatchMode",80);
            Column(_mappings,"Classification","Discipline",130);Column(_mappings,"Exclude","Exclude",65);Column(_mappings,"Priority","Priority",65);
            Column(_mappings,"System type constraint","SystemTypeEquals",180);Column(_mappings,"System classification constraint","SystemClassificationEquals",170);Column(_mappings,"Mapping ID","Id",230);
            var keywords=new StackPanel {Margin=new Thickness(14)};tabs.Items.Add(new TabItem {Header="Keyword fallbacks",Content=new ScrollViewer {Content=keywords,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
            keywords.Children.Add(Text("Visible legacy keyword fallbacks",17));keywords.Children.Add(Text("Source: "+SystemKeywordConfig.ConfigurationPath));
            keywords.Children.Add(Text("Revit category and system classification are primary. These keywords are used only when authoritative classification is unavailable. Project mappings override fallback guesses; generic models still require explicit mappings."));
            keywords.Children.Add(Text("Medical Gas",15));keywords.Children.Add(Text(string.Join(", ",plan.MedicalGasKeywords)));
            keywords.Children.Add(Text("Gravity Drainage",15));keywords.Children.Add(Text(string.Join(", ",plan.GravityDrainageKeywords)));
            Model.Changed+=UpdateStatus;Content=root;
            _timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            _timer.Tick+=(_,__)=> {
                try {
                    var budget=Stopwatch.StartNew();
                    do {
                        if(!_inventory!.MoveNext()) {
                            _timer.Stop();_inventory.Dispose();_inventory=null;Model.Refresh();InventoryReady=true;
                            _review.ItemsSource=Model.Rows;Filter();UpdateStatus();return;
                        }
                        Model.AddSample(_inventory.Current);
                    } while(budget.ElapsedMilliseconds<25);
                    _status.Text="Reading classification metadata: "+Model.ElementCount+" source elements… Close cancels collection.";
                } catch(Exception ex){_timer.Stop();_inventory?.Dispose();_inventory=null;_status.Text="Classification inventory unavailable: "+ex.Message;}
            };
            Loaded+=(_,__)=>{try {_inventory=inventory();_timer.Start();}catch(Exception ex){_status.Text=ex.Message;}};
            Closing+=(_,e)=> {
                if(!_closing&&Model.Dirty&&MessageBox.Show(this,"Discard unsaved project mapping edits?","Close classification review",MessageBoxButton.YesNo)!=MessageBoxResult.Yes){e.Cancel=true;return;}
                _closing=true;_timer.Stop();_inventory?.Dispose();_inventory=null;
            };
        }
        private static TextBlock Text(string value,double size=12)=>new TextBlock {Text=value,FontSize=size,Foreground=Light,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,5)};
        private Button ActionButton(string label,Action action)
        {
            var button=new Button {Content=label,Padding=new Thickness(10,5,10,5),Margin=new Thickness(3)};button.Click+=(_,__)=>Run(action);return button;
        }
        private static DataGrid Table()
        {
            var grid=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,
                SelectionMode=DataGridSelectionMode.Single,Background=Panel,Foreground=Light,RowBackground=Panel,AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(31,41,59)),
                GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,BorderBrush=Brushes.DimGray,HeadersVisibility=DataGridHeadersVisibility.Column};
            var style=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));style.Setters.Add(new Setter(Control.BackgroundProperty,Panel));style.Setters.Add(new Setter(Control.ForegroundProperty,Light));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(7)));grid.ColumnHeaderStyle=style;return grid;
        }
        private static void Column(DataGrid grid,string header,string binding,double width)=>grid.Columns.Add(new DataGridTextColumn {Header=header,Binding=new Binding(binding),Width=width});
        private void Run(Action action)
        {
            try {action();UpdateStatus();}
            catch(Exception ex) {
                Diagnostics.Log("Classification review action failed",ex);
                if(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_SCANFILTER")=="1")throw;
                MessageBox.Show(this,ex.Message,"Classification review");
            }
        }
        private void WithRow(Action<ClassificationReviewRow> action){if(InventoryReady&&_review.SelectedItem is ClassificationReviewRow row)action(row);}
        private void Act(ClassificationReviewRow row)
        {
            if(row.Classification.Discipline==Discipline.Unknown&&row.Suggestion.Discipline==Discipline.Unknown){Edit(null,row);return;}
            if(row.Classification.Confidence>=.9&&row.Classification.Discipline!=Discipline.Unknown)Model.Keep(row);else Model.Confirm(row);
        }
        private void Filter()
        {
            if(_review.ItemsSource==null)return;
            string text=_search.Text.Trim();var view=CollectionViewSource.GetDefaultView(_review.ItemsSource);
            view.Filter=item=>item is ClassificationReviewRow row&&(_onlyReview.IsChecked!=true||row.Classification.Discipline==Discipline.Unknown||row.Classification.Confidence<.9)&&
                (text==""||new[]{row.Category,row.FamilyType,row.Sources,row.SystemType,row.CurrentClassification}.Any(s=>s.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0));
        }
        private void UpdateStatus()
        {
            _save.IsEnabled=InventoryReady&&Model.Dirty;
            _status.Text=$"{Model.ElementCount:N0} unique source elements · {Model.Rows.Count:N0} classification groups · {Model.NeedsReview:N0} need review · {Model.Mappings.Count:N0} project mappings"+(Model.Dirty?" · Unsaved draft":" · Saved state");Filter();
        }
        private void Edit(ClassificationMapping? mapping,ClassificationReviewRow? row)
        {
            int priority=100;
            if(row!=null)foreach(var match in Model.Mappings.Where(m=>m.Matches(row.Input)))priority=Math.Max(priority,match.Priority==int.MaxValue?int.MaxValue:match.Priority+1);
            var dialog=new ClassificationMappingEditor(Model.Rows,mapping,row,priority) {Owner=this};if(dialog.ShowDialog()==true)Model.Upsert(dialog.Result!);
        }
        private void Save(){Model.Save();_saved?.Invoke();}
        private void Import(){var dialog=new OpenFileDialog {Filter="Classification mappings (*.json)|*.json",Title="Import mappings into this project's draft"};if(dialog.ShowDialog(this)==true)Model.ImportDraft(dialog.FileName);}
        private void Export(){var dialog=new SaveFileDialog {Filter="Classification mappings (*.json)|*.json",FileName="ClassificationMappings.json",Title="Export current mapping draft"};if(dialog.ShowDialog(this)==true)Model.ExportDraft(dialog.FileName);}
    }

    internal sealed class ClassificationMappingEditor : Window
    {
        private sealed class CategoryChoice {public long? Id;public string Name="";public override string ToString()=>Name;}
        internal ClassificationMapping? Result { get; private set; }
        internal ClassificationMappingEditor(IEnumerable<ClassificationReviewRow> rows,ClassificationMapping? mapping,ClassificationReviewRow? row,int suggestedPriority=100)
        {
            Title=mapping==null?"Add project classification mapping":"Edit project classification mapping";Width=540;SizeToContent=SizeToContent.Height;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            var panel=new StackPanel {Margin=new Thickness(18)};Content=panel;
            void Label(string text)=>panel.Children.Add(new TextBlock {Text=text,Margin=new Thickness(0,9,0,3)});
            Label("Category (optional)");
            var categories=rows.GroupBy(r=>r.CategoryId).Select(g=>new CategoryChoice {Id=g.Key,Name=g.First().Category+" ["+g.Key+"]"}).ToList();
            long? categoryId=mapping?.CategoryId??row?.CategoryId;
            if(categoryId.HasValue&&!categories.Any(c=>c.Id==categoryId))categories.Add(new CategoryChoice {Id=categoryId,Name="Category ["+categoryId+"]"});
            categories.Insert(0,new CategoryChoice {Name="Any supported category"});var category=new ComboBox {ItemsSource=categories,SelectedItem=categories.First(c=>c.Id==categoryId)};panel.Children.Add(category);
            Label("Family/type condition (optional when a category is selected)");var family=new TextBox {Text=mapping?.FamilyTypeContains??row?.FamilyType??""};panel.Children.Add(family);
            var match=new ComboBox {ItemsSource=Enum.GetValues(typeof(FamilyTypeMatch)),SelectedItem=mapping?.MatchMode??(row==null?FamilyTypeMatch.Contains:FamilyTypeMatch.Exact),Margin=new Thickness(0,5,0,0)};panel.Children.Add(match);
            Label("Classification");var disciplines=Enum.GetValues(typeof(Discipline)).Cast<Discipline>().Where(d=>d!=Discipline.Unknown).ToArray();
            var chosen=mapping?.Discipline??(row?.Suggestion.Discipline!=Discipline.Unknown?row?.Suggestion.Discipline:row?.Classification.Discipline);
            var discipline=new ComboBox {ItemsSource=disciplines,SelectedItem=chosen.HasValue&&chosen!=Discipline.Unknown?(object)chosen.Value:null};panel.Children.Add(discipline);
            var exclude=new CheckBox {Content="Exclude matching elements",IsChecked=mapping?.Exclude??false,Margin=new Thickness(0,8,0,0)};panel.Children.Add(exclude);
            Label("Priority (higher wins)");var priority=new TextBox {Text=(mapping?.Priority??suggestedPriority).ToString(System.Globalization.CultureInfo.InvariantCulture)};panel.Children.Add(priority);
            var limitType=new CheckBox {Content="Match exact system type (empty means unassigned)",IsChecked=mapping==null?row!=null:mapping.SystemTypeEquals!=null,Margin=new Thickness(0,9,0,3)};panel.Children.Add(limitType);
            var systemType=new TextBox {Text=mapping?.SystemTypeEquals??row?.SystemType??""};panel.Children.Add(systemType);
            var limitClass=new CheckBox {Content="Match exact system classification (empty means unassigned)",IsChecked=mapping==null?row!=null:mapping.SystemClassificationEquals!=null,Margin=new Thickness(0,9,0,3)};panel.Children.Add(limitClass);
            var systemClass=new TextBox {Text=mapping?.SystemClassificationEquals??row?.SystemClassification??""};panel.Children.Add(systemClass);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};panel.Children.Add(buttons);
            buttons.Children.Add(new Button {Content="Cancel",IsCancel=true,Padding=new Thickness(14,5,14,5),Margin=new Thickness(4)});
            var apply=new Button {Content="Apply to draft",IsDefault=true,Padding=new Thickness(14,5,14,5),Margin=new Thickness(4)};buttons.Children.Add(apply);
            apply.Click+=(_,__)=> {
                try {
                    if(!int.TryParse(priority.Text,out int value))throw new ArgumentException("Priority must be a whole number.");
                    var selectedDiscipline=discipline.SelectedItem is Discipline valueDiscipline?valueDiscipline:Discipline.Unknown;
                    if(exclude.IsChecked!=true&&selectedDiscipline==Discipline.Unknown)throw new ArgumentException("Select a classification, or explicitly exclude these elements.");
                    Result=new ClassificationMapping(mapping?.Id??Guid.NewGuid().ToString("N"),exclude.IsChecked==true?Discipline.Unknown:selectedDiscipline,
                        family.Text,(category.SelectedItem as CategoryChoice)?.Id,exclude.IsChecked==true,value,(FamilyTypeMatch)match.SelectedItem,limitType.IsChecked==true?systemType.Text:null,limitClass.IsChecked==true?systemClass.Text:null);
                    DialogResult=true;
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"Mapping condition");}
            };
        }
    }
}
