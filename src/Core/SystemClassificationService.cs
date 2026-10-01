// Core/SystemClassificationService.cs  — NEW in v9.0
//
// ════════════════════════════════════════════════════════════════════════
//  WHY THIS FILE EXISTS — ROOT CAUSE OF "FULL SCAN GETS THE WRONG SYSTEM
//  TYPE / CATEGORY"
// ════════════════════════════════════════════════════════════════════════
//
// Prior to v9.0, three different places in the codebase each had their own
// ad-hoc way of reading an element's MEP system type, and all three were
// unreliable for the same underlying reason:
//
//   1. Engine/ClashEngine.cs  GetSystemType()
//   2. Core/GeometryCacheService.cs  BuildEntry() "system type" block
//   3. Core/ElementCollector.cs  IsGravityDrainage() / IsMedicalGas()
//
//   THE BUG: all three read parameters via raw casted integer literals:
//
//       el.get_Parameter((BuiltInParameter)(-1176500))
//       el.get_Parameter((BuiltInParameter)(-1176800))
//       el.get_Parameter((BuiltInParameter)(-1001100))
//       el.get_Parameter((BuiltInParameter)(-2000988))
//       el.get_Parameter((BuiltInParameter)(-1176400))
//
//   These are NOT documented, named Revit API constants — they are guessed
//   integer values with no source-controlled meaning. Revit's BuiltInParameter
//   IDs are not guaranteed stable/portable across API versions for values
//   used this way, and several of these IDs do not correspond to the
//   parameters the surrounding comments claim ("system name", "level").
//   In practice this produced silent failures: get_Parameter() returned
//   null (or the wrong parameter) for a large share of elements, so
//   System Type came back blank, and the keyword classifier downstream
//   (IsGravityDrainage / IsMedicalGas) then ran against an empty string —
//   meaning EVERY pipe silently fell through to "Plumbing", regardless of
//   whether it was actually a drain, vent, or medical-gas line. That is
//   precisely the "full scan doesn't get the accurate system type or
//   categories" symptom.
//
//   THE FIX: this service reads ONLY documented, named BuiltInParameter
//   enum members (RBS_PIPING_SYSTEM_TYPE_PARAM, RBS_SYSTEM_CLASSIFICATION_PARAM,
//   RBS_DUCT_SYSTEM_TYPE_PARAM, RBS_CTC_SERVICE_TYPE), and — critically —
//   uses Revit's own SYSTEM CLASSIFICATION as the primary signal for
//   Plumbing vs. GravityDrainage instead of free-text keyword guessing.
//   RBS_SYSTEM_CLASSIFICATION_PARAM exposes Revit's own enum-backed
//   classification (Sanitary / Vent / Domestic Hot Water / Domestic Cold
//   Water / Fire / Other / Hydronic Supply / Hydronic Return …) which is
//   locale-stable and does NOT depend on how the project team happened to
//   name their systems. Keyword matching is now a SECONDARY, fully
//   user-configurable disambiguator (see Rules/SystemKeywords.json) used
//   only to split the catch-all "Other" classification into Medical Gas
//   vs. ordinary Plumbing vs. Storm/Gravity drainage — which Revit itself
//   has no dedicated classification for.
//
//   Level lookup is similarly fixed: instead of guessing a BuiltInParameter
//   ID, this service uses the documented Element.LevelId property.
//
// All public methods are defensive (never throw) and degrade gracefully to
// a generic label rather than mis-classifying — consistent with the rest
// of the codebase's error-handling style.
// ════════════════════════════════════════════════════════════════════════

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Core
{
    // ══════════════════════════════════════════════════════════════════════
    //  PROJECT-CONFIGURABLE KEYWORD OVERRIDES
    //  %APPDATA%\ClashResolveAI\Rules\SystemKeywords.json
    //  Exported on first run from BuildDefault(), exactly like RulesEngine.
    //  Engineers can edit this file per-project without recompiling the
    //  add-in (e.g. SMC-3's BeaconMedaes naming conventions vs. a different
    //  project's medical-gas naming scheme).
    // ══════════════════════════════════════════════════════════════════════

    public class SystemKeywordConfig
    {
        [JsonProperty("name")]    public string Name    { get; set; } = "SystemKeywords";
        [JsonProperty("version")] public string Version { get; set; } = "1.0";

        // Matched against System Type Name + System Classification text +
        // (for Generic Model fallback) family/type name, case-insensitive.
        [JsonProperty("medical_gas_keywords")]
        public List<string> MedicalGasKeywords { get; set; } = new List<string>
        {
            "MED", "MEDGAS", "MEDICAL", "O2", "OXYGEN", "N2O", "NITROUS",
            "NITROGEN", "N2", "CO2", "CARBON DIOXIDE", "MEDAIR", "MED AIR",
            "MED-AIR", "SURGICAL AIR", "INST AIR", "VACUUM", "VAC", "AGSS",
            "SCAVENGING", "WAGD", "BEACONMEDAES"
        };

        [JsonProperty("gravity_drainage_keywords")]
        public List<string> GravityDrainageKeywords { get; set; } = new List<string>
        {
            "DRAIN", "WASTE", "SOIL", "GRAVITY", "SEWER", "SANIT", "GREY",
            "GRAY", "RWP", "RAINWAT", "STORM", "SVP", "SWV", "WP", "WC", "VENT"
        };

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClashResolveAI", "Rules");

        private static readonly string ConfigPath =
            Path.Combine(ConfigDir, "SystemKeywords.json");

        public static SystemKeywordConfig Load()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                if (!File.Exists(ConfigPath))
                {
                    var def = new SystemKeywordConfig();
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(def, Formatting.Indented));
                    return def;
                }
                var loaded = JsonConvert.DeserializeObject<SystemKeywordConfig>(File.ReadAllText(ConfigPath));
                return loaded ?? new SystemKeywordConfig();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemKeywordConfig] Load error (using built-in defaults): {ex.Message}");
                return new SystemKeywordConfig();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SYSTEM CLASSIFICATION SERVICE
    // ══════════════════════════════════════════════════════════════════════

    public static class SystemClassificationService
    {
        // Lazily loaded, reloadable so a user can edit the JSON and re-run
        // a scan without restarting Revit.
        private static SystemKeywordConfig? _keywords;
        private static SystemKeywordConfig Keywords =>
            _keywords ?? (_keywords = SystemKeywordConfig.Load());

        public static void ReloadKeywords() => _keywords = SystemKeywordConfig.Load();

        // ════════════════════════════════════════════════════════════════
        //  DISCIPLINE  — replaces ElementCollector.IsGravityDrainage /
        //  IsMedicalGas keyword-only logic. Classification-first, keyword
        //  fallback only for the ambiguous "Other" bucket.
        // ════════════════════════════════════════════════════════════════

        public static Discipline ClassifyPipeDiscipline(Element el)
        {
            try
            {
                string classification = ReadParamText(el, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
                    valueStringFirst: false).ToUpperInvariant();
                string typeName = ReadParamText(el, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM,
                    valueStringFirst: true).ToUpperInvariant();
                string familyName = SafeFamilyName(el).ToUpperInvariant();
                string searchText = $"{classification} {typeName} {familyName}";

                // 1) Strong, locale-stable signal: Revit's own classification.
                //    Sanitary / Vent / Storm are unambiguously gravity-fed —
                //    independent of whatever the team named the system.
                bool classifiedAsGravity =
                    classification.Contains("SANITARY") ||
                    classification.Contains("VENT") ||
                    classification.Contains("STORM");

                if (classifiedAsGravity)
                {
                    // Still allow an explicit medical-gas keyword match to win,
                    // since a vacuum/AGSS (waste anaesthetic gas scavenging)
                    // line is sometimes routed as "Other"/"Vent" classification
                    // but is functionally a medical gas system.
                    if (ContainsAny(searchText, Keywords.MedicalGasKeywords))
                        return Discipline.MedicalGas;
                    return Discipline.GravityDrainage;
                }

                // 2) Classification clearly identifies a pressurised/closed
                //    system (domestic water, hydronic, fire) — these are
                //    "Plumbing" in this add-in's taxonomy unless the keyword
                //    list flags it as Medical Gas (e.g. a custom "Medical
                //    Hot Water" sterilisation loop, rare but seen on hospital
                //    projects).
                bool classifiedAsPressurised =
                    classification.Contains("DOMESTIC") ||
                    classification.Contains("HYDRONIC") ||
                    classification.Contains("FIRE");

                if (classifiedAsPressurised)
                {
                    if (ContainsAny(searchText, Keywords.MedicalGasKeywords))
                        return Discipline.MedicalGas;
                    return Discipline.Plumbing;
                }

                // 3) Classification = "Other" / "Undefined" / blank — Revit has
                //    no built-in classification for medical gas or storm
                //    drainage, so this is genuinely the only case where the
                //    project-configurable keyword list is the deciding
                //    factor (matches real-world practice: BeaconMedaes/AGSS
                //    medical gas systems are almost always built on a custom
                //    "Other"-classified piping system type).
                if (ContainsAny(searchText, Keywords.MedicalGasKeywords))
                    return Discipline.MedicalGas;
                if (ContainsAny(searchText, Keywords.GravityDrainageKeywords))
                    return Discipline.GravityDrainage;

                return Discipline.Plumbing;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemClassificationService] ClassifyPipeDiscipline: {ex.Message}");
                return Discipline.Plumbing;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  SYSTEM TYPE — human-readable display text. Replaces
        //  ClashEngine.GetSystemType() and GeometryCacheService's inline
        //  magic-number lookup.
        // ════════════════════════════════════════════════════════════════

        public static string GetSystemTypeName(Element el)
        {
            if (el?.Category == null) return "";
            try
            {
                long catId = el.Category.Id.Value;

                // ── Pipe family: system type name, classification fallback ──
                if (catId == (long)BuiltInCategory.OST_PipeCurves ||
                    catId == (long)BuiltInCategory.OST_PipeFitting ||
                    catId == (long)BuiltInCategory.OST_FlexPipeCurves ||
                    catId == (long)BuiltInCategory.OST_PipeAccessory ||
                    catId == (long)BuiltInCategory.OST_PlumbingFixtures)
                {
                    string name = ReadParamText(el, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM, valueStringFirst: true);
                    if (!string.IsNullOrWhiteSpace(name)) return name;

                    string cls = ReadParamText(el, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM, valueStringFirst: false);
                    if (!string.IsNullOrWhiteSpace(cls)) return cls;
                }

                // ── Duct family: duct system type name ───────────────────
                if (catId == (long)BuiltInCategory.OST_DuctCurves ||
                    catId == (long)BuiltInCategory.OST_DuctFitting ||
                    catId == (long)BuiltInCategory.OST_FlexDuctCurves ||
                    catId == (long)BuiltInCategory.OST_DuctAccessory ||
                    catId == (long)BuiltInCategory.OST_DuctTerminal ||
                    catId == (long)BuiltInCategory.OST_MechanicalEquipment)
                {
                    string name = ReadParamText(el, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM, valueStringFirst: true);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                // ── Cable tray / conduit: "Service Type" ──────────────────
                if (catId == (long)BuiltInCategory.OST_CableTray ||
                    catId == (long)BuiltInCategory.OST_CableTrayFitting ||
                    catId == (long)BuiltInCategory.OST_Conduit ||
                    catId == (long)BuiltInCategory.OST_ConduitFitting)
                {
                    string svc = ReadServiceType(el);
                    if (!string.IsNullOrWhiteSpace(svc)) return svc;
                }

                // ── Fallback for everything else (electrical devices,
                //    structural, generic model): Family + Type name is the
                //    most meaningful "system" label available.
                string famType = ElementCollector.GetFamilyTypeName(el);
                if (!string.IsNullOrWhiteSpace(famType)) return famType;

                var typeEl = el.Document.GetElement(el.GetTypeId());
                return typeEl?.Name ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemClassificationService] GetSystemTypeName: {ex.Message}");
                return "";
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  LEVEL — replaces the GeometryCacheService magic-number lookup.
        //  Element.LevelId is a documented API property; InvalidElementId
        //  is returned (not an exception) for elements with no host level,
        //  which the elevation-based GetLevel(XYZ) in ClashEngine already
        //  handles as a fallback path for display purposes.
        // ════════════════════════════════════════════════════════════════

        public static ElementId GetLevelId(Element el)
        {
            try
            {
                if (el == null) return ElementId.InvalidElementId;
                var id = el.LevelId;
                return id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reads a parameter's display text defensively. Element-id-backed
        /// parameters (system type references) expose their text via
        /// AsValueString(); plain string parameters (classification, service
        /// type) expose it via AsString(). valueStringFirst controls which
        /// is tried first, but both are attempted either way so a Revit
        /// version that stores the parameter differently still works.
        /// </summary>
        private static string ReadParamText(Element el, BuiltInParameter bip, bool valueStringFirst)
        {
            try
            {
                var p = el?.get_Parameter(bip);
                if (p == null) return "";

                string a = valueStringFirst ? SafeValueString(p) : SafeAsString(p);
                if (!string.IsNullOrWhiteSpace(a)) return a;

                string b = valueStringFirst ? SafeAsString(p) : SafeValueString(p);
                return b ?? "";
            }
            catch { return ""; }
        }

        private static string SafeAsString(Parameter p)
        {
            try { return p.AsString() ?? ""; } catch { return ""; }
        }

        private static string SafeValueString(Parameter p)
        {
            try { return p.AsValueString() ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// Cable tray / conduit "Service Type". Revit exposes this only as a
        /// named instance parameter (no dedicated MEPSystem for tray/conduit,
        /// confirmed: unlike pipe/duct they don't belong to a system), so
        /// LookupParameter("Service Type") — matching the exact label shown
        /// in the Revit UI — is used as the primary, version-portable path.
        /// BuiltInParameter.RBS_CTC_SERVICE_TYPE is attempted as a secondary
        /// check for projects where the parameter was renamed; if your
        /// installed Revit API build doesn't expose that exact enum member,
        /// simply delete the inner try block below — the LookupParameter
        /// path above already covers the normal case.
        /// </summary>
        private static string ReadServiceType(Element el)
        {
            try
            {
                var p = el.LookupParameter("Service Type");
                if (p != null)
                {
                    string v = SafeAsString(p);
                    if (string.IsNullOrWhiteSpace(v)) v = SafeValueString(p);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch { }

            try
            {
                var p2 = el.get_Parameter(BuiltInParameter.RBS_CTC_SERVICE_TYPE);
                if (p2 != null)
                {
                    string v2 = SafeAsString(p2);
                    if (string.IsNullOrWhiteSpace(v2)) v2 = SafeValueString(p2);
                    if (!string.IsNullOrWhiteSpace(v2)) return v2;
                }
            }
            catch { /* enum member may differ across API builds — already covered above */ }

            return "";
        }

        private static string SafeFamilyName(Element el)
        {
            try { return (el as FamilyInstance)?.Symbol?.FamilyName ?? ""; }
            catch { return ""; }
        }

        private static bool ContainsAny(string haystack, IEnumerable<string> needles)
        {
            if (string.IsNullOrEmpty(haystack) || needles == null) return false;
            foreach (var n in needles)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (haystack.Contains(n.ToUpperInvariant())) return true;
            }
            return false;
        }
    }
}
