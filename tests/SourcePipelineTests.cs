using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DiscArchive;

internal static class SourcePipelineTests
{
    private sealed class VirtualSource : DiscSource
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AbsentVirtualDisc-"+Guid.NewGuid().ToString("N"));
        internal readonly Dictionary<string,byte[]> Data = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal string BadRead, BadEnumeration, BadMetadata;
        internal int Opens;
        internal VirtualSource(string root=null) { if(root!=null) Root=root; Dirs.Add(Root); }
        private static string Key(string p) { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); }
        internal string Add(string relative, byte[] data)
        {
            string p=Path.Combine(Root,relative); Data[p]=data;
            for(string d=Path.GetDirectoryName(p); d!=null && d.StartsWith(Root,StringComparison.OrdinalIgnoreCase); d=Path.GetDirectoryName(d)) Dirs.Add(d);
            return p;
        }
        internal override IEnumerable<string> Entries(string directory)
        {
            string d=Key(directory);
            foreach(string p in Dirs) if(String.Equals(Path.GetDirectoryName(p),d,StringComparison.OrdinalIgnoreCase)) yield return p;
            foreach(string p in Data.Keys) if(String.Equals(Path.GetDirectoryName(p),d,StringComparison.OrdinalIgnoreCase)) yield return p;
            if(d==BadEnumeration) throw new IOException("synthetic enumeration interruption");
        }
        internal override FileAttributes Attributes(string path)
        {
            string p=Key(path); if(p==BadMetadata) throw new IOException("synthetic metadata error");
            if(Dirs.Contains(p)) return FileAttributes.Directory;
            if(Data.ContainsKey(p)) return FileAttributes.Normal;
            throw new FileNotFoundException("virtual source path",p);
        }
        internal override long Length(string path) { return Data[Key(path)].Length; }
        internal override DateTime Modified(string path) { return new DateTime(2009,5,6); }
        internal override Stream OpenRead(string path) { Interlocked.Increment(ref Opens); string p=Key(path); return p==BadRead ? new FailingStream(Data[p]) : new MemoryStream(Data[p],false); }
    }
    private sealed class FailingStream : MemoryStream
    {
        internal FailingStream(byte[] data) : base(data,false) { }
        public override int Read(byte[] b,int o,int n) { if(Position>=3) throw new IOException("synthetic read failure"); return base.Read(b,o,Math.Min(n,3)); }
    }
    private static string work;
    private static void Assert(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static RescueResult Copy(VirtualSource media,string target,string attempt,string journal=null,bool verify=false)
    { return RescueCopy.Run(media.Root,target,"Project",null,"SYNTHETIC",attempt,null,null,CancellationToken.None,journal,verify,media); }
    private static string Target(string name) { string path=Path.Combine(work,name); Directory.CreateDirectory(path); return path; }
    private static byte[] Tiff()
    {
        byte[] b=new byte[80]; b[0]=b[1]=(byte)'I';b[2]=42;b[4]=8;b[8]=1;
        b[10]=0x69;b[11]=0x87;b[12]=4;b[14]=1;b[18]=26;
        b[26]=1;b[28]=3;b[29]=0x90;b[30]=2;b[32]=20;b[36]=44;
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("2012:03:04 05:06:07\0"),0,b,44,20); return b;
    }
    public static int Main()
    {
        work=TargetRoot();
        try
        {
            VirtualSource media=new VirtualSource();media.Add("good.txt",Encoding.UTF8.GetBytes("good"));media.BadRead=media.Add("broken.bin",new byte[8]);media.BadEnumeration=Path.Combine(media.Root,"folder");media.Add(Path.Combine("folder","readable.txt"),new byte[]{1,2,3});media.BadMetadata=media.Add("missing-meta.bin",new byte[4]);
            Assert(!Directory.Exists(media.Root),"virtual source unexpectedly exists");
            RescueResult result=Copy(media,Target("failure-target"),Path.Combine(work,"attempt1"));
            Assert(result.Files==2 && result.Failed==3,"partial failures must preserve readable siblings: "+result.Files+"/"+result.Failed);
            Assert(Directory.GetFiles(work,"*.partial",SearchOption.AllDirectories).Length==1,"bad read fragment missing");
            DiscIdentity damaged=DiscIdentity.Inspect(media.Root,null,CancellationToken.None,media);Assert(!damaged.Complete,"failed identity should be incomplete");
            Console.WriteLine("PASS virtual copy handles enumeration, metadata and mid-stream failures");
            VirtualSource photo=new VirtualSource();photo.Add("capture.iiq",Tiff());
            DiscPlan plan=DiscPlan.Decide(photo.Root,"","","","VIRTUAL",DateTime.Today,CancellationToken.None,photo);
            Assert(plan.Folder.StartsWith("120304-"),"capture metadata date via virtual stream: "+plan.Folder);
            DiscIdentity first=DiscIdentity.Inspect(photo.Root,null,CancellationToken.None,photo),second=DiscIdentity.Inspect(photo.Root,null,CancellationToken.None,photo);
            Assert(first.Complete && first.Key==second.Key,"virtual identity deterministic");
            Console.WriteLine("PASS virtual bounded camera metadata, planning and identity");
            VirtualSource continued=new VirtualSource();string entry=continued.Add("same.bin",new byte[]{1,2,3,4});string target=Target("continuation-target"),journal=Path.Combine(work,"journal");
            Assert(Copy(continued,target,Path.Combine(work,"attempt2"),journal).Files==1,"first continuation");int opens=continued.Opens;
            Assert(Copy(continued,target,Path.Combine(work,"attempt3"),journal,true).ReusedFiles==1 && continued.Opens>opens,"source verification must use supplied stream");
            continued.Data[entry]=new byte[]{9,8,7,6};RescueResult changed=Copy(continued,target,Path.Combine(work,"attempt4"),journal,true);
            Assert(changed.Files==1 && changed.ReusedFiles==0,"changed source must recopy");
            Console.WriteLine("PASS continuation source rehash uses the same virtual source");
            string concurrent=Target("concurrent");VirtualSource a=new VirtualSource();VirtualSource c=new VirtualSource(a.Root);a.Add("same.bin",new byte[]{11});c.Add("same.bin",new byte[]{22});Exception errorA=null,errorC=null;RescueResult ra=null,rc=null;
            Thread ta=new Thread(delegate(){try{ra=Copy(a,concurrent,Path.Combine(work,"thread-a"));}catch(Exception ex){errorA=ex;}});
            Thread tc=new Thread(delegate(){try{rc=Copy(c,concurrent,Path.Combine(work,"thread-c"));}catch(Exception ex){errorC=ex;}});
            ta.Start();tc.Start();ta.Join();tc.Join();if(errorA!=null)throw errorA;if(errorC!=null)throw errorC;
            Assert(ra.Files==1 && rc.Files==1,"parallel workers failed");string[] outputs=Directory.GetFiles(Path.Combine(concurrent,"Project"),"same.bin",SearchOption.AllDirectories);
            Assert(outputs.Length==2 && File.ReadAllBytes(outputs[0])[0]!=File.ReadAllBytes(outputs[1])[0],"parallel sources collided");
            Console.WriteLine("PASS independent virtual sessions share safe destination allocation");
            Console.WriteLine("Generated fixtures: "+work);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);for(Exception detail=ex;detail!=null;detail=detail.InnerException)Console.Error.WriteLine("HResult "+detail.GetType().Name+": 0x"+detail.HResult.ToString("X8"));Console.Error.WriteLine("Generated fixtures: "+work);return 1;}
    }
    private static string TargetRoot() { string root=Path.Combine(Path.GetTempPath(),"SourcePipelineTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root; }
}
