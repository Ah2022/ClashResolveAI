// Only plain metadata/BCF serialization is tested here. This is not a Revit geometry test.
namespace Autodesk.Revit.DB
{
    public class ElementId { public long Value { get; } public ElementId(long value) { Value = value; } }
    public class Category { public string Name { get; set; } = "Test"; }
    public class Element { public bool IsValidObject => true; public Category Category { get; } = new(); public ElementId Id { get; set; } = new(1); }
    public class Document { public Element? GetElement(ElementId id) => null; public Element? GetElement(string id) => null; }
    public class Solid { }
    public class BoundingBoxXYZ { }
    public class XYZ
    {
        public double X { get; } public double Y { get; } public double Z { get; }
        public XYZ(double x,double y,double z) { X=x;Y=y;Z=z; }
        public static XYZ Zero => new(0,0,0);
    }
}
namespace ClashResolveAI.Core
{
    internal static class DocumentSession { public static bool Matches(ClashResult result, Autodesk.Revit.DB.Document doc) => true; }
}

namespace ClashResolveAI.Dashboard
{
    public sealed class ClashDashboard
    {
        public static ClashDashboard Instance { get; }=new();
        public void UpdateClashStatus(string id,ClashResolveAI.Core.ClashStatus status) { }
    }
}
