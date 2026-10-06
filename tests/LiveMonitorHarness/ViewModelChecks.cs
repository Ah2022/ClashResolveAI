using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using ClashResolveAI.Inspection;

static class ViewModelChecks
{
    public static int Run(LiveClashDto row)
    {
        int passed=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var store=RadarDataStore.Instance;store.Activate(row.HostDocumentKey);store.Clear();store.BeginSession();store.ThisSession=false;store.AddClashes(new[]{row});
        var actions=new FakeActions();var posted=new Queue<Action>();
        var vm=new ClashRadarViewModel(store,actions,a=>posted.Enqueue(a));
        vm.SetSession(row.HostDocumentKey,row.SessionGeneration,true,MonitorMode.Live);vm.Selected=row;
        Check(vm.Show3DCommand.CanExecute(null)&&vm.InspectCommand.CanExecute(null),"Current verified DTO enables navigation and inspection commands");
        vm.Show3DCommand.Execute(null);Check(actions.Navigations==1,"View model sends navigation through its action boundary");
        vm.SetSession("another",row.SessionGeneration,true,MonitorMode.Live);vm.Show3DCommand.Execute(null);
        Check(actions.Navigations==1&&!vm.InspectCommand.CanExecute(null),"Changing documents disables a selected old DTO without API access");
        vm.SetSession(row.HostDocumentKey,row.SessionGeneration,true,MonitorMode.Trigger);
        vm.ModeCommand.Execute(MonitorMode.Off);Check(actions.ModeRequested==MonitorMode.Off&&vm.Mode==MonitorMode.Trigger,"Mode commands wait for gateway acknowledgement instead of changing scheduling optimistically");
        vm.SetStatus(new LiveScanStatus(LiveScanOutcome.Watching,"Waiting for Check Changes",4));
        vm.CheckChangesCommand.Execute(null);Check(actions.Checks==1&&actions.Document==row.HostDocumentKey&&actions.Generation==row.SessionGeneration&&vm.QueueText=="4 queued","Check Changes carries captured document/generation and displays queue depth");
        vm.SetStatus(new LiveScanStatus(LiveScanOutcome.RequiresFullScan,"Full Scan required",4));
        vm.CheckChangesCommand.Execute(null);Check(actions.Checks==1&&!vm.CheckChangesCommand.CanExecute(null),"Full Scan requirement disables manual local check commands");
        vm.SetStatus(new LiveScanStatus(LiveScanOutcome.PausedForFullScan,"Full Scan running; changes retained",4));
        Check(!vm.CheckChangesCommand.CanExecute(null),"Full Scan handoff disables local check commands without exposing a Full Scan cancel");
        vm.SetStatus(new LiveScanStatus(LiveScanOutcome.Checking,"Checking 4 elements",4));
        Check(vm.CheckLabel=="Checking…"&&vm.CancelCommand.CanExecute(null)&&!vm.CheckChangesCommand.CanExecute(null),"Scanning status enables cancel and prevents overlapping check commands");
        vm.SetSession(row.HostDocumentKey,row.SessionGeneration,true,MonitorMode.Off);vm.Show3DCommand.Execute(null);
        Check(vm.QueueDepth==0&&vm.StatusText=="Off"&&!vm.CanInspect&&!vm.CheckChangesCommand.CanExecute(null),"Off displays zero queue and disables live result operations");
        vm.SetSession(row.HostDocumentKey,row.SessionGeneration,true,MonitorMode.Live);vm.Selected=row;vm.IgnoreCommand.Execute(null);
        while(posted.Count>0)posted.Dequeue()();
        Check(vm.Rows.Count==0&&vm.Selected==null&&row.Status!=ClashStatus.Ignored,"Ignore command replaces the stored DTO and clears selection");
        vm.Dispose();store.Clear();Check(posted.Count==0,"Disposed view model unsubscribes store notifications");
        return passed;
    }
    private sealed class FakeActions : IRadarActions
    {
        public int Navigations,Checks;public MonitorMode ModeRequested;public string Document="";public long Generation;
        public bool IsUsable(LiveClashDto row)=>true;
        public bool Check(string document,long generation,LiveScanAction action){Checks++;Document=document;Generation=generation;return true;}
        public void Mode(string document,long generation,MonitorMode mode){ModeRequested=mode;}
        public void Navigate(LiveClashDto row,bool threeD){Navigations++;}
        public void Inspect(LiveClashDto row){}
        public void Pin(LiveClashDto row,RadarPinRequest request){}
        public void Export(IReadOnlyList<LiveClashDto> rows){}
        public void Hide(string document,long generation){}
        public void ExportDiagnostics(string document){}
        public bool ConfirmPurge()=>true;
    }
}
