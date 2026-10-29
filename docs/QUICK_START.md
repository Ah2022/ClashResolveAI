# ClashResolveAI — quick start

**Revit 2024 · MEP AI Tools → ClashResolve AI 9.4**

## 1. Run your first coordination scan

1. Open the host project and load the RVT links you need.
2. Click **Full Scan** and keep **Quick coordination** selected.
3. Choose **one floor** or bounded zones, then select the required loaded links.
4. Use **Rules & classification** to review Unknown elements and project mappings when needed.
5. Click **Calculate estimate**. Review the scope and workload; uncheck expensive model relationships and recalculate.
6. Click **Start reviewed scan**. Changed inputs require a new estimate.

Quick finds hard clashes between host elements and selected loaded links. It does not automatically scan every link against every other link. Generic models, insulation and support families are excluded by default.

## 2. Review and coordinate

Open **Dashboard → Issues**. Inspect the geometry in 2D/3D, navigate to the affected elements and review the evidence. Use **Groups**, owners, due dates and comments to coordinate work. **History** and **Analytics** show saved scans and issue trends.

Fix the model in Revit, then rerun the relevant Full Scan. Select the intended current/saved view and filters before exporting **BCF, Excel or Word**. Bounding-box candidates are not confirmed clashes; **Unverified** findings need review.

## 3. Choose another scan when needed

| Mode | Use it for |
|---|---|
| **Quick** | Routine hard-clash checks on one floor or bounded zones. |
| **Detailed** | Hard clashes first, then selected clearance rules around confirmed hard-clash areas. Review the area radius; each completed pass is saved. No confirmed areas means clearance is skipped. |
| **Advanced** | Broader clearance, discipline/category choices, explicitly mapped generic models, dedicated insulation clearance or selected link-to-link pairs. |

For **link-to-link**, select both loaded link instances, enable the separate job and add only the required pairs. To include a generic model, create an explicit classification mapping and enable mapped generic models in Advanced.

## 4. Use local feedback while modelling

Click **Live Monitor** to open **Clash Radar**. Choose **Live** for automatic local checks, **Trigger** to accumulate edits until **Check Changes**, or **Off** to stop checking. Place/edit supported host elements, inspect the local findings and fix them in Revit.

**Full Scan → Dashboard. Live Monitor → Clash Radar.** Live checks do not refresh or certify a saved Dashboard scan. Run Full Scan again before sharing current coordination results.

## If something is missing

Check the selected floor/zones, loaded links, relationships, discipline/category rules and classification exclusions. Review Unknown families before mapping them. If an estimate expires, recalculate. Duration is unavailable until comparable completed scans provide timings; ETA is a planning aid.

[Visual usage map](USAGE_MAP.md) · [Full documentation](../README.md)
