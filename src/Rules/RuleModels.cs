using Newtonsoft.Json;
using System.Collections.Generic;

namespace ClashResolveAI.Rules
{
    public class ClashRule
    {
        [JsonProperty("clearance_mm")] public double ClearanceMM { get; set; }=50;
        [JsonProperty("priority")] public string Priority { get; set; }="Medium";
        [JsonProperty("severity")] public string Severity { get; set; }="Hard";
        [JsonProperty("moving")] public string Moving { get; set; }="auto";
        [JsonProperty("note")] public string Note { get; set; }="";
        [JsonProperty("code_ref")] public string CodeRef { get; set; }="";
        [JsonProperty("ignore")] public bool Ignore { get; set; }
    }
    public class RuleSet
    {
        [JsonProperty("name")] public string Name { get; set; }="Default";
        [JsonProperty("version")] public string Version { get; set; }="1.0";
        [JsonProperty("project_type")] public string ProjectType { get; set; }="Generic";
        [JsonProperty("rules")] public Dictionary<string,ClashRule> Rules { get; set; }=new Dictionary<string,ClashRule>();
        [JsonProperty("discipline_pairs")] public Dictionary<string,ClashRule> DisciplinePairs { get; set; }=new Dictionary<string,ClashRule>();
        [JsonProperty("category_pairs")] public Dictionary<string,ClashRule> CategoryPairs { get; set; }=new Dictionary<string,ClashRule>();
    }
}
