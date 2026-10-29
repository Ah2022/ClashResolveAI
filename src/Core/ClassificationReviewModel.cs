using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ClashResolveAI.Core
{
    public sealed class ClassificationReviewSample
    {
        public ClassificationInput Input { get; }
        public string Source { get; }
        public int Count { get; }
        public ClassificationReviewSample(ClassificationInput input,string source,int count=1)
        {
            Input=new ClassificationInput {CategoryId=input.CategoryId,Category=input.Category,CategoryAlias=input.CategoryAlias,
                FamilyType=input.FamilyType,SystemType=input.SystemType,SystemClassification=input.SystemClassification,
                Kind=input.Kind,CategoryDiscipline=input.CategoryDiscipline,SystemDiscipline=input.SystemDiscipline,ConflictingSystems=input.ConflictingSystems};
            Source=source;Count=count;
        }
        internal string Key => Newtonsoft.Json.JsonConvert.SerializeObject(Input);
    }
    public sealed class ClassificationReviewRow : INotifyPropertyChanged
    {
        internal ClassificationInput Input { get; }
        private readonly HashSet<string> _sources=new HashSet<string>();
        public int Count { get; private set; }
        public string Category=>Input.Category;
        public long CategoryId=>Input.CategoryId;
        public string FamilyType=>Input.FamilyType;
        public string SystemType=>Input.SystemType;
        public string SystemClassification=>Input.SystemClassification;
        public string Sources=>string.Join("; ",_sources.OrderBy(s=>s,StringComparer.Ordinal));
        public ElementClassification Classification { get; private set; }=null!;
        public ElementClassification Suggestion { get; private set; }=null!;
        public string CurrentClassification=>Classification.Discipline.ToString();
        public string Confidence=>Classification.Confidence==0?"Unknown":(Classification.Confidence>=.9?"High":Classification.Confidence>=.5?"Medium":"Low")+$" ({Classification.Confidence:P0})";
        public string SuggestedClassification=>Classification.Discipline==Discipline.Unknown&&Suggestion.Discipline!=Discipline.Unknown?Suggestion.Discipline+" — "+Suggestion.Evidence:"";
        public string Evidence=>Classification.Evidence+": "+Classification.Explanation;
        public string Inclusion=>Classification.IsCoordinationCandidate?"Included":Classification.ExclusionReason;
        public string Action=>Classification.Discipline==Discipline.Unknown?(Suggestion.Discipline==Discipline.Unknown?"Map…":"Confirm suggestion"):Classification.Confidence>=.9?"Keep":"Confirm";
        public string ReviewStatus { get; private set; }="";
        public event PropertyChangedEventHandler? PropertyChanged;
        internal ClassificationReviewRow(ClassificationReviewSample sample) {Input=new ClassificationReviewSample(sample.Input,sample.Source).Input;Add(sample);}
        internal void Add(ClassificationReviewSample sample) {Count+=sample.Count;_sources.Add(sample.Source);}
        internal void Update(ScanPlan plan,IReadOnlyList<ClassificationMapping> mappings)
        {
            Classification=CoordinationClassifier.Classify(Input,plan.CandidatePolicy,plan.Stage,mappings,plan.MedicalGasKeywords,plan.GravityDrainageKeywords);
            var fallback=new ClassificationInput {CategoryId=Input.CategoryId,Category=Input.Category,CategoryAlias=Input.CategoryAlias,
                FamilyType=Input.FamilyType,SystemType=Input.SystemType,SystemClassification=Input.SystemClassification,
                Kind=CoordinationElementKind.Pipe,SystemDiscipline=Input.SystemDiscipline,ConflictingSystems=Input.ConflictingSystems};
            Suggestion=CoordinationClassifier.Classify(fallback,new CoordinationCandidatePolicy(excludeNamedSupports:false),ScanStage.HardClash,
                medicalKeywords:plan.MedicalGasKeywords,drainageKeywords:plan.GravityDrainageKeywords);
            ReviewStatus="";PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));
        }
        internal void Keep() {ReviewStatus="Kept current classification";PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(ReviewStatus)));}
    }
    public sealed class ClassificationReviewModel
    {
        private readonly ScanPlan _plan;
        private readonly ProjectClassificationStore _store;
        private ProjectMappingSnapshot _saved;
        private readonly Dictionary<string,ClassificationReviewRow> _rows=new Dictionary<string,ClassificationReviewRow>(StringComparer.Ordinal);
        public ObservableCollection<ClassificationReviewRow> Rows { get; }=new ObservableCollection<ClassificationReviewRow>();
        public ObservableCollection<ClassificationMapping> Mappings { get; }=new ObservableCollection<ClassificationMapping>();
        public string DocumentKey=>_plan.DocumentKey;
        public string StoragePath=>_store.PathFor(DocumentKey);
        public bool Dirty=>new ProjectMappingSnapshot(DocumentKey,Mappings).Fingerprint!=_saved.Fingerprint;
        public int ElementCount=>Rows.Sum(r=>r.Count);
        public int NeedsReview=>Rows.Count(r=>r.Classification.Discipline==Discipline.Unknown||r.Classification.Confidence<.9);
        public event Action? Changed;
        public ClassificationReviewModel(ScanPlan plan,ProjectClassificationStore store)
        {
            _plan=plan;_store=store;_saved=store.Load(plan.DocumentKey);
            foreach(var mapping in _saved.Mappings)Mappings.Add(mapping);
        }
        public void AddSample(ClassificationReviewSample sample)
        {
            if(_rows.TryGetValue(sample.Key,out var row))row.Add(sample);
            else {_rows[sample.Key]=row=new ClassificationReviewRow(sample);row.Update(_plan,Mappings);Rows.Add(row);}
        }
        public void Refresh()
        {
            foreach(var row in Rows)row.Update(_plan,Mappings);Changed?.Invoke();
        }
        public void Upsert(ClassificationMapping mapping)
        {
            var existing=Mappings.FirstOrDefault(m=>m.Id==mapping.Id);
            if(existing!=null)Mappings.Remove(existing);
            Mappings.Add(mapping);Refresh();
        }
        public void Remove(ClassificationMapping mapping) {Mappings.Remove(mapping);Refresh();}
        public void Keep(ClassificationReviewRow row)
        {
            if(row.Classification.Discipline==Discipline.Unknown)throw new InvalidOperationException("Unknown classifications require a mapping.");
            row.Keep();Changed?.Invoke();
        }
        public ClassificationMapping Confirm(ClassificationReviewRow row,Discipline? discipline=null,bool exclude=false)
        {
            var target=discipline ?? (row.Classification.Discipline!=Discipline.Unknown?row.Classification.Discipline:row.Suggestion.Discipline);
            if(target==Discipline.Unknown&&!exclude)throw new InvalidOperationException("Select a known discipline before confirming this row.");
            string id="review-"+ProjectClassificationStore.Hash(Newtonsoft.Json.JsonConvert.SerializeObject(row.Input)).Substring(0,24);
            var matching=Mappings.Where(m=>m.Id!=id&&m.Matches(row.Input)).ToList();
            int priority=matching.Count==0?100:Math.Max(100,matching.Max(m=>m.Priority)==int.MaxValue?int.MaxValue:matching.Max(m=>m.Priority)+1);
            var mapping=new ClassificationMapping(id,target,row.FamilyType,row.CategoryId,exclude,priority,
                FamilyTypeMatch.Exact,row.SystemType,row.SystemClassification);
            Upsert(mapping);return mapping;
        }
        public ProjectMappingSnapshot Save()
        {
            _saved=_store.Save(DocumentKey,Mappings,_saved.Fingerprint);Changed?.Invoke();return _saved;
        }
        public void Reload()
        {
            _saved=_store.Load(DocumentKey);Mappings.Clear();foreach(var mapping in _saved.Mappings)Mappings.Add(mapping);Refresh();
        }
        public void ImportDraft(string path)
        {
            var imported=_store.ReadImport(path);Mappings.Clear();foreach(var mapping in imported)Mappings.Add(mapping);Refresh();
        }
        public void ExportDraft(string path)=>_store.Export(path,DocumentKey,Mappings);
    }
}
