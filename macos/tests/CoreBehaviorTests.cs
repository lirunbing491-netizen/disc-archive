using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using DiscArchive;

internal static class CoreBehaviorTests
{
    private static string root;
    private static int passed;
    private static string Dir(string name) {return Directory.CreateDirectory(Path.Combine(root,name)).FullName;}
    private static void Check(bool yes,string message) {if(!yes)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
    private static void Throws(Action action,string message) {try{action();}catch(IOException){Check(true,message);return;}throw new Exception(message);}
    private static string Enc(string value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
    private static string Digest(string value){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","");}
    private sealed class Reader:IReader
    {
        internal string Source;
        public string[] Drives(){return new[]{"synthetic-drive"};}
        public bool Ready(string drive){return true;}
        public bool Empty(string drive){return false;}
        public MountedDisc Open(string drive){return new MountedDisc(Source,"240102-Client",delegate{},null,null,"Synthetic only");}
    }
    private static void WindowsJournal()
    {
        string source=Dir("journal-source"),target=Dir("journal-target"),journal=Dir("journal-target/DiscArchive/OLD");
        string id=Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(target,".discarchive-target.id"),id);
        var owner=new Entry {Id="OLD",TargetId=id,TargetRoot=@"Z:\Backup\"};
        using(var f=File.Create(Path.Combine(journal,"entry.xml")))new XmlSerializer(typeof(List<Entry>)).Serialize(f,new List<Entry>{owner});
        string relative=@"folder\file.bin",destination=@"Project\folder\file.bin",body="F\t"+Enc(relative)+"\t"+Enc(destination)+"\t4\t"+new string('A',64);
        File.WriteAllText(Path.Combine(journal,"continuation-v1.tsv"),"DiscArchive continuation v1\n"+body+"\t"+Digest(body)+"\n");
        File.WriteAllText(Path.Combine(journal,"continuation-target.txt"),"guid:"+id);
        using(var j=new ContinuationJournal(journal,source,target))
        {
            var e=j.Find(Path.Combine(source,"folder","file.bin"));
            Check(e!=null && e.Complete,"Windows v1 receipt routes decode on the current platform");
            Check(j.Destination(e)==Path.Combine(target,"Project","folder","file.bin"),"destination remains beneath current mounted backup");
        }
        string legacy=Dir("legacy-target"),old=Dir("legacy-target/DiscArchive/OLD");
        File.WriteAllText(Path.Combine(legacy,".discarchive-target.id"),id);
        using(var f=File.Create(Path.Combine(old,"entry.xml")))new XmlSerializer(typeof(List<Entry>)).Serialize(f,new List<Entry>{owner});
        File.WriteAllText(Path.Combine(old,"路径清单.tsv"),"文件\tE:\\\\folder\\\\file.bin\tZ:\\\\Backup\\\\Project\\\\folder\\\\file.bin\t4\n");
        using(var j=new ContinuationJournal(old,source,legacy))
        {
            var e=j.Find(Path.Combine(source,"folder","file.bin"));
            Check(e!=null && !e.Complete,"legacy Windows routes import without declaring files verified");
            Check(j.Destination(e)==Path.Combine(legacy,"Project","folder","file.bin"),"legacy target is mapped by authenticated original root");
        }
        string bad=Dir("unsafe-legacy"),badJournal=Dir("unsafe-legacy/DiscArchive/OLD");
        File.WriteAllText(Path.Combine(bad,".discarchive-target.id"),id);
        using(var f=File.Create(Path.Combine(badJournal,"entry.xml")))new XmlSerializer(typeof(List<Entry>)).Serialize(f,new List<Entry>{owner});
        File.WriteAllText(Path.Combine(badJournal,"路径清单.tsv"),"文件\tE:\\\\file.bin\tY:\\\\Other\\\\file.bin\t4\n");
        Throws(()=>{using(var j=new ContinuationJournal(badJournal,source,bad)){}},"unprovable old destination mapping stops explicitly");
        Check(!File.Exists(Path.Combine(badJournal,"continuation-v1.tsv")),"unsafe legacy import does not publish a replacement journal");
        string journalText=File.ReadAllText(Path.Combine(journal,"continuation-v1.tsv"));
        File.WriteAllText(Path.Combine(journal,"continuation-v1.tsv"),journalText.Replace(new string('A',64),new string('B',64)));
        Throws(()=>{using(var j=new ContinuationJournal(journal,source,target)){}},"damaged journal checksum is refused without trusting completion");
        if(Path.DirectorySeparatorChar=='/')
        {
            string caseJournal=Dir("case-journal");
            using(var j=new ContinuationJournal(caseJournal,source,target))
            {
                j.Route(Path.Combine(source,"A"),Path.Combine(target,"Project","A"),false);
                j.Route(Path.Combine(source,"a"),Path.Combine(target,"Project","a"),false);
                Check(j.Find(Path.Combine(source,"A")).Destination!=j.Find(Path.Combine(source,"a")).Destination,"POSIX source paths retain case distinctions");
                Throws(()=>j.Route(Path.Combine(source.ToUpperInvariant(),"bad"),Path.Combine(target,"Project","bad"),false),"POSIX source-root case mismatch is refused");
            }
        }
    }
    private static async Task Workflow()
    {
        string source=Dir("disc");Directory.CreateDirectory(Path.Combine(source,"nested"));File.WriteAllText(Path.Combine(source,"nested","file.txt"),"full payload");
        string target=Dir("original-mount"),photo=Path.Combine(root,"original-photo.raw");byte[] full=Enumerable.Range(0,10000).Select(x=>(byte)(x%251)).ToArray();File.WriteAllBytes(photo,full);
        PortableSupport.ThumbnailForTests=delegate(Stream input){return new byte[]{255,216,255,217};};
        var reader=new Reader {Source=source};var book=new Catalog(Dir("catalog1"));var station=new Workstation(book,reader);
        Entry entry=station.Register(photo,target,"","synthetic-drive","240102-Client",true);
        Check(File.ReadAllBytes(entry.OriginalPhoto).SequenceEqual(full),"registration preserves the full original photograph");
        Entry copied=await station.Copy(delegate{});
        Check(copied.Status==Workstation.Done && copied.Files==1 && copied.Failed==0,"shared workstation copies and reports verified files");
        string moved=Path.Combine(root,"renamed-mount");Directory.Move(target,moved);
        Check(!book.IsOriginalTarget(entry),"old mount path no longer authorizes writes");
        var book2=new Catalog(Dir("catalog2"));var next=new Workstation(book2,reader);
        Entry resumed=next.ResumeKnown(moved,entry.Id,"synthetic-drive");
        Check(resumed.TargetId==entry.TargetId && resumed.CanonicalId==entry.Id,"same TargetId resumes old disc number after mount change");
        Entry reconciled=await next.Copy(delegate{});
        Check(reconciled.Status==Workstation.Done && reconciled.ReusedFiles==1 && reconciled.Failed==0,"continuation rehashes and reuses the complete old file");
        string original=Catalog.OriginalPhotoAt(moved,new Entry{Id=entry.Id,OriginalPhoto=@"Z:\Backup\DiscArchive\"+entry.Id+@"\photo.raw"});
        Check(File.ReadAllBytes(original).SequenceEqual(full),"Windows original-photo receipt is readable after relocation");
        ProjectAliases.Remember(moved,"240102-ClientAlias","240102-Client");
        Check(ProjectAliases.ResolveForWrite(moved,"240102-ClientAlias",false,CancellationToken.None).Resolved=="240102-Client","project aliases remain shared");
        PortableSupport.ThumbnailForTests=null;
        Environment.SetEnvironmentVariable("DISCARC_MEDIA_HELPER",Path.Combine(root,"missing-helper"));
        int before=book2.List().Count;
        Throws(()=>book2.Create(photo,moved,"synthetic-drive",""),"missing native photo helper fails before registration");
        Check(book2.List().Count==before,"failed thumbnail leaves no catalog entry");
    }
    public static async Task<int> Main()
    {
        root=Path.Combine(Path.GetTempPath(),"discarchive-mac-core-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Console.WriteLine("Fixture: "+root);
        WindowsJournal();await Workflow();await HostProtocolTests.Run();Console.WriteLine("PASS portable behavior total="+passed);
        if(ArchiveTreeTests.Main()!=0 || PlanTests.Main()!=0 || CameraFileDateTests.Main(new string[0])!=0 || LayoutTests.Main()!=0 || HfsGuardTests.Main()!=0 || SourcePipelineTests.Main()!=0 || IdentityTests.Main()!=0) return 1;
        Console.WriteLine("PASS all shared regression suites");return 0;
    }
}
