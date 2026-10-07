using System;
using System.Collections.Generic;
namespace ClashResolveAI.Dashboard.Application
{
    public sealed class HistoryEntry
    {
        public DateTime? WhenUtc {get;set;}
        public string Lane {get;set;}="";public string Time {get;set;}="";public string Scan {get;set;}="";public string Workflow {get;set;}="";public string Change {get;set;}="";public string Flags {get;set;}="";public string Detail {get;set;}="";public string ClashId {get;set;}="";
    }
    public interface IDashboardHistorySource {IReadOnlyList<HistoryEntry> Timeline(string clashId);}
}
