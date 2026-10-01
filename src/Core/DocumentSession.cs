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
        private static string _currentKey = "";
        public static string CurrentKey=>_currentKey;
        public static Document? Current { get; private set; }
        public static string Key(Document doc)
        {
            // Revit can return different managed Document wrappers for the same native
            // model. Never use wrapper reference equality to partition document state.
            string identity = doc.ProjectInformation.UniqueId + "|" +
                (string.IsNullOrEmpty(doc.PathName) ? "unsaved:" + doc.Title : doc.PathName);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24);
        }
        public static bool Matches(ClashResult clash, Document doc) => clash.HostDocumentKey == Key(doc);
        public static void Activate(Document doc)
        {
            if (doc.IsFamilyDocument) return;
            string key = Key(doc);
            ScanCoordinator.Observe(doc);
            if (key == _currentKey) { Current = doc; return; }
            if (States.TryGetValue(_currentKey, out var old))
            {
                old.Dashboard = ClashDashboard.Instance.Clashes.ToList();
                old.Filter=ClashDashboard.Instance.ViewFilter.Copy();
            }
            if (!States.TryGetValue(key, out var state)) States[key] = state = new State();
            GeometryCacheService.Instance.Clear();
            ClashDatabase.Instance.Open(Key(doc));
            Current = doc;
            _currentKey = key;
            Session.ProjectName = doc.Title;
            Session.RuleSetName = AppSettings.Load().RuleSetName;
            RadarDataStore.Instance.Activate(key);
            ClashDashboard.Instance.RestoreSession(state.Dashboard,state.Filter);
            Diagnostics.Log("Active document: " + Key(doc));
        }
        public static void Close(Document doc)
        {
            string key = Key(doc);
            ScanCoordinator.Close(key);
            RadarDataStore.Instance.Close(key);
            States.Remove(key);
            if (_currentKey != key) return;
            Current = null;
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
