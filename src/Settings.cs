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
        public bool IncludeLinkToLink { get; set; } = false;
        public bool TrackEditedElements { get; set; } = true;
        public bool ExcludeConnectedJoints { get; set; } = true;
        public bool ExcludeNamedSupports { get; set; } = false;
        public bool IncludeGenericModels { get; set; } = true;
        public bool IncludeInsulation { get; set; } = true;
        private static AppSettings? _cached;
        private static DateTime _stamp;
        private static string UserSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashResolveAI", "ClashResolveAI_Settings.json");
        private static string SettingsPath => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR"))?UserSettingsPath:
            Path.Combine(Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")!,"Settings.json");

        public AppSettings ScanSnapshot() => (AppSettings)MemberwiseClone();
        public static AppSettings Load()
        {
            try {
                string path=File.Exists(SettingsPath)?SettingsPath:UserSettingsPath;
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_cached != null && stamp == _stamp) return _cached;
                _stamp = stamp;
                return _cached = stamp == DateTime.MinValue ? new AppSettings() : JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(string.Concat("[ClashResolve] Settings load error: ", ex.Message)); }
            return new AppSettings();
        }

        public static void Save(AppSettings s) { _cached = null; Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented)); }

        private static string DefaultOut() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClashResolve_Reports");
    }

}
#endif

