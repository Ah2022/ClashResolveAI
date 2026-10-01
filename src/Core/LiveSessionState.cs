using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    public enum LedgerOrigin { Drawn, Edited }
    public sealed class LedgerEntry
    {
        public long ElementId;
        public string UniqueId="";
        public LedgerOrigin Origin;
        public DateTime Timestamp;
        public bool Checked;
    }
    // Plain state: no Revit API calls; each document owns independent entries and revisions.
    public sealed class LiveLedgerStore
    {
        private readonly Dictionary<string,Dictionary<long,LedgerEntry>> _documents=new Dictionary<string,Dictionary<long,LedgerEntry>>();
        public void Note(string document,long id,string uniqueId,LedgerOrigin origin)
        {
            if(!_documents.TryGetValue(document,out var entries))_documents[document]=entries=new Dictionary<long,LedgerEntry>();
            if(!entries.TryGetValue(id,out var entry)||entry.UniqueId!=uniqueId)entries[id]=entry=new LedgerEntry {ElementId=id,UniqueId=uniqueId,Origin=origin};
            entry.Timestamp=DateTime.UtcNow;entry.Checked=false;
        }
        public List<LedgerEntry> Entries(string document)=>_documents.TryGetValue(document,out var entries)?entries.Values.ToList():new List<LedgerEntry>();
        public List<long> LiveIds(string document,Func<long,string?> identity)
        {
            if(!_documents.TryGetValue(document,out var entries))return new List<long>();
            foreach(var entry in entries.Values.ToList())if(identity(entry.ElementId)!=entry.UniqueId)entries.Remove(entry.ElementId);
            return entries.Keys.ToList();
        }
        public void Checked(string document,ISet<long> ids){foreach(var entry in Entries(document))if(ids.Contains(entry.ElementId))entry.Checked=true;}
        public void Pending(string document,ISet<long> ids){foreach(var entry in Entries(document))if(ids.Contains(entry.ElementId))entry.Checked=false;}
        public bool HasUnchecked(string document)=>Entries(document).Any(e=>!e.Checked);
        public void Clear(string document)=>_documents.Remove(document);
        public void Clear()=>_documents.Clear();
    }
    public sealed class ElementRevisionStore
    {
        private sealed class State
        {
            public readonly Dictionary<long,long> Elements=new Dictionary<long,long>();
            public readonly HashSet<long> Dirty=new HashSet<long>();
            public readonly HashSet<long> FullDirty=new HashSet<long>();
            public long Inputs,VerifiedInputs;
        }
        private readonly Dictionary<string,State> _documents=new Dictionary<string,State>();
        public long Revision {get;private set;}
        private State Get(string document){if(!_documents.TryGetValue(document,out var state))_documents[document]=state=new State();return state;}
        public void Changed(string document,IEnumerable<long> ids,bool inputs)
        {
            var state=Get(document);long revision=++Revision;
            foreach(long id in ids){state.Elements[id]=revision;state.Dirty.Add(id);state.FullDirty.Add(id);}
            if(inputs)state.Inputs=revision;
        }
        public long Required(ClashResult clash)
        {
            var state=Get(clash.HostDocumentKey);long revision=state.Inputs;
            if(string.IsNullOrEmpty(clash.LinkInstanceA)&&state.Elements.TryGetValue(clash.ElementAId,out var a))revision=Math.Max(revision,a);
            if(string.IsNullOrEmpty(clash.LinkInstanceB)&&state.Elements.TryGetValue(clash.ElementBId,out var b))revision=Math.Max(revision,b);
            return revision;
        }
        public bool IsStale(ClashResult clash)=>Required(clash)>clash.GeometryRevision;
        public bool IsDirty(string document)=>Get(document).Dirty.Count>0||Get(document).Inputs>Get(document).VerifiedInputs;
        public bool InputsStale(string document)=>Get(document).Inputs>Get(document).VerifiedInputs;
        public bool FullResultsStale(string document)=>Get(document).FullDirty.Count>0||InputsStale(document);
        public void Checked(string document,IEnumerable<long> ids)=>Get(document).Dirty.ExceptWith(ids);
        public void CheckedScope(string document,Func<long,bool> covered,bool complete)
        {
            var state=Get(document);
            // A full-scope scan also evaluates host targets and supersedes deleted
            // dependants restored by Redo (center lines, systems, etc.). Those IDs
            // need not be geometry sources. Scoped scans retain uncovered edits.
            if(complete){state.Dirty.Clear();state.FullDirty.Clear();state.VerifiedInputs=state.Inputs;}
            else {state.Dirty.RemoveWhere(id=>covered(id));state.FullDirty.RemoveWhere(id=>covered(id));}
        }
        public void InputsCurrent(string document){var state=Get(document);state.VerifiedInputs=state.Inputs;}
        public void Close(string document)=>_documents.Remove(document);
    }
}
