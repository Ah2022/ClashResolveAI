using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using ClashResolveAI.LiveMonitor;

namespace ClashResolveAI.Core
{
    public static class Diagnostics
    {
        private static readonly object Gate=new object(),QueueGate=new object();
        private static readonly Queue<LiveDiagnosticSnapshot> Pending=new Queue<LiveDiagnosticSnapshot>();
        private static readonly ManualResetEventSlim Idle=new ManualResetEventSlim(true);
        private static bool _writing;
        private static long _dropped;
        public static long DroppedLiveRecords=>Interlocked.Read(ref _dropped);
        private static string Folder {
            get {string verification=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")??"";
                return verification!=""?Path.Combine(verification,"diagnostics"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClashResolveAI","Logs");}
        }
        private static void Append(string path,string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const long maximum=2*1024*1024;
            if(File.Exists(path)&&new FileInfo(path).Length+System.Text.Encoding.UTF8.GetByteCount(text)>maximum) {
                if(File.Exists(path+".4"))File.Delete(path+".4");
                for(int i=3;i>=1;i--)if(File.Exists(path+"."+i))File.Move(path+"."+i,path+"."+(i+1));
                File.Move(path,path+".1");
            }
            File.AppendAllText(path,text);
        }
        public static void Log(string message,Exception? error=null) {
            try {lock(Gate)Append(Path.Combine(Folder,"application.log"),DateTime.UtcNow.ToString("O")+" "+message+(error==null?"":" "+error)+Environment.NewLine);}catch{}
        }
        public static void RecordLive(LiveDiagnosticSnapshot snapshot) {
            lock(QueueGate) {
                if(Pending.Count>=2048){Pending.Dequeue();Interlocked.Increment(ref _dropped);}
                Pending.Enqueue(snapshot);
                if(_writing)return;_writing=true;Idle.Reset();ThreadPool.QueueUserWorkItem(_=>Drain());
            }
        }
        private static void Drain() {
            while(true) {
                LiveDiagnosticSnapshot[] batch;
                lock(QueueGate){if(Pending.Count==0){_writing=false;Idle.Set();return;}var records=new List<LiveDiagnosticSnapshot>();while(Pending.Count>0&&records.Count<32)records.Add(Pending.Dequeue());batch=records.ToArray();}
                try {var text=new System.Text.StringBuilder();foreach(var record in batch)text.AppendLine(JsonConvert.SerializeObject(record));lock(Gate)Append(Path.Combine(Folder,"live-monitor.jsonl"),text.ToString());}
                catch{Interlocked.Add(ref _dropped,batch.Length);}
            }
        }
        public static bool FlushLive(TimeSpan timeout)=>Idle.Wait(timeout);
        public static void ExportLive(string document) {
            var dialog=new Microsoft.Win32.SaveFileDialog {Title="Export Live Monitor diagnostics",Filter="Diagnostics JSON|*.json",FileName="LiveMonitor-9.4-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".json"};
            if(dialog.ShowDialog()!=true)return;
            File.WriteAllText(dialog.FileName,JsonConvert.SerializeObject(new {Version="9.4.0",ExportedUtc=DateTime.UtcNow,DroppedRecords=DroppedLiveRecords,Current=LiveDiagnostics.Current(document),Events=LiveDiagnostics.Export(document)},Formatting.Indented));
        }
    }
}
