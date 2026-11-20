using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class MountedSourceTests
{
    private static readonly Assembly Core=Assembly.Load("discarchive-core");
    private const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
    private static int passed;
    private static Type T(string name)=>Core.GetType("DiscArchive."+name,true);
    private static object Call(object target,string method,params object[] args) {
        try{return target.GetType().GetMethod(method,Flags).Invoke(target,args);}
        catch(TargetInvocationException ex){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    private static void Check(bool yes,string message){if(!yes)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
    private static string[] Entries(object source,string path)=>((IEnumerable<string>)Call(source,"Entries",path)).ToArray();
    private static string Named(object source,string root,string name) {
        string[] paths=Entries(source,root);string found=paths.SingleOrDefault(p=>Path.GetFileName(p)==name);
        if(found!=null)return found;
        var details=new List<string>();foreach(string path in paths){try{details.Add(path+" "+Call(source,"Attributes",path));}catch(Exception ex){details.Add(path+" ERROR "+ex.Message);}}
        throw new IOException("Mounted fixture entry missing: "+name+"; actual entries: "+string.Join(" | ",details));
    }
    private static object Source(string root,string[] paths,Action guard) {
        Type volume=T("MacVolume");var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(volume));
        foreach(string path in paths){object v=Activator.CreateInstance(volume,true);volume.GetField("mount",Flags).SetValue(v,path);volume.GetField("label",Flags).SetValue(v,"SAME");volume.GetField("format",Flags).SetValue(v,"synthetic");list.Add(v);}
        return Activator.CreateInstance(T("MacMountedSource"),Flags,null,new object[]{root,list,guard},null);
    }
    private static void Throws(Action action,string message){try{action();}catch(IOException){Check(true,message);return;}catch(TargetInvocationException ex) when(ex.InnerException is IOException){Check(true,message);return;}throw new Exception(message);}
    private static byte[] Read(object source,string path){using var stream=(Stream)Call(source,"OpenRead",path);using var data=new MemoryStream();stream.CopyTo(data);return data.ToArray();}
    private static void PosixNames(string temp) {
        Type itemType=T("ArchiveItem");var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
        void Add(int index,string name,bool directory,int parent){object item=Activator.CreateInstance(itemType,true);foreach(var pair in new (string name,object value)[]{("Index",index),("Name",name),("Path",name),("IsDirectory",directory),("ParentIndex",parent),("Size",directory?0L:3L)})itemType.GetField(pair.name,Flags).SetValue(item,pair.value);list.Add(item);}
        Add(0,"dir\\literal",true,-1);Add(1,"child.jpg",false,0);Add(2,"CON.txt",false,-1);Add(3,"con.TXT",false,-1);
        string root=Path.Combine(temp,"posix");Func<int,Stream> open=_=>new MemoryStream(new byte[]{1,2,3});
        object tree=Activator.CreateInstance(T("ArchiveDiscSource"),Flags,null,new object[]{root,list,open,"","POSIX",null},null);
        string[] paths=Entries(tree,root);Check(paths.Length==3,"POSIX parent/name graph never splits literal backslash");
        string dir=paths.Single(p=>Path.GetFileName(p).Contains("dir~005Cliteral"));Check(Entries(tree,dir).Length==1,"POSIX literal directory retains child hierarchy");
        Check(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()==3,"Windows reserved names and case collisions remain distinct");
        object repeat=Activator.CreateInstance(T("ArchiveDiscSource"),Flags,null,new object[]{root,list,open,"","POSIX",null},null);Check(paths.SequenceEqual(Entries(repeat,root)),"unsafe-name mapping is repeatable");
    }
    private static void Coverage() {
        string Evaluate((string format,long offset,long length)[] regions,string[] formats,bool map=false) {
            object layout=Activator.CreateInstance(T("DiscLayout"),true);
            T("DiscLayout").GetField("HasPartitionMap").SetValue(layout,map);
            var regionsList=(IList)T("DiscLayout").GetField("Volumes").GetValue(layout);
            foreach(var r in regions){object v=Activator.CreateInstance(T("DiscVolume"),true);T("DiscVolume").GetField("Format").SetValue(v,r.format);T("DiscVolume").GetField("Offset").SetValue(v,r.offset);T("DiscVolume").GetField("Length").SetValue(v,r.length);regionsList.Add(v);}
            Type volume=T("MacVolume");var mounted=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(volume));
            for(int i=0;i<formats.Length;i++){object v=Activator.CreateInstance(volume,true);volume.GetField("mount",Flags).SetValue(v,"/fixture/volume"+i);volume.GetField("format",Flags).SetValue(v,formats[i]);volume.GetField("offset",Flags).SetValue(v,-1L);mounted.Add(v);}
            return (string)T("MacReader").GetMethod("MountedCoverage",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{layout,mounted,(object)100000L});
        }
        var two=new[]{("HFS",1000L,10000L),("HFSX",20000L,10000L)};
        Check(Evaluate(two,new[]{"hfs"},true).Contains("至少 1"),"two verified distinct data partitions and one mounted volume report a real omission");
        Check(Evaluate(two,new[]{"hfs","hfs"},true)=="","both mounted data partitions avoid a false omission");
        Check(Evaluate(new[]{("HFS",1000L,10000L)},new[]{"hfs"},true)=="","unknown native offset alone never causes partial completion");
        Check(Evaluate(new[]{("HFS",1000L,10000L)},new[]{""},true)=="","unknown mounted format remains possible coverage");
        Check(Evaluate(new[]{("ISO",0L,90000L),("UDF",0L,90000L)},new[]{"udf"})=="","overlapping ISO/UDF bridge views count once");
        Check(Evaluate(new[]{("ISO",0L,90000L),("HFS",1000L,10000L),("HFSX",20000L,10000L)},new[]{"hfs"},true).Contains("至少 1"),"whole-disc bridge cannot hide a confirmed unmounted Apple partition");
        Check(Evaluate(new[]{("HFS",1000L,20000L),("HFS",2000L,10000L)},new[]{"hfs"})=="","overlapping aliases do not manufacture a missing volume");
        Check(Evaluate(Array.Empty<(string,long,long)>(),new[]{"hfs"})=="","unknown or unreadable layout does not manufacture failure");
    }
    private static void DirectoryReports() {
        MethodInfo apply=T("MacMountedSource").GetMethod("ApplyDirectoryReport",BindingFlags.Static|BindingFlags.NonPublic);
        var names=new List<string>();
        apply.Invoke(null,new object[]{"{\"names\":[\"first.jpg\"],\"error\":\"\",\"note\":\"cleanup diagnostic only\"}",names});
        Check(names.SequenceEqual(new[]{"first.jpg"}),"directory cleanup diagnostic does not turn completed listing into failure");
        Throws(()=>apply.Invoke(null,new object[]{"{\"names\":[\"second.jpg\"],\"error\":\"actual readdir I/O error\",\"note\":\"\"}",names}),"real directory read error remains failure");
        Check(names.SequenceEqual(new[]{"first.jpg","second.jpg"}),"partial directory names survive a coexisting read error");
    }
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="setxattr",SetLastError=true)] private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)]string p,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,byte[] data,nuint size,uint pos,int options);
    public static void Main() {
        string temp=Path.Combine(Path.GetTempPath(),"discarchive-mounted-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            string a=Directory.CreateDirectory(Path.Combine(temp,"a")).FullName,b=Directory.CreateDirectory(Path.Combine(temp,"b")).FullName;
            byte[] photo=Enumerable.Range(0,900000).Select(i=>(byte)(i*17)).ToArray();
            File.WriteAllBytes(Path.Combine(a,"photo.jpg"),photo);Directory.CreateDirectory(Path.Combine(a,"folder"));File.WriteAllText(Path.Combine(a,"folder","readable.txt"),"original");File.WriteAllText(Path.Combine(b,"other.txt"),"second");
            int guards=0;string virtualRoot=Path.Combine(temp,"virtual");object one=Source(virtualRoot,new[]{a},()=>guards++);
            string photoPath=Named(one,virtualRoot,"photo.jpg");
            Check(SHA256.HashData(Read(one,photoPath)).SequenceEqual(SHA256.HashData(photo)),"photo bytes preserved; ordinary no-xattr file stays readable");
            Check(Entries(one,virtualRoot).Any(p=>Path.GetFileName(p)=="folder"),"single volume preserves original root hierarchy");
            int before=guards;using(var s=(Stream)Call(one,"OpenRead",photoPath)){int afterOpen=guards;s.Read(new byte[13],0,13);Check(guards>=afterOpen+2,"each source read verifies identity before and after");}Check(before<guards,"opening verifies identity");
            bool changed=false;object guarded=Source(Path.Combine(temp,"guarded"),new[]{a},()=>{if(changed)throw new IOException("changed");});string guardedPhoto=Entries(guarded,Path.Combine(temp,"guarded")).Single(p=>Path.GetFileName(p)=="photo.jpg");using(var s=(Stream)Call(guarded,"OpenRead",guardedPhoto)){changed=true;Throws(()=>s.Read(new byte[1],0,1),"changed disc refuses further reads");}
            object multi=Source(Path.Combine(temp,"multi"),new[]{a,b},()=>{});string[] roots=Entries(multi,Path.Combine(temp,"multi"));Check(roots.Length==2&&roots.Distinct(StringComparer.OrdinalIgnoreCase).Count()==2,"same-label volumes keep two stable distinct roots");Check(Entries(multi,roots[0]).Length>0&&Entries(multi,roots[1]).Length>0,"both volume trees remain accessible");
            // A stale/deleted file fails individually after enumeration.
            object stale=Source(Path.Combine(temp,"stale"),new[]{a},()=>{});string stalePhoto=Entries(stale,Path.Combine(temp,"stale")).Single(p=>Path.GetFileName(p)=="photo.jpg");File.Delete(Path.Combine(a,"photo.jpg"));Throws(()=>Read(stale,stalePhoto),"bad individual file reports failure");string folder=Entries(stale,Path.Combine(temp,"stale")).Single(p=>Path.GetFileName(p)=="folder");Check(Read(stale,Entries(stale,folder).Single()).Length==8,"bad file does not prevent readable sibling");
            PosixNames(temp);
            Coverage();
            DirectoryReports();
            if(OperatingSystem.IsMacOS()) {
                string file=Path.Combine(a,"forked");File.WriteAllText(file,"data");byte[] fork=Enumerable.Range(0,600000).Select(i=>(byte)(i*11)).ToArray(),finder=new byte[32];finder[0]=65;
                Check(SetXattr(file,"com.apple.ResourceFork",fork,(nuint)fork.Length,0,0)==0,"created synthetic resource fork");Check(SetXattr(file,"com.apple.FinderInfo",finder,32,0,0)==0,"created synthetic FinderInfo");
                File.CreateSymbolicLink(Path.Combine(a,"outside-link"),Path.Combine(b,"other.txt"));File.WriteAllText(Path.Combine(a,"back\\slash"),"literal");
                object native=Source(Path.Combine(temp,"native"),new[]{a},()=>{});string[] nativeRoot=Entries(native,Path.Combine(temp,"native"));string link=nativeRoot.Single(p=>Path.GetFileName(p)=="outside-link");Throws(()=>Call(native,"Attributes",link),"symlink refuses traversal and read");
                Check(nativeRoot.Any(p=>Path.GetFileName(p).Contains("back~005Cslash")),"literal POSIX backslash is mapped without inventing hierarchy");
                string extra=nativeRoot.Single(p=>Path.GetFileName(p).Contains("macOS-metadata"));string[] sides=Entries(native,extra);Check(Read(native,sides.Single(p=>p.EndsWith("resource.rsrc"))).SequenceEqual(fork),"resource fork streams intact across chunk boundaries");Check(Read(native,sides.Single(p=>p.EndsWith("FinderInfo.bin"))).SequenceEqual(finder),"FinderInfo bytes preserved separately");
            }else Console.WriteLine("SKIP Darwin fd/xattr/symlink branch: requires macOS; Windows projection tests only.");
            Console.WriteLine(passed+" mounted-source checks passed.");
        }finally{Directory.Delete(temp,true);}
    }
}
