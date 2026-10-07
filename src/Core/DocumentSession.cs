using Autodesk.Revit.DB;
using ClashResolveAI.Commands;
using ClashResolveAI.Dashboard;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static class DocumentSession
    {
        private sealed class State
        {
            public List<ClashResult> Dashboard = new List<ClashResult>();
            public ResultViewFilter Filter=new ResultViewFilter {Types=ResultViewFilter.Defaults(AppSettings.Load().FullScanMode)};
        }
        private static readonly Dictionary<string, State> States = new Dictionary<string, State>();
        // Cache native document identity for its open lifetime: Save As must not
        // change live operation keys, and managed wrappers may differ.
        private static readonly List<(Document Document,string Key)> OpenDocuments = new List<(Document,string)>();
        private static string _currentKey = "";
        public static string CurrentKey=>_currentKey;
        public static Document? Current { get; private set; }
        public static string Key(Document doc)
        {
            var known=OpenDocuments.FirstOrDefault(entry=>entry.Document.IsValidObject && entry.Document.Equals(doc));
            if(known.Document!=null)return known.Key;
            // Revit can return different managed Document wrappers for the same native
            // model. Never use wrapper reference equality to partition document state.
            string identity = doc.ProjectInformation.UniqueId + "|" +
                (string.IsNullOrEmpty(doc.PathName) ? "unsaved:" + doc.Title : doc.PathName);
            using var sha = System.Security.Cryptography.SHA256.Create();
            string key=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24);
            if(OpenDocuments.Any(entry=>entry.Key==key))key+="-"+Guid.NewGuid().ToString("N");
            OpenDocuments.Add((doc,key));return key;
        }
        public static bool Matches(ClashResult clash, Document doc) => clash.HostDocumentKey == Key(doc);
        public static void Activate(Document doc)
        {
            if (doc.IsFamilyDocument) { LiveMonitorService.Instance.ActivateDocument("");return; }
            string key = Key(doc);
            ScanCoordinator.Observe(doc);
            LiveMonitorService.Instance.ActivateDocument(key);
            if (key == _currentKey) { Current = doc; return; }
            if(ScanCoordinator.Busy&&ScanCoordinator.BusyDocumentKey!=key)ScanCoordinator.Cancel("Active project changed; previous results retained.");
            if (States.TryGetValue(_currentKey, out var old))
            {
                old.Dashboard = ClashDashboard.Instance.Clashes.ToList();
                old.Filter=ClashDashboard.Instance.ViewFilter.Copy();
            }
            bool newSession=!States.TryGetValue(key,out var state);
            if(newSession)state=new State();
            GeometryCacheService.Instance.Clear();
            ClashDatabase.Instance.Open(Key(doc));
            if(newSession){state!.Dashboard=ClashDatabase.Instance.LoadCurrentClashes();States[key]=state;}
            Current = doc;
            _currentKey = key;
            Session.ProjectName = doc.Title;
            Session.RuleSetName = AppSettings.Load().RuleSetName;
            RadarDataStore.Instance.Activate(key);
            ClashDashboard.Instance.RestoreSession(state!.Dashboard,state.Filter);
            Diagnostics.Log("Active document: " + Key(doc));
        }
        public static void Close(Document doc) => CloseKey(Key(doc));
        internal static void PruneClosedDocuments() {
            foreach(var entry in OpenDocuments.Where(entry=>!entry.Document.IsValidObject).ToList())CloseKey(entry.Key);
        }
        private static void CloseKey(string key)
        {
            LiveMonitorService.Instance.CloseDocument(key);
            OpenDocuments.RemoveAll(entry=>entry.Key==key);
            ScanCoordinator.Close(key);
            RadarDataStore.Instance.Close(key);
            States.Remove(key);
            if (_currentKey != key) return;
            Current = null;
            LiveMonitorService.Instance.ActivateDocument("");
            _currentKey = "";
            Session.Clashes = null;
            Session.Groups = null;
            ClashDashboard.Instance.Clear();
            RadarDataStore.Instance.Activate("");
            GeometryCacheService.Instance.Clear();
            ClashDatabase.Instance.Dispose();
        }
    }
}
