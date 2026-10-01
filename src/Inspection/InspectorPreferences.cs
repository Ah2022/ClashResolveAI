using System;
using System.IO;
using Newtonsoft.Json;

namespace ClashResolveAI.Inspection
{
    public sealed class InspectorPreferences
    {
        [JsonIgnore] public bool PersistenceEnabled { get; set; } = true;
        public string Mode { get; set; } = "3d";
        public double Zoom { get; set; } = 1;
        public double ListRatio { get; set; } = .38;
        public double ControlsHeight { get; set; } = 80;
        public double PaddingFeet { get; set; } = 6;
        public bool Context { get; set; } = true;
        public bool Focus { get; set; } = true;
        public bool AxisB { get; set; }
        public int Corner { get; set; }
        private static string PathName {
            get { string verification=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")??"";
                return verification!=""?Path.Combine(verification,"Inspector.json"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ClashResolveAI","Inspector.json"); }
        }
        public static InspectorPreferences Load()
        {
            try { var p=File.Exists(PathName)?JsonConvert.DeserializeObject<InspectorPreferences>(File.ReadAllText(PathName)):null;return (p??new InspectorPreferences()).Normalize(); }
            catch { return new InspectorPreferences(); }
        }
        public InspectorPreferences Normalize()
        {
            if(Mode!="2d"&&Mode!="3d"&&Mode!="section"&&Mode!="compare"&&Mode!="triple")Mode="3d";
            Zoom=Bound(Zoom,.15,20,1);ListRatio=Bound(ListRatio,.15,.75,.38);
            ControlsHeight=Bound(ControlsHeight,76,116,80);PaddingFeet=Bound(PaddingFeet,1,20,6);Corner=((Corner%4)+4)%4;
            return this;
        }
        private static double Bound(double v,double lo,double hi,double fallback) => double.IsNaN(v)||double.IsInfinity(v)?fallback:Math.Max(lo,Math.Min(hi,v));
        public void Save()
        {
            if(!PersistenceEnabled)return;
            try { Normalize();Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);File.WriteAllText(PathName,JsonConvert.SerializeObject(this,Formatting.Indented)); }
            catch(Exception ex){System.Diagnostics.Debug.WriteLine("Inspector preferences: "+ex.Message);}
        }
    }
}
