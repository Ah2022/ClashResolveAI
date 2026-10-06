using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using ClashResolveAI.Core;
using ClashResolveAI.Inspection;

namespace ClashResolveAI.LiveMonitor
{
    public sealed class RadarCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?,bool> _canExecute;
        public RadarCommand(Action<object?> execute,Func<object?,bool>? canExecute=null){_execute=execute;_canExecute=canExecute??(_=>true);}
        public bool CanExecute(object? parameter)=>_canExecute(parameter);
        public void Execute(object? parameter){if(CanExecute(parameter))_execute(parameter);}
        public event EventHandler? CanExecuteChanged;
        internal void Update()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
    }
    public sealed class RadarPinRequest
    {
        public InspectionScene Scene {get;}
        public InspectorPreferences Preferences {get;}
        public RadarPinRequest(InspectionScene scene,InspectorPreferences preferences){Scene=scene;Preferences=preferences;}
    }
    public interface IRadarActions
    {
        bool IsUsable(LiveClashDto row);
        bool Check(string document,long generation,LiveScanAction action);
        void Mode(string document,long generation,MonitorMode mode);
        void Navigate(LiveClashDto row,bool threeD);
        void Inspect(LiveClashDto row);
        void Pin(LiveClashDto row,RadarPinRequest request);
        void Export(IReadOnlyList<LiveClashDto> rows);
        void Hide(string document,long generation);
        bool ConfirmPurge();
        void ExportDiagnostics(string document);
    }
    // Immutable DTOs and plain session/status values only. No Revit API types.
    public sealed class ClashRadarViewModel : INotifyPropertyChanged,IDisposable
    {
        private readonly RadarDataStore _store;
        private readonly IRadarActions _actions;
        private readonly Action<Action> _dispatch;
        private bool _disposed,_refreshQueued;
        private string _category="All Categories",_document="";
        private long _generation;
        private LiveClashDto? _selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? SelectionChanged;
        public IReadOnlyList<LiveClashDto> Rows {get;private set;}=Array.Empty<LiveClashDto>();
        public IReadOnlyList<string> Categories {get;private set;}=new[]{"All Categories"};
        public IReadOnlyList<MonitorMode> Modes {get;}=new[]{MonitorMode.Live,MonitorMode.Trigger,MonitorMode.Off};
        public MonitorMode Mode {get;private set;}=MonitorMode.Off;
        public bool Running {get;private set;}
        public bool Checking {get;private set;}
        public bool RequiresFullScan {get;private set;}
        public bool FullScanRunning {get;private set;}
        public int QueueDepth {get;private set;}
        public int VisibleCount=>Rows.Count;
        public string StatusText {get;private set;}="Off";
        public string DiagnosticText {get;private set;}="No live check recorded";
        public void SetDiagnostics(LiveDiagnosticSnapshot s){DiagnosticText=$"{s.ChangedElementCount} changed · {s.CandidateCount} candidates · {s.TestedPairCount} pairs tested\n{s.BooleanFailures} Boolean failures · {s.MissingGeometry} missing geometry · {s.UnverifiedCount} unverified\n{s.NewClashCount} new · {s.ResolvedClashCount} resolved · {s.StaleRequestCount} stale requests\nMaximum API slice {s.MaximumApiSliceMilliseconds:F1} ms · scan {s.ScanElapsedMilliseconds:F0} ms";Changed(nameof(DiagnosticText));}
        public string ScopeText {get;private set;}="Affected host elements";
        public void SetScope(string text){ScopeText=text;Changed(nameof(ScopeText));}
        public string QueueText=>$"{QueueDepth} queued";
        public string ModeText=>Mode==MonitorMode.Live?"Live · checking automatically":Mode==MonitorMode.Trigger?"Trigger · waiting for Check Changes":"Off · live checking disabled";
        public string CheckLabel=>Checking?"Checking…":Mode==MonitorMode.Trigger?"Check Changes":"Re-check session";
        public string Category {get=>_category;set{if(_category==value)return;_category=value;Refresh();}}
        public ResultTypes Types {get=>_store.Types;set{_store.Types=value;}}
        public bool ThisSession {get=>_store.ThisSession;set{_store.ThisSession=value;}}
        public LiveClashDto? Selected {get=>_selected;set{if(ReferenceEquals(_selected,value))return;_selected=value;Changed(nameof(Selected));UpdateCommands();SelectionChanged?.Invoke(this,EventArgs.Empty);}}
        public bool CanInspect=>Running&&Mode!=MonitorMode.Off&&Selected!=null&&Selected.HostDocumentKey==_document&&Selected.SessionGeneration==_generation&&Selected.Verification==LiveVerificationState.Verified&&_actions.IsUsable(Selected);
        public RadarCommand Show3DCommand {get;}
        public RadarCommand Show2DCommand {get;}
        public RadarCommand InspectCommand {get;}
        public RadarCommand PinCommand {get;}
        public RadarCommand IgnoreCommand {get;}
        public RadarCommand CheckChangesCommand {get;}
        public RadarCommand CancelCommand {get;}
        public RadarCommand ClearCommand {get;}
        public RadarCommand ExportCommand {get;}
        public RadarCommand ExportDiagnosticsCommand {get;}
        public RadarCommand ModeCommand {get;}
        public RadarCommand HideCommand {get;}
        public RadarCommand PurgeCommand {get;}
        private IEnumerable<RadarCommand> Commands=>new[]{Show3DCommand,Show2DCommand,InspectCommand,PinCommand,IgnoreCommand,CheckChangesCommand,CancelCommand,ClearCommand,ExportCommand,ExportDiagnosticsCommand,ModeCommand,HideCommand,PurgeCommand};
        public ClashRadarViewModel(RadarDataStore store,IRadarActions actions,Action<Action> dispatch)
        {
            _store=store;_actions=actions;_dispatch=dispatch;
            Show3DCommand=new RadarCommand(_=>_actions.Navigate(Selected!,true),_=>CanInspect);
            Show2DCommand=new RadarCommand(_=>_actions.Navigate(Selected!,false),_=>CanInspect);
            InspectCommand=new RadarCommand(_=>_actions.Inspect(Selected!),_=>CanInspect);
            PinCommand=new RadarCommand(p=>_actions.Pin(Selected!,(RadarPinRequest)p!),p=>CanInspect&&p is RadarPinRequest);
            IgnoreCommand=new RadarCommand(_=>{_store.IgnoreClash(Selected!);Selected=null;},_=>Selected!=null&&Selected.HostDocumentKey==_document);
            CheckChangesCommand=new RadarCommand(_=>Submit(LiveScanAction.Ledger),_=>Running&&Mode!=MonitorMode.Off&&!Checking&&!RequiresFullScan&&!FullScanRunning);
            CancelCommand=new RadarCommand(_=>Submit(LiveScanAction.Cancel),_=>Running&&Mode!=MonitorMode.Off&&(Checking||QueueDepth>0));
            ClearCommand=new RadarCommand(_=>Submit(LiveScanAction.Clear),_=>Running&&_document!="");
            ExportCommand=new RadarCommand(_=>_actions.Export(Rows),_=>Rows.Count>0);
            ExportDiagnosticsCommand=new RadarCommand(_=>_actions.ExportDiagnostics(_document),_=>_document!="");
            ModeCommand=new RadarCommand(p=>_actions.Mode(_document,_generation,(MonitorMode)p!),p=>Running&&_document!=""&&p is MonitorMode);
            HideCommand=new RadarCommand(_=>_actions.Hide(_document,_generation),_=>Running&&_document!="");
            PurgeCommand=new RadarCommand(_=>{if(_actions.ConfirmPurge())_store.PurgeNonHard();},_=>Rows.Count>0);
            _store.DataChanged+=OnDataChanged;Refresh();
        }
        private void Submit(LiveScanAction action){if(!_actions.Check(_document,_generation,action))SetMessage("Live request could not be queued",true);}
        private void OnDataChanged(object? sender,EventArgs args){if(_disposed||_refreshQueued)return;_refreshQueued=true;_dispatch(()=>{_refreshQueued=false;if(!_disposed)Refresh();});}
        public void Refresh()
        {
            if(_disposed)return;
            Categories=new[]{"All Categories"}.Concat(_store.GetCategories()).ToList().AsReadOnly();
            if(!Categories.Contains(_category))_category="All Categories";
            Rows=_store.GetVisible().Where(c=>_category=="All Categories"||c.CategoryNameA.Equals(_category,StringComparison.OrdinalIgnoreCase)||c.CategoryNameB.Equals(_category,StringComparison.OrdinalIgnoreCase)).ToList().AsReadOnly();
            var key=Selected?.ClashKey;Selected=Rows.FirstOrDefault(c=>c.ClashKey==key);
            foreach(var name in new[]{nameof(Rows),nameof(Categories),nameof(Category),nameof(VisibleCount),nameof(Types),nameof(ThisSession)})Changed(name);
            UpdateCommands();
        }
        public void SetSession(string document,long generation,bool running,MonitorMode mode)
        {
            if(_document!=document){RequiresFullScan=false;FullScanRunning=false;Checking=false;QueueDepth=0;}
            _document=document;_generation=generation;Running=running;Mode=running?mode:MonitorMode.Off;
            if(!running||Mode==MonitorMode.Off){Checking=false;QueueDepth=0;StatusText="Off";}
            else if(mode==MonitorMode.Trigger&&!Checking)StatusText="Waiting for Check Changes";
            else if(!Checking)StatusText="Watching";
            NotifyState();Refresh();SelectionChanged?.Invoke(this,EventArgs.Empty);
        }
        public void SetStatus(LiveScanStatus status){if(_disposed)return;Checking=status.Outcome==LiveScanOutcome.Checking;FullScanRunning=status.Outcome==LiveScanOutcome.PausedForFullScan;RequiresFullScan=status.RequiresFullScan;QueueDepth=status.QueueDepth;StatusText=status.Reason;NotifyState();}
        public void SetMessage(string message,bool complete){Checking=!complete;StatusText=message;NotifyState();}
        private void NotifyState(){foreach(var name in new[]{nameof(Mode),nameof(Running),nameof(Checking),nameof(RequiresFullScan),nameof(FullScanRunning),nameof(QueueDepth),nameof(QueueText),nameof(StatusText),nameof(ModeText),nameof(CheckLabel)})Changed(name);UpdateCommands();}
        private void Changed(string property)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(property));
        private void UpdateCommands(){if(Show3DCommand!=null)foreach(var command in Commands)command.Update();}
        public void Dispose(){if(_disposed)return;_disposed=true;_store.DataChanged-=OnDataChanged;}
    }
}
