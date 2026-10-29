using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using ClashResolveAI.Dashboard.Domain;
using ClashResolveAI.Core;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
namespace ClashResolveAI.Dashboard.Application
{
    public enum DashboardExportFormat { Bcf, Excel, Word }
    public sealed class DashboardExportRequest
    {
        public string DocumentKey {get;set;}="";public string Project {get;set;}="";
        public string Scope {get;set;}="";public string Context {get;set;}="";
        public string ScanId {get;set;}="";public bool Current {get;set;}
        public List<ClashObservation> Rows {get;set;}=new List<ClashObservation>();
        public GroupView? Group {get;set;}
        public DashboardExportFormat Format {get;set;}
    }
    public static class DashboardExportService
    {
        public static DashboardExportRequest Capture(DashboardSnapshot snapshot,IReadOnlyList<ClashObservation> rows,GroupView? group,DashboardExportFormat format,string filters)
        {
            if(snapshot.Selected==null)throw new InvalidOperationException("A completed scan is required for export.");
            if(group!=null){if(!snapshot.Groups.Any(g=>g.GroupKey==group.Key))throw new InvalidOperationException("Browse the group's saved version before exporting its historical membership.");var members=new HashSet<string>(group.CurrentIds);rows=group.Members.Where(r=>members.Contains(r.ClashId)).ToList();}
            if(rows.Count==0)throw new InvalidOperationException("No issues in the selected export scope.");
            if(rows.Any(r=>r.DocumentKey!=snapshot.DocumentKey))throw new InvalidOperationException("Export scope contains another document.");
            var request=new DashboardExportRequest {DocumentKey=snapshot.DocumentKey,Project=snapshot.Selected.Capture.DisplayName,ScanId=snapshot.Selected.ScanId,Current=snapshot.IsCurrent,Format=format,Rows=rows.ToList(),Group=group,Scope=(snapshot.IsCurrent?"Current workflow":"Historical captured state")+" / "+(group==null?"filtered issues":"selected group "+group.Key)};
            request.Context=$"Scope: {request.Scope}\nDocument: {snapshot.DocumentKey}\nScan: V{snapshot.Selected.SequenceNumber:D3} ({snapshot.Selected.ScanId})\nCompleted: {snapshot.Selected.EndedAtUtc:O}\nBaseline: {snapshot.Comparison?.PreviousScanId}\nCoverage: {snapshot.Comparison?.CoverageNotice}\nModel: {snapshot.Selected.Capture.ModelFingerprint}\nMode: {snapshot.Selected.Capture.ScanMode}; rules: {snapshot.Selected.Capture.RuleSetName}\nConfiguration: {snapshot.Selected.Capture.ConfigurationFingerprint}\nCaptured scope: {snapshot.Selected.Capture.ScopeJson}\nCaptured links: {snapshot.Selected.Capture.LinkedModelsJson}\nFilters: {(group==null?filters:"Exact selected group membership")}\nIssues: {rows.Count}\nEvidence and labels are copied values. Historical geometry is not reconstructed; snapshot media are external file references.";
            return JsonConvert.DeserializeObject<DashboardExportRequest>(JsonConvert.SerializeObject(request))!;
        }
        public static string Write(DashboardExportRequest request,string folder)
        {
            Directory.CreateDirectory(folder);
            if(request.Format==DashboardExportFormat.Bcf){var topics=request.Group==null?DashboardExportProjection.IssueTopics(request.Rows):new List<ClashGroup>{DashboardExportProjection.GroupTopic(request.Group)};foreach(var topic in topics)topic.Metadata.ResolutionNotes+="\n\n"+request.Context;
                var file=new Services.BcfExportService().ExportGroups(topics,request.Project,folder);using(var zip=ZipFile.Open(file,ZipArchiveMode.Update)){using var writer=new StreamWriter(zip.CreateEntry("dashboard-context.json").Open());writer.Write(JsonConvert.SerializeObject(new {request.DocumentKey,request.Scope,request.ScanId,request.Current,request.Context},Formatting.Indented));}return file;}
            string name=string.Join("_",request.Project.Split(Path.GetInvalidFileNameChars()));if(name.Length>50)name=name.Substring(0,50);
            string path=Path.Combine(folder,"Coordination_"+name+"_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)+(request.Format==DashboardExportFormat.Excel?".xlsx":".docx"));
            if(request.Format==DashboardExportFormat.Excel)WriteExcel(request,path);else WriteWord(request,path);return path;
        }
        private static readonly string[] Headers={"Issue","Workflow","Change","Flags","Evidence type","Priority","Level","Group","Owner","Due (stored)","Element A","Element B","Geometry evidence","Gap mm","Overlap mm3","Required mm","Comments"};
        private static string[] Values(ClashObservation r)=>new[]{r.ClashId,r.Status,r.ChangeKind?.ToString()??"",r.ChangeFlags.ToString(),r.TestType,r.Priority,r.LevelName,r.GroupId,DashboardWorkspace.Metadata(r,"AssignedEngineer"),DashboardWorkspace.Metadata(r,"DueDate"),r.ElementAId+" / "+r.ElementUniqueIdA+" / "+r.LinkInstanceA,r.ElementBId+" / "+r.ElementUniqueIdB+" / "+r.LinkInstanceB,r.GeometryEvidence,r.GapMm.ToString(System.Globalization.CultureInfo.InvariantCulture),r.OverlapVolumeMm3.ToString(System.Globalization.CultureInfo.InvariantCulture),r.RequiredClearanceMm.ToString(System.Globalization.CultureInfo.InvariantCulture),DashboardWorkspace.Metadata(r,"Comments")};
        private static void WriteExcel(DashboardExportRequest request,string path)
        {
            using var book=new XLWorkbook();var context=book.Worksheets.Add("Export context");context.Cell(1,1).Value="FULL SCAN COORDINATION";context.Cell(1,1).Style.Font.Bold=true;
            int row=3;foreach(string line in request.Context.Split('\n'))context.Cell(row++,1).Value=line;context.Column(1).Width=115;context.Column(1).Style.Alignment.WrapText=true;
            var issues=book.Worksheets.Add("Issues");for(int i=0;i<Headers.Length;i++)issues.Cell(1,i+1).Value=Headers[i];row=2;
            foreach(var issue in request.Rows){var values=Values(issue);for(int i=0;i<values.Length;i++)issues.Cell(row,i+1).Value=values[i];row++;}
            issues.Range(1,1,row-1,Headers.Length).CreateTable();issues.SheetView.FreezeRows(1);issues.Columns().Width=24;issues.Column(13).Width=55;issues.Column(17).Width=55;issues.Columns().Style.Alignment.WrapText=true;
            if(request.Group!=null){var group=book.Worksheets.Add("Group");group.Cell(1,1).Value=request.Group.Title;group.Cell(2,1).Value=request.Group.State;group.Cell(3,1).Value=request.Group.Lineage;group.Cell(4,1).Value="Default owner: "+request.Group.Owner;group.Cell(5,1).Value="Effective owners: "+request.Group.EffectiveOwners;group.Column(1).Width=110;group.Column(1).Style.Alignment.WrapText=true;}
            book.SaveAs(path);
        }
        private static void WriteWord(DashboardExportRequest request,string path)
        {
            using var doc=WordprocessingDocument.Create(path,WordprocessingDocumentType.Document);var main=doc.AddMainDocumentPart();var body=new Body();main.Document=new Document(body);
            Paragraph Para(string text,bool bold=false)=>new Paragraph(new Run(new RunProperties(new Bold {Val=bold},new FontSize {Val=bold?"28":"20"}),new Text(text){Space=SpaceProcessingModeValues.Preserve}));
            body.Append(Para("Full Scan coordination report",true));foreach(string line in request.Context.Split('\n'))body.Append(Para(line));
            if(request.Group!=null){body.Append(Para(request.Group.Title,true));body.Append(Para(request.Group.State+" · "+request.Group.Lineage));}
            body.Append(new Paragraph(new Run(new Break {Type=BreakValues.Page})));
            foreach(var issue in request.Rows){body.Append(Para(issue.ClashId+" · "+issue.Status+" · "+issue.TestType,true));var values=Values(issue);var table=new Table(new TableProperties(new TableWidth {Width="10000",Type=TableWidthUnitValues.Dxa},new TableBorders(new TopBorder {Val=BorderValues.Single,Size=4},new BottomBorder {Val=BorderValues.Single,Size=4},new InsideHorizontalBorder {Val=BorderValues.Single,Size=4})));
                table.Append(new TableGrid(new GridColumn {Width="2200"},new GridColumn {Width="7800"}));
                for(int i=0;i<Headers.Length;i++){var tr=new TableRow(new TableRowProperties(new CantSplit()));tr.Append(new TableCell(new TableCellProperties(new TableCellWidth {Width="2200",Type=TableWidthUnitValues.Dxa}),Para(Headers[i])),new TableCell(new TableCellProperties(new TableCellWidth {Width="7800",Type=TableWidthUnitValues.Dxa}),Para(values[i])));table.Append(tr);}body.Append(table);body.Append(Para(""));}
            body.Append(new SectionProperties(new PageSize {Width=12240,Height=15840},new PageMargin {Top=720,Bottom=720,Left=720,Right=720}));main.Document.Save();
        }
    }
}
