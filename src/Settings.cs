// Settings.cs
using Newtonsoft.Json;
using ClashResolveAI.Core;
using System;
using System.IO;

#if !REVIT_STUB_BUILD
namespace ClashResolveAI
{
    public class AppSettings
    {
        public string OpenAiApiKey      { get; set; } = "";
        public string OpenAiModel       { get; set; } = "gpt-4o";
        public string OutputFolder      { get; set; } = DefaultOut();
        public double InsulationMM      { get; set; } = 50.0;
        public double MaintenanceMM     { get; set; } = 100.0;
        public bool   LiveMonitorUseAI  { get; set; } = true;
        public bool   IncludeStructural { get; set; } = true;
        public bool   AutoSectionBox    { get; set; } = true;
        public bool   ShowToast         { get; set; } = true;
        public bool   ScanLinkedModels  { get; set; } = true;
        public int ScanConfigurationVersion { get; set; }
        public bool ScanHostToHost { get; set; } = true;
        public bool ScanHostToLinks { get; set; } = true;

        public string RuleSetName { get; set; } = "DefaultRules";
        public double MinimumOverlapMM3 { get; set; } = 1.0;
        public ScanMode FullScanMode { get; set; } = ScanMode.HardOnly;
        public ScanMode LiveMode { get; set; } = ScanMode.HardOnly;
        [JsonIgnore] public ScanMode EffectiveMode { get; set; } = ScanMode.HardOnly;
        public int FullScanSliceMilliseconds { get; set; } = 40;
        public int LiveSliceMilliseconds { get; set; } = 25;
        public int LiveDebounceMilliseconds { get; set; } = 500;
        public int LiveMaximumSliceMilliseconds { get; set; } = 100;
        public bool ScanWithinLinks { get; set; } = true;
        public bool FullScanWithinLinks { get; set; } = false;
        public bool FullScanLinkedModels { get; set; } = true;
        public string FullScanRuleSetName { get; set; } = "";
        public double FullScanMinimumOverlapMM3 { get; set; } = 1;
        public double FullScanInsulationMM { get; set; } = 50;
        public double FullScanMaintenanceMM { get; set; } = 100;
        public bool FullScanIncludeStructural { get; set; } = true;
        public bool FullScanExcludeConnectedJoints { get; set; } = true;
        public bool IncludeLinkToLink { get; set; } = false;
        public bool TrackEditedElements { get; set; } = true;
        public bool ExcludeConnectedJoints { get; set; } = true;
        // Legacy candidate preferences remain available to the Live Monitor pipeline.
        public bool ExcludeNamedSupports { get; set; } = false;
        public bool IncludeGenericModels { get; set; } = true;
        public bool IncludeInsulation { get; set; } = true;
        public bool FullScanExcludeNamedSupports { get; set; } = true;
        public bool FullScanIncludeGenericModels { get; set; } = false;
        public bool FullScanIncludeInsulation { get; set; } = false;
        private static AppSettings? _cached;
        private static DateTime _stamp;
        private static string UserSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashResolveAI", "ClashResolveAI_Settings.json");
        private static string SettingsPath => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR"))?UserSettingsPath:
            Path.Combine(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")!,"Settings.json");

        public AppSettings ScanSnapshot() => (AppSettings)MemberwiseClone();
        public AppSettings FullScanSnapshot()
        {
            var copy=ScanSnapshot();
            copy.RuleSetName=string.IsNullOrWhiteSpace(FullScanRuleSetName)?RuleSetName:FullScanRuleSetName;
            copy.MinimumOverlapMM3=FullScanMinimumOverlapMM3;copy.InsulationMM=FullScanInsulationMM;copy.MaintenanceMM=FullScanMaintenanceMM;
            copy.IncludeStructural=FullScanIncludeStructural;copy.ExcludeConnectedJoints=FullScanExcludeConnectedJoints;
            return copy;
        }
        // One-time, in-memory migration. Persist only when the user saves settings.
        public void MigrateScanConfiguration()
        {
            if(ScanConfigurationVersion<1) {
                ScanHostToHost=true;ScanHostToLinks=true;IncludeLinkToLink=false;
                FullScanExcludeNamedSupports=true;FullScanIncludeGenericModels=false;FullScanIncludeInsulation=false;
            }
            if(ScanConfigurationVersion<2) {
                FullScanWithinLinks=false;FullScanLinkedModels=ScanLinkedModels;
                if(string.IsNullOrWhiteSpace(FullScanRuleSetName))FullScanRuleSetName=RuleSetName;
                FullScanMinimumOverlapMM3=MinimumOverlapMM3;FullScanInsulationMM=InsulationMM;FullScanMaintenanceMM=MaintenanceMM;
                FullScanIncludeStructural=IncludeStructural;FullScanExcludeConnectedJoints=ExcludeConnectedJoints;
            }
            ScanConfigurationVersion=2;
        }
        public static AppSettings Load()
        {
            try {
                string path=File.Exists(SettingsPath)?SettingsPath:UserSettingsPath;
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_cached != null && stamp == _stamp) return _cached;
                _stamp = stamp;
                _cached = stamp == DateTime.MinValue ? new AppSettings() : JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                _cached.MigrateScanConfiguration();
                return _cached;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(string.Concat("[ClashResolve] Settings load error: ", ex.Message)); }
            return new AppSettings();
        }

        public static void Save(AppSettings s) { s.ScanConfigurationVersion=2; _cached = null; Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented)); }

        private static string DefaultOut() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClashResolve_Reports");
    }

}
#endif

