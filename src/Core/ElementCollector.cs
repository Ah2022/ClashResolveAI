// Core/ElementCollector.cs  — v6.1 FIX: InferDisciplineFromCategory added to GetDiscipline fallback  — v6.1.1
//
// v6.1 additions:
//   • GetCategoryName / GetFamilyTypeName / GetStructuralSubType — for filter display
//   • AreSameConnectedSystem / IsSupportOrAccessoryFamily — connection exclusion
//   • Extended IsGravityDrainage + IsMedicalGas keyword sets
//   • GetConnectorManager private helper
//
// FIX v6.1.1:
//   • AreSameConnectedSystem: REMOVED Check 1 (MEPSystem identity).
//     MEPSystem.Id is building-wide — all WP pipes share one Id.
//     Check 1 excluded every host-model MEP pair in a properly modelled
//     project, causing Live Monitor to detect zero clashes.
//     Only Check 2 (direct connector sharing) is retained.
//     • OST_AirTerminal          → HVAC  (NEW — same as DuctTerminal in some versions)
//     • OST_CommunicationDevices → Electrical (NEW)
//     • OST_DataDevices          → Electrical (NEW)
//     • OST_NurseCallDevices     → Electrical (NEW)
//     • OST_SecurityDevices      → Electrical (NEW)
//     • OST_TelephoneDevices     → Electrical (NEW)
//     • OST_FireAlarmDevices     → FireProtection (already existed — confirmed)
//     • OST_GenericModel         → resolved via system parameter heuristic (NEW)
//
//   IsMonitoredClashElement now accepts structural elements explicitly
//   so host structural walls/columns are always scannable.

using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.Core
{
    public static class ElementCollector
    {
        private const double MinSolidVolume = 1e-9;

        private static readonly Options _liveOpts = new Options
        {
            DetailLevel              = ViewDetailLevel.Medium,
            ComputeReferences        = false,
            IncludeNonVisibleObjects = false
        };

        // ── Clash matrix ──────────────────────────────────────────────────
        public static readonly List<ClashMatrixEntry> ClashMatrix = new List<ClashMatrixEntry>
        {
            // ── Same-Discipline Clashes ───────────────────────────────────
            new ClashMatrixEntry { Source=Discipline.Plumbing,        Target=Discipline.Plumbing,       Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.HVAC,            Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.Electrical,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.GravityDrainage, Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.FireProtection, Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.MedicalGas,      Target=Discipline.MedicalGas,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.CableTray,      Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.Conduit,        Check=true, RuleKey="Generic"             },

            // ── Cross-Discipline Clashes ──────────────────────────────────
            new ClashMatrixEntry { Source=Discipline.Plumbing,        Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.HVAC,            Target=Discipline.Structural,     Check=true, RuleKey="Duct_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.Structural,     Check=true, RuleKey="FireMain_vs_Structure"},
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.Plumbing,       Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.Plumbing,        Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Electrical,      Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.Electrical,     Check=true, RuleKey="Sprinkler_vs_Light"  },
            new ClashMatrixEntry { Source=Discipline.FireProtection,  Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.HVAC,           Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Electrical,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.GravityDrainage, Target=Discipline.Plumbing,       Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.MedicalGas,      Target=Discipline.HVAC,           Check=true, RuleKey="MedGas_vs_HVAC"      },
            new ClashMatrixEntry { Source=Discipline.MedicalGas,      Target=Discipline.Structural,     Check=true, RuleKey="Pipe_vs_Beam"        },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.HVAC,           Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.Plumbing,       Check=true, RuleKey="CableTray_vs_Pipe"   },
            new ClashMatrixEntry { Source=Discipline.CableTray,       Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.HVAC,           Check=true, RuleKey=""                    },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.Structural,     Check=true, RuleKey="Generic"             },
            new ClashMatrixEntry { Source=Discipline.Conduit,         Target=Discipline.Plumbing,       Check=true, RuleKey="Generic"             },
        };

        // ════════════════════════════════════════════════════════════════
        //  PUBLIC COLLECT
        // ════════════════════════════════════════════════════════════════

        public static List<Element> GetRawByDisciplineAll(Document doc, Discipline disc)
            => GetRawByDiscipline(doc, disc);

        // FIX v8.0 — BUG #2 ROOT CAUSE:
        //
        // Plumbing, GravityDrainage and MedicalGas all share the same
        // Revit categories (OST_PipeCurves, OST_PipeFitting, etc.).
        // The old implementation used only category-based collection,
        // so GetByDiscipline(doc, GravityDrainage) returned EVERY pipe
        // in the model — identical to GetByDiscipline(doc, Plumbing).
        //
        // This caused two problems in the full scan:
        //   1. BuildSpatialIndex registered each pipe THREE times (once
        //      per discipline label) corrupting the spatial hash grid.
        //   2. ScanHostVsHostSpatial labeled clashes with the matrix-entry
        //      discipline rather than the actual element discipline, causing
        //      random/wrong discipline attribution in every clash result.
        //
        // THE FIX: For pipe-family disciplines (Plumbing, GravityDrainage,
        // MedicalGas), post-filter the category-based collection by calling
        // GetDiscipline() on each element. GetDiscipline() uses the piping
        // system-type parameter — the only reliable way to distinguish these
        // three disciplines in Revit. Category-only collection is used as
        // the fast first pass; GetDiscipline() is the discriminating filter.
        //
        // For all other disciplines (HVAC, Electrical, FireProtection,
        // CableTray, Conduit, Structural), categories are already unique
        // so no post-filter is needed — behaviour is unchanged.
        public static List<Element> GetByDiscipline(Document doc, Discipline disc)
        {
            var raw = GetRawByDiscipline(doc, disc);

            // Pipe-family disciplines share categories — must post-filter
            // using actual system-type parameter via GetDiscipline().
            if (disc == Discipline.Plumbing ||
                disc == Discipline.GravityDrainage ||
                disc == Discipline.MedicalGas)
            {
                return raw
                    .Where(el => GetDiscipline(el) == disc)
                    .Where(IsValidMEPElement)
                    .ToList();
            }

            // All other disciplines have unique categories — category match
            // is sufficient; no per-element parameter check needed.
            return raw.Where(IsValidMEPElement).ToList();
        }

        public static List<Element> GetByDisciplineNearby(
            Document doc, Discipline disc, BoundingBoxIntersectsFilter filter)
        {
            var cats   = GetCategoriesForDiscipline(disc);
            var result = new List<Element>();
            foreach (var cat in cats)
            {
                try
                {
                    foreach (var el in new FilteredElementCollector(doc)
                        .OfCategory(cat).WherePasses(filter)
                        .WhereElementIsNotElementType().ToElements())
                        if (IsValidMEPElement(el)) result.Add(el);
                }
                catch (System.Exception ex)
                {
                    Debug.WriteLine($"[Collector] Nearby error: {ex.Message}");
                }
            }
            return result.Distinct().ToList();
        }

        // ════════════════════════════════════════════════════════════════
        //  VALIDATION
        // ════════════════════════════════════════════════════════════════

        public static bool IsValidMEPElement(Element el)
        {
            if (el == null || el.Category == null || el.Location == null) return false;
            try
            {
                var geom = el.get_Geometry(_liveOpts);
                return geom != null && HasSolidVolume(geom);
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Collector] IsValid {el.Id.Value}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns true for ANY element that can participate in a live clash check.
        /// v5.1: Structural elements are explicitly accepted (they often have no
        /// Location property but always have a bounding box).
        /// </summary>
        public static bool IsMonitoredClashElement(Element el)
        {
            if (el?.Category == null) return false;
            var disc = GetDiscipline(el);
            // FIX v6.1: GetDiscipline now falls back to InferDisciplineFromCategory,
            // so Unknown here means truly unrecognised category (not just missing system).
            if (disc == Discipline.Unknown) return false;

            try { return el.get_BoundingBox(null) != null; }
            catch { return false; }
        }

        // ════════════════════════════════════════════════════════════════
        //  SOLID EXTRACTION
        // ════════════════════════════════════════════════════════════════

        public static Solid? GetSolidFromElement(Element el)
        {
            if (el == null) return null;
            var cached = GeometryCacheService.Instance.Get(el.Id);
            if (cached?.Solid != null) return cached.Solid;
            try
            {
                var geom = el.get_Geometry(_liveOpts);
                return geom != null ? ExtractBestSolid(geom) : null;
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Collector] GetSolid: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  DISCIPLINE DETECTION  — v5.1: expanded category list
        // ════════════════════════════════════════════════════════════════

        public static Discipline GetDiscipline(Element el)
        {
            if (el?.Category == null) return Discipline.Unknown;

            switch (el.Category.Id.Value)
            {
                // ── HVAC ──────────────────────────────────────────────
                case (long)BuiltInCategory.OST_DuctCurves:
                case (long)BuiltInCategory.OST_DuctFitting:
                case (long)BuiltInCategory.OST_FlexDuctCurves:
                case (long)BuiltInCategory.OST_DuctAccessory:
                case (long)BuiltInCategory.OST_MechanicalEquipment:
                case (long)BuiltInCategory.OST_DuctTerminal:    // FIX v5.1
                    return Discipline.HVAC;

                // ── Plumbing / Drainage / Medical Gas ────────────────
                case (long)BuiltInCategory.OST_PipeCurves:
                case (long)BuiltInCategory.OST_PipeFitting:
                case (long)BuiltInCategory.OST_FlexPipeCurves:
                case (long)BuiltInCategory.OST_PipeAccessory:   // FIX v5.1
                case (long)BuiltInCategory.OST_PlumbingFixtures:
                    // FIX v9.0: delegated to SystemClassificationService, which
                    // reads Revit's own RBS_SYSTEM_CLASSIFICATION_PARAM as the
                    // primary (locale-stable, naming-independent) signal and
                    // only falls back to project-configurable keywords for the
                    // genuinely ambiguous "Other"-classified systems (medical
                    // gas, storm). See Core/SystemClassificationService.cs for
                    // full rationale — this replaces the old keyword-only
                    // IsMedicalGas()/IsGravityDrainage() classifiers, which
                    // silently mis-labelled every pipe whose System Type name
                    // didn't literally contain one of a small hardcoded set of
                    // English keywords.
                    return SystemClassificationService.ClassifyPipeDiscipline(el);

                // ── Cable Tray ────────────────────────────────────────
                case (long)BuiltInCategory.OST_CableTray:
                case (long)BuiltInCategory.OST_CableTrayFitting:
                    return Discipline.CableTray;

                // ── Conduit ───────────────────────────────────────────
                case (long)BuiltInCategory.OST_Conduit:
                case (long)BuiltInCategory.OST_ConduitFitting:
                    return Discipline.Conduit;

                // ── Electrical (expanded in v5.1) ─────────────────────
                case (long)BuiltInCategory.OST_ElectricalEquipment:
                case (long)BuiltInCategory.OST_LightingFixtures:
                case (long)BuiltInCategory.OST_CommunicationDevices: // FIX v5.1
                case (long)BuiltInCategory.OST_DataDevices:           // FIX v5.1
                case (long)BuiltInCategory.OST_NurseCallDevices:      // FIX v5.1
                case (long)BuiltInCategory.OST_SecurityDevices:       // FIX v5.1
                case (long)BuiltInCategory.OST_TelephoneDevices:      // FIX v5.1
                    return Discipline.Electrical;

                // ── Fire Protection ───────────────────────────────────
                case (long)BuiltInCategory.OST_Sprinklers:
                case (long)BuiltInCategory.OST_FireAlarmDevices:
                    return Discipline.FireProtection;

                // ── Structural ────────────────────────────────────────
                case (long)BuiltInCategory.OST_StructuralFraming:
                case (long)BuiltInCategory.OST_StructuralColumns:
                case (long)BuiltInCategory.OST_Floors:
                case (long)BuiltInCategory.OST_Walls:
                case (long)BuiltInCategory.OST_Ceilings:
                    return Discipline.Structural;

                // ── Generic Model — resolve via system param heuristic ─
                case (long)BuiltInCategory.OST_GenericModel:         // FIX v5.1
                    return GuessGenericModelDiscipline(el);

                default:
                    // FIX v6.1 (Plan Phase 2): Newly placed elements may not have a
                    // system assigned yet, causing GetDiscipline to return Unknown and
                    // silently drop them from the targeted-scan path.
                    // InferDisciplineFromCategory uses category ID alone — no system
                    // parameter needed — so it works immediately after element placement.
                    return InferDisciplineFromCategory(el.Category?.Id.Value ?? 0);
            }
        }

        public static BuiltInCategory[] GetCategoriesForDiscipline(Discipline disc)
        {
            switch (disc)
            {
                case Discipline.Structural:
                    return new[]
                    {
                        BuiltInCategory.OST_StructuralFraming,
                        BuiltInCategory.OST_StructuralColumns,
                        BuiltInCategory.OST_Floors,
                        BuiltInCategory.OST_Walls,
                        BuiltInCategory.OST_Ceilings
                    };
                case Discipline.HVAC:
                    return new[]
                    {
                        BuiltInCategory.OST_DuctCurves,
                        BuiltInCategory.OST_DuctFitting,
                        BuiltInCategory.OST_FlexDuctCurves,
                        BuiltInCategory.OST_DuctAccessory,
                        BuiltInCategory.OST_DuctTerminal,
                        BuiltInCategory.OST_MechanicalEquipment
                    };
                case Discipline.Plumbing:
                case Discipline.GravityDrainage:
                case Discipline.MedicalGas:
                    return new[]
                    {
                        BuiltInCategory.OST_PipeCurves,
                        BuiltInCategory.OST_PipeFitting,
                        BuiltInCategory.OST_FlexPipeCurves,
                        BuiltInCategory.OST_PipeAccessory,
                        BuiltInCategory.OST_PlumbingFixtures
                    };
                case Discipline.CableTray:
                    return new[] { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting };
                case Discipline.Conduit:
                    return new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting };
                case Discipline.Electrical:
                    return new[]
                    {
                        BuiltInCategory.OST_ElectricalEquipment,
                        BuiltInCategory.OST_LightingFixtures,
                        BuiltInCategory.OST_CommunicationDevices,
                        BuiltInCategory.OST_DataDevices,
                        BuiltInCategory.OST_NurseCallDevices,
                        BuiltInCategory.OST_SecurityDevices,
                        BuiltInCategory.OST_TelephoneDevices
                    };
                case Discipline.FireProtection:
                    return new[] { BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_FireAlarmDevices };
                default:
                    return new BuiltInCategory[0];
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ELEMENT METADATA HELPERS  — v6.1
        //  Used by ClashEngine.BuildResult to populate filter/display fields.
        // ════════════════════════════════════════════════════════════════

        /// <summary>Human-readable category label for display and filtering.</summary>
        public static string GetCategoryName(Element el)
        {
            if (el?.Category == null) return "";
            long catId = el.Category.Id.Value;
            // Map common MEP/structural categories to friendly names
            switch (catId)
            {
                case (long)BuiltInCategory.OST_PipeCurves:         return "Pipe";
                case (long)BuiltInCategory.OST_PipeFitting:        return "Pipe Fitting";
                case (long)BuiltInCategory.OST_PipeAccessory:      return "Pipe Accessory";
                case (long)BuiltInCategory.OST_FlexPipeCurves:     return "Flex Pipe";
                case (long)BuiltInCategory.OST_DuctCurves:         return "Duct";
                case (long)BuiltInCategory.OST_DuctFitting:        return "Duct Fitting";
                case (long)BuiltInCategory.OST_FlexDuctCurves:     return "Flex Duct";
                case (long)BuiltInCategory.OST_DuctAccessory:      return "Duct Accessory";
                case (long)BuiltInCategory.OST_DuctTerminal:       return "Air Terminal";
                case (long)BuiltInCategory.OST_MechanicalEquipment:return "Mech. Equipment";
                case (long)BuiltInCategory.OST_CableTray:          return "Cable Tray";
                case (long)BuiltInCategory.OST_CableTrayFitting:   return "CT Fitting";
                case (long)BuiltInCategory.OST_Conduit:            return "Conduit";
                case (long)BuiltInCategory.OST_ConduitFitting:     return "Conduit Fitting";
                case (long)BuiltInCategory.OST_ElectricalEquipment:return "Elec. Equipment";
                case (long)BuiltInCategory.OST_LightingFixtures:   return "Lighting";
                case (long)BuiltInCategory.OST_Sprinklers:         return "Sprinkler";
                case (long)BuiltInCategory.OST_FireAlarmDevices:   return "Fire Alarm";
                case (long)BuiltInCategory.OST_StructuralFraming:  return "Structural Framing";
                case (long)BuiltInCategory.OST_StructuralColumns:  return "Column";
                case (long)BuiltInCategory.OST_Floors:             return "Floor / Slab";
                case (long)BuiltInCategory.OST_Walls:              return "Wall";
                case (long)BuiltInCategory.OST_Ceilings:           return "Ceiling";
                case (long)BuiltInCategory.OST_PlumbingFixtures:   return "Plumbing Fixture";
                default:                                            return el.Category.Name;
            }
        }

        /// <summary>Returns "FamilyName : TypeName" or just TypeName for system families.</summary>
        public static string GetFamilyTypeName(Element el)
        {
            if (el == null) return "";
            try
            {
                if (el is FamilyInstance fi)
                {
                    string fam  = fi.Symbol?.FamilyName ?? "";
                    string type = fi.Symbol?.Name ?? "";
                    return string.IsNullOrEmpty(fam) ? type : $"{fam} : {type}";
                }
                // System family (pipe, duct, cable tray…)
                var typeEl = el.Document.GetElement(el.GetTypeId());
                return typeEl?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Returns structural sub-classification for grouping purposes.</summary>
        public static string GetStructuralSubType(Element el)
        {
            if (el?.Category == null) return "";
            long catId = el.Category.Id.Value;
            if (catId == (long)BuiltInCategory.OST_Floors)            return "Slab";
            if (catId == (long)BuiltInCategory.OST_StructuralColumns) return "Column";
            if (catId == (long)BuiltInCategory.OST_Walls)             return "Shear Wall";
            if (catId == (long)BuiltInCategory.OST_Ceilings)          return "Ceiling";
            if (catId == (long)BuiltInCategory.OST_StructuralFraming)
            {
                // FIX v9.0: the old code built an always-empty `usage` string
                // ("Note: ... don't exist in Revit 2024 API — default to
                // 'Beam'"), which is incorrect — FamilyInstance.StructuralUsage
                // is a documented property (Autodesk.Revit.DB.Structure.
                // StructuralInstanceUsage) available since Revit 2011 and
                // reliably distinguishes beams from braces, joists, purlins
                // and horizontal bracing. Every structural framing element
                // was previously labelled "Beam" regardless of its real role.
                try
                {
                    if (el is FamilyInstance fi)
                    {
                        switch (fi.StructuralUsage)
                        {
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.Brace:
                                return "Brace";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.Girder:
                                return "Girder";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.Joist:
                                return "Joist";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.Purlin:
                                return "Purlin";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.HorizontalBracing:
                                return "Horizontal Bracing";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.TrussChord:
                                return "Truss Chord";
                            case Autodesk.Revit.DB.Structure.StructuralInstanceUsage.TrussWeb:
                                return "Truss Web";
                            default:
                                return "Beam";
                        }
                    }
                }
                catch { return "Beam"; }
                return "Beam";
            }
            return el.Category.Name;
        }

        // ════════════════════════════════════════════════════════════════
        //  CONNECTION DETECTION  — v6.1
        //  Used by ClashEngine to exclude same-system / connected pairs.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if elA and elB are physically connected (share a connector)
        /// OR belong to the same MEPSystem instance.
        /// Same-connected pairs are NOT real clashes — they are designed joints.
        /// </summary>
        /// <summary>
        /// Returns true ONLY when elA and elB are physically snapped together
        /// at a shared connector — i.e. they are directly connected segments.
        ///
        /// FIX v6.1.1: Check 1 (MEPSystem identity) has been removed.
        ///
        /// Why: MEPSystem.Id is a BUILDING-WIDE identifier.  Every WP drain pipe
        /// in the entire model shares one MEPSystem instance.  Using that as an
        /// exclusion criterion eliminates all host-model pairs in a properly
        /// modelled MEP project — exactly the opposite of what we want.
        ///
        /// Only Check 2 (direct connector sharing) is kept.  Two elements that
        /// share a physical connector ARE a designed connection and cannot clash.
        /// Two elements that merely belong to the same named system but are not
        /// snapped together CAN still physically intersect and must be checked.
        /// </summary>
        public static bool AreSameConnectedSystem(Element elA, Element elB)
        {
            if (elA == null || elB == null) return false;
            try
            {
                // Only exclusion: physically share a connector (directly connected)
                ConnectorManager? cmA = GetConnectorManager(elA);
                if (cmA == null) return false;

                foreach (Connector cA in cmA.Connectors)
                {
                    try
                    {
                        foreach (Connector refC in cA.AllRefs)
                        {
                            if (refC?.Owner?.Id == elB.Id && cA.ConnectorType != ConnectorType.Logical && refC.ConnectorType != ConnectorType.Logical && cA.IsConnectedTo(refC))
                                return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns true if this element is a support/hanger/sleeve that is
        /// DESIGNED to touch structural elements and should not be flagged.
        /// </summary>
        public static bool IsSupportOrAccessoryFamily(Element el)
        {
            if (el == null) return false;
            try
            {
                string famName = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "").ToUpperInvariant();
                string typName = (el.Name ?? "").ToUpperInvariant();
                string combined = famName + " " + typName;

                return combined.Contains("HANGER")    || combined.Contains("SUPPORT")  ||
                       combined.Contains("SLEEVE")    || combined.Contains("BRACKET")  ||
                       combined.Contains("ANCHOR")    || combined.Contains("PENETRAT") ||
                       combined.Contains("BLOCKOUT")  || combined.Contains("CLAMP")    ||
                       combined.Contains("TRAPEZE")   || combined.Contains("ROD");
            }
            catch { return false; }
        }

        // ── Private helpers ───────────────────────────────────────────────

        private static ConnectorManager? GetConnectorManager(Element el)
        {
            try
            {
                // MEPCurve (Pipe, Duct, CableTray, Conduit) exposes ConnectorManager directly.
                if (el is MEPCurve curve) return curve.ConnectorManager;

                // FIX v8.0: fi.MEPModel is of type MEPModel, NOT ConnectorManager.
                // The old code returned fi.MEPModel as ConnectorManager which is an
                // invalid cast — causing a silent runtime exception that made
                // AreSameConnectedSystem always return false for FamilyInstance elements.
                // Pipe fittings, duct fittings and terminals were never excluded as
                // connected pairs, generating false-positive clashes on every joint.
                // Correct: read fi.MEPModel.ConnectorManager (the actual CM property).
                if (el is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }
            return null;
        }

        // ── Existing private helpers (unchanged) ──────────────────────────

        private static List<Element> GetRawByDiscipline(Document doc, Discipline disc)
        {
            var cats = GetCategoriesForDiscipline(disc);
            var res  = new List<Element>();
            foreach (var cat in cats)
            {
                try { res.AddRange(new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().ToElements()); }
                catch (System.Exception ex) { Debug.WriteLine($"[Collector] Raw: {ex.Message}"); }
            }
            return res;
        }

        private static bool HasSolidVolume(GeometryElement g)
        {
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > MinSolidVolume) return true;
                if (o is GeometryInstance gi && HasSolidVolume(gi.GetInstanceGeometry())) return true;
            }
            return false;
        }

        private static Solid? ExtractBestSolid(GeometryElement g)
        {
            Solid? best = null; double bv = MinSolidVolume;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > bv) { best = s; bv = s.Volume; }
                else if (o is GeometryInstance gi)
                {
                    var n = ExtractBestSolid(gi.GetInstanceGeometry());
                    if (n != null && n.Volume > bv) { best = n; bv = n.Volume; }
                }
            }
            return best;
        }

        // FIX v9.0: IsGravityDrainage()/IsMedicalGas() removed — their logic
        // now lives in Core/SystemClassificationService.ClassifyPipeDiscipline(),
        // which checks Revit's RBS_SYSTEM_CLASSIFICATION_PARAM first and only
        // uses keywords (now project-configurable via SystemKeywords.json) as
        // a fallback for the "Other"-classified bucket. See that file for the
        // full root-cause writeup.

        /// <summary>
        /// For Generic Model families, guess the discipline from the family name
        /// or system parameters. Returns Unknown if no match found.
        /// </summary>

        // ════════════════════════════════════════════════════════════════
        //  INFER DISCIPLINE FROM CATEGORY  — v6.1 (Plan Phase 2)
        //
        //  Centralised fallback used by GetDiscipline when the element's
        //  MEP system parameter is not yet assigned (e.g. just placed).
        //  Works on category ID alone so it fires immediately after placement.
        //  Moved here from EventListener so ALL detection paths benefit.
        // ════════════════════════════════════════════════════════════════

        public static Discipline InferDisciplineFromCategory(long catId)
        {
            if (catId == (long)BuiltInCategory.OST_DuctCurves    ||
                catId == (long)BuiltInCategory.OST_DuctFitting    ||
                catId == (long)BuiltInCategory.OST_FlexDuctCurves ||
                catId == (long)BuiltInCategory.OST_DuctAccessory  ||
                catId == (long)BuiltInCategory.OST_DuctTerminal   ||
                catId == (long)BuiltInCategory.OST_MechanicalEquipment)
                return Discipline.HVAC;

            if (catId == (long)BuiltInCategory.OST_CableTray ||
                catId == (long)BuiltInCategory.OST_CableTrayFitting)
                return Discipline.CableTray;

            if (catId == (long)BuiltInCategory.OST_Conduit ||
                catId == (long)BuiltInCategory.OST_ConduitFitting)
                return Discipline.Conduit;

            if (catId == (long)BuiltInCategory.OST_Sprinklers ||
                catId == (long)BuiltInCategory.OST_FireAlarmDevices)
                return Discipline.FireProtection;

            if (catId == (long)BuiltInCategory.OST_ElectricalEquipment ||
                catId == (long)BuiltInCategory.OST_LightingFixtures     ||
                catId == (long)BuiltInCategory.OST_CommunicationDevices ||
                catId == (long)BuiltInCategory.OST_DataDevices          ||
                catId == (long)BuiltInCategory.OST_NurseCallDevices     ||
                catId == (long)BuiltInCategory.OST_SecurityDevices      ||
                catId == (long)BuiltInCategory.OST_TelephoneDevices)
                return Discipline.Electrical;

            if (catId == (long)BuiltInCategory.OST_PipeCurves    ||
                catId == (long)BuiltInCategory.OST_PipeFitting    ||
                catId == (long)BuiltInCategory.OST_FlexPipeCurves ||
                catId == (long)BuiltInCategory.OST_PipeAccessory  ||
                catId == (long)BuiltInCategory.OST_PlumbingFixtures)
                return Discipline.Plumbing;

            return Discipline.Unknown;
        }

        private static Discipline GuessGenericModelDiscipline(Element el)
        {
            try
            {
                string name = (el.Name ?? "").ToUpperInvariant();
                string famName = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "").ToUpperInvariant();
                string combined = name + " " + famName;

                if (combined.Contains("DUCT") || combined.Contains("AIR") || combined.Contains("HVAC") ||
                    combined.Contains("VENT") || combined.Contains("MECH"))
                    return Discipline.HVAC;

                // v9.0: check medical-gas keywords (project-configurable via
                // SystemKeywords.json) BEFORE the generic PIPE/PLUMB bucket —
                // manufacturer content for outlets/alarm panels/zone valves
                // (e.g. BeaconMedaes) is frequently modelled as Generic Model
                // or Specialty Equipment rather than a pipe-derived category.
                if (ContainsAnyKeyword(combined, SystemKeywordConfig.Load().MedicalGasKeywords))
                    return Discipline.MedicalGas;

                if (combined.Contains("PIPE") || combined.Contains("PLUMB") ||
                    combined.Contains("DRAIN") || combined.Contains("WATER"))
                    return Discipline.Plumbing;

                if (combined.Contains("CABLE") || combined.Contains("TRAY") ||
                    combined.Contains("CONDUIT") || combined.Contains("ELEC"))
                    return Discipline.Electrical;

                if (combined.Contains("SPRINK") || combined.Contains("FIRE"))
                    return Discipline.FireProtection;
            }
            catch { }
            return Discipline.Unknown;
        }

        private static bool ContainsAnyKeyword(string haystack, System.Collections.Generic.IEnumerable<string> needles)
        {
            if (string.IsNullOrEmpty(haystack) || needles == null) return false;
            foreach (var n in needles)
            {
                if (!string.IsNullOrWhiteSpace(n) && haystack.Contains(n.ToUpperInvariant()))
                    return true;
            }
            return false;
        }
    }
}
