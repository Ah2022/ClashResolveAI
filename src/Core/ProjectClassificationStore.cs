using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClashResolveAI.Core
{
    public sealed class ProjectMappingSnapshot
    {
        public string DocumentKey { get; }
        public IReadOnlyList<ClassificationMapping> Mappings { get; }
        public string Fingerprint { get; }
        internal ProjectMappingSnapshot(string documentKey,IEnumerable<ClassificationMapping> mappings)
        {
            DocumentKey=documentKey;Mappings=Array.AsReadOnly(mappings.OrderBy(m=>m.Id,StringComparer.Ordinal).ToArray());
            Fingerprint=ProjectClassificationStore.Hash(JsonConvert.SerializeObject(new {Version=1,DocumentKey,Mappings}));
        }
    }
    /// <summary>Project-scoped atomic storage; no Revit handles or application credentials.</summary>
    public sealed class ProjectClassificationStore
    {
        private sealed class FileData
        {
            public int Version { get; set; }=1;
            public string DocumentKey { get; set; }="";
            public List<ClassificationMapping> Mappings { get; set; }=new List<ClassificationMapping>();
        }
        private readonly string _root;
        public ProjectClassificationStore(string? root=null)
        {
            string verification=Environment.GetEnvironmentVariable("CLASHRESOLVE_VERIFY_DIR")??"";
            _root=Path.GetFullPath(root ?? (verification!=""?Path.Combine(verification,"classifications"):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ClashResolveAI","Projects")));
        }
        internal static string Hash(string text) {using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","");}
        public string PathFor(string documentKey)
        {
            if(string.IsNullOrWhiteSpace(documentKey))throw new ArgumentException("A project identity is required.");
            return Path.Combine(_root,Hash(documentKey),"ClassificationMappings.json");
        }
        public ProjectMappingSnapshot Load(string documentKey)
        {
            string path=PathFor(documentKey);
            if(!File.Exists(path))return new ProjectMappingSnapshot(documentKey,Array.Empty<ClassificationMapping>());
            var data=Read(path);
            if(data.DocumentKey!=documentKey)throw new InvalidDataException("Classification mappings belong to a different project. Import them explicitly to reuse them.");
            return new ProjectMappingSnapshot(documentKey,Validate(data.Mappings));
        }
        public ProjectMappingSnapshot Save(string documentKey,IEnumerable<ClassificationMapping> mappings,string expectedFingerprint)
        {
            var validated=Validate(mappings);string path=PathFor(documentKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Exclusive lock spans read/compare/replace, including other app processes.
            using var gate=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(Load(documentKey).Fingerprint!=expectedFingerprint)throw new InvalidOperationException("Project mappings changed since this editor opened. Reload before saving.");
            var snapshot=new ProjectMappingSnapshot(documentKey,validated);
            AtomicWrite(path,JsonConvert.SerializeObject(new FileData {DocumentKey=documentKey,Mappings=snapshot.Mappings.ToList()},Formatting.Indented));
            return snapshot;
        }
        public IReadOnlyList<ClassificationMapping> ReadImport(string path)=>Array.AsReadOnly(Validate(Read(path).Mappings).ToArray());
        public void Export(string path,string documentKey,IEnumerable<ClassificationMapping> mappings)
        {
            var data=new FileData {DocumentKey=documentKey,Mappings=Validate(mappings)};
            AtomicWrite(Path.GetFullPath(path),JsonConvert.SerializeObject(data,Formatting.Indented));
        }
        private static FileData Read(string path)
        {
            if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Mapping file exceeds the 8 MB limit.");
            FileData data;
            try {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                using var reader=new StreamReader(stream);
                data=JsonConvert.DeserializeObject<FileData>(reader.ReadToEnd(),new JsonSerializerSettings {MissingMemberHandling=MissingMemberHandling.Error})??throw new InvalidDataException("Empty mapping file.");
            }
            catch(JsonException ex){throw new InvalidDataException("Invalid classification mapping file: "+ex.Message,ex);}
            if(data.Version!=1)throw new InvalidDataException("Unsupported classification mapping version.");
            if(string.IsNullOrWhiteSpace(data.DocumentKey)||data.Mappings==null)throw new InvalidDataException("Mapping file is missing its project identity or rules.");
            return data;
        }
        private static List<ClassificationMapping> Validate(IEnumerable<ClassificationMapping> mappings)
        {
            var result=mappings.ToList();
            if(result.Count>5000)throw new InvalidDataException("A project supports at most 5,000 classification mappings.");
            if(result.Any(m=>m==null||string.IsNullOrWhiteSpace(m.Id)||m.Id.Length>200||
                (!m.CategoryId.HasValue&&string.IsNullOrWhiteSpace(m.FamilyTypeContains))||
                !Enum.IsDefined(typeof(Discipline),m.Discipline)||!Enum.IsDefined(typeof(FamilyTypeMatch),m.MatchMode)||
                (!m.Exclude&&m.Discipline==Discipline.Unknown)))throw new InvalidDataException("Each mapping needs a valid condition and a known discipline, or an explicit exclusion.");
            if(result.GroupBy(m=>m.Id,StringComparer.Ordinal).Any(g=>g.Count()>1))throw new InvalidDataException("Mapping IDs must be unique.");
            return result;
        }
        private static void AtomicWrite(string path,string text)
        {
            if(Encoding.UTF8.GetByteCount(text)>8*1024*1024)throw new InvalidDataException("Mapping file exceeds the 8 MB limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep the temporary filename shorter than the destination for .NET Framework path limits.
            string temporary=Path.Combine(Path.GetDirectoryName(path)!,"."+Guid.NewGuid().ToString("N").Substring(0,16)+".tmp");
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false))) {writer.Write(text);writer.Flush();stream.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,path+".bak",true);else File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
