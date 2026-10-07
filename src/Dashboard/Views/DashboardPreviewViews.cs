using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClashResolveAI.Inspection;
using ClashResolveAI.Dashboard.Domain;
namespace ClashResolveAI.Dashboard
{
    public partial class DashboardWindow
    {
        private readonly ClashInspector _panelPreview=new ClashInspector(new InspectorPreferences {Mode="compare",PersistenceEnabled=false,Context=false,Focus=false,PaddingFeet=1});
        private readonly DispatcherTimer _previewDelay=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};
        private string _previewKey="";private int _previewGeneration;private bool _previewClosed;
        private UIElement BuildPreviewPanel()
        {
            var panel=new DockPanel();var bar=new WrapPanel();DockPanel.SetDock(bar,Dock.Top);panel.Children.Add(bar);
            bar.Children.Add(new TextBlock {Text="CLASH PREVIEW · current model",Margin=new Thickness(8)});
            bar.Children.Add(Button("Reload preview",()=>{_previewKey="";QueuePreview(rowForNavigation());}));
            _panelPreview.Height=300;panel.Children.Add(_panelPreview);
            _panelPreview.NavigateRequested+=three=>Safe(()=>_actions.Navigate(_vm.SelectedId,three));
            _panelPreview.PopOutRequested+=()=>Safe(()=>_actions.Inspect(_vm.SelectedId));
            _panelPreview.PinRequested+=scene=>Safe(()=>_actions.PinPreview(_vm.SelectedId,scene,_panelPreview.Preferences));
            _previewDelay.Tick+=(_,__)=>{_previewDelay.Stop();RequestPreview();};
            Closed+=(_,__)=>{_previewClosed=true;++_previewGeneration;_previewDelay.Stop();_panelPreview.Clear();};
            return panel;
        }
        private void QueuePreview(ClashObservation? row)
        {
            string key=$"{_vm.Snapshot.DocumentKey}|{_vm.Snapshot.Selected?.ScanId}|{_vm.Snapshot.IsCurrent}|{row?.ClashId}|{_vm.Snapshot.RequiresFullScan}|{_vm.Snapshot.Metrics.Stale}";
            if(key==_previewKey)return;_previewKey=key;++_previewGeneration;_previewDelay.Stop();
            if(row==null){_panelPreview.Clear();return;}
            if(!_vm.Snapshot.IsCurrent){_panelPreview.Clear("Historical capture: see the stored snapshot in Viewpoint. Current elements cannot reconstruct historical geometry.");return;}
            if(_vm.Snapshot.RequiresFullScan||_vm.Snapshot.Metrics.Stale>0){_panelPreview.Clear("Model changed. Run Full Scan to refresh the panel preview.");return;}
            _panelPreview.Clear("Loading copied clash geometry…");_previewDelay.Start();
        }
        private void RequestPreview()
        {
            var id=_vm.SelectedId;int generation=_previewGeneration;
            try {_actions.Preview(id,(scene,error)=>Dispatcher.BeginInvoke(new Action(()=>{
                if(_previewClosed||generation!=_previewGeneration||id!=_vm.SelectedId)return;
                if(!_vm.Snapshot.IsCurrent||_vm.Snapshot.RequiresFullScan||_vm.Snapshot.Metrics.Stale>0){_panelPreview.Clear("Model changed. Run Full Scan to refresh.");return;}
                if(scene==null)_panelPreview.Clear(error);else _panelPreview.Display(scene);
            })));}catch(Exception ex){_panelPreview.Clear(ex.Message);}
        }
    }
}
