using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscArchive;

// Compile with all src/*.cs, /main:OpticalMountTests, and the normal desktop references.
// Run in a fresh temporary directory with native/{x64|x86}/7z.dll beside this executable.
// All sectors, pictures, catalogs and output files below are generated fixtures.
internal static class OpticalMountTests
{
    private static string work,photo;
    private static int checks;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    private static void BE(byte[] b,int p,ulong v,int n){for(int i=n-1;i>=0;i--){b[p+i]=(byte)v;v>>=8;}}
    private static void LE(byte[] b,int p,uint v,int n){for(int i=0;i<n;i++){b[p+i]=(byte)v;v>>=8;}}
    private static void Both(byte[] b,int p,uint v,int n){LE(b,p,v,n);BE(b,p+n,v,n);}
    private static int IsoRecord(byte[] b,int p,uint block,uint size,byte flags,byte[] name)
    {
        int n=33+name.Length+(name.Length%2==0?1:0);b[p]=(byte)n;Both(b,p+2,block,4);Both(b,p+10,size,4);b[p+18]=126;b[p+19]=11;b[p+20]=20;b[p+25]=flags;Both(b,p+28,1,2);b[p+32]=(byte)name.Length;name.CopyTo(b,p+33);return n;
    }
    private static byte[] Iso()
    {
        byte[] b=new byte[24*2048];int p=16*2048;b[p]=1;Encoding.ASCII.GetBytes("CD001").CopyTo(b,p+1);b[p+6]=1;Encoding.ASCII.GetBytes("120304-ISO-SYNTHETIC".PadRight(32)).CopyTo(b,p+40);Both(b,p+80,24,4);Both(b,p+120,1,2);Both(b,p+124,1,2);Both(b,p+128,2048,2);IsoRecord(b,p+156,20,2048,2,new byte[]{0});
        p=17*2048;b[p]=255;Encoding.ASCII.GetBytes("CD001").CopyTo(b,p+1);b[p+6]=1;p=20*2048;p+=IsoRecord(b,p,20,2048,2,new byte[]{0});p+=IsoRecord(b,p,20,2048,2,new byte[]{1});IsoRecord(b,p,21,5,0,Encoding.ASCII.GetBytes("HELLO.TXT;1"));Encoding.ASCII.GetBytes("hello").CopyTo(b,21*2048);return b;
    }
    private static void Fork(byte[] b,int p,ulong size,uint start,uint count){BE(b,p,size,8);BE(b,p+12,count,4);BE(b,p+16,start,4);BE(b,p+20,count,4);}
    private static int HfsRecord(byte[] b,int p,uint parent,uint id,string name,bool directory,uint block,bool resource)
    {
        int key=8+name.Length*2;BE(b,p,(ulong)(key-2),2);BE(b,p+2,parent,4);BE(b,p+6,(ulong)name.Length,2);Encoding.BigEndianUnicode.GetBytes(name).CopyTo(b,p+8);int r=p+key;BE(b,r,directory?1u:2u,2);BE(b,r+8,id,4);BE(b,r+42,directory?0x41EDu:0x81A4u,2);
        if(!directory){Fork(b,r+88,5,block,1);if(resource)Fork(b,r+168,4,block+1,1);}return key+(directory?88:248);
    }
    private static byte[] Hfs()
    {
        byte[] b=new byte[32*512];int h=1024;BE(b,h,0x482B,2);BE(b,h+2,4,2);BE(b,h+4,0x100,4);BE(b,h+32,1,4);BE(b,h+40,512,4);BE(b,h+44,32,4);Fork(b,h+272,8192,4,16);
        int tree=2048,leaf=tree+4096;b[tree+8]=1;BE(b,tree+10,3,2);BE(b,tree+20,2,4);BE(b,tree+24,1,4);BE(b,tree+28,1,4);BE(b,tree+32,4096,2);BE(b,tree+36,2,4);b[leaf+8]=255;b[leaf+9]=1;BE(b,leaf+10,2,2);
        int p=leaf+14;BE(b,leaf+4094,14,2);p+=HfsRecord(b,p,1,2,"120304-合成拍摄",true,0,false);BE(b,leaf+4092,(ulong)(p-leaf),2);p+=HfsRecord(b,p,2,16,"café-文.txt",false,22,true);BE(b,leaf+4090,(ulong)(p-leaf),2);Encoding.ASCII.GetBytes("hello").CopyTo(b,22*512);Encoding.ASCII.GetBytes("RSRC").CopyTo(b,23*512);return b;
    }
    private static byte[] Classic()
    {
        byte[] b=new byte[32*512];int h=1024;BE(b,h,0x4244,2);BE(b,h+18,16,2);BE(b,h+20,512,4);BE(b,h+28,4,2);byte[] name=Encoding.ASCII.GetBytes("UNREAD-CLASSIC");b[h+36]=(byte)name.Length;name.CopyTo(b,h+37);return b;
    }
    private static void Apm(byte[] b,int index,int count,int start,int size,string type)
    {int p=index*512;BE(b,p,0x504D,2);BE(b,p+4,(ulong)count,4);BE(b,p+8,(ulong)start,4);BE(b,p+12,(ulong)size,4);Encoding.ASCII.GetBytes("Synthetic partition "+index).CopyTo(b,p+16);Encoding.ASCII.GetBytes(type).CopyTo(b,p+48);}
    private static byte[] Multiple(bool includeIso,bool unreadable)
    {
        byte[] b=new byte[256*512];if(includeIso)Iso().CopyTo(b,0);BE(b,0,0x4552,2);BE(b,2,512,2);Apm(b,1,2,128,32,"Apple_HFS");Apm(b,2,2,192,32,"Apple_HFS");Hfs().CopyTo(b,128*512);(unreadable?Classic():Hfs()).CopyTo(b,192*512);return b;
    }
    private sealed class TrackedStream : MemoryStream
    {
        internal int Closes,Reads;internal bool ReadAfterLease;
        internal TrackedStream(byte[] bytes):base(bytes,false){}
        public override int Read(byte[] b,int o,int n){if(Closes>0)ReadAfterLease=true;Reads++;return base.Read(b,o,n);}
        protected override void Dispose(bool disposing){Closes++;base.Dispose(disposing);}
    }
    private sealed class Lease : IDisposable
    {
        internal readonly TrackedStream Raw;internal int Closes;
        internal Lease(TrackedStream raw){Raw=raw;}
        internal void Verify(){if(Closes!=0 || !Raw.CanRead)throw new IOException("Synthetic lease ended");}
        public void Dispose(){Closes++;Raw.Dispose();}
    }
    private sealed class SyntheticReader : IReader
    {
        internal readonly byte[] Bytes;internal readonly OpticalView View;internal readonly string Root;internal readonly List<Lease> Leases=new List<Lease>();
        internal SyntheticReader(byte[] bytes,OpticalView view){Bytes=bytes;View=view;Root=Path.Combine(work,"absent-source-"+Guid.NewGuid().ToString("N"));}
        public string[] Drives(){return new[]{@"S:\"};}public bool Empty(string drive){return false;}public bool Ready(string drive){return true;}
        public MountedDisc Open(string drive){TrackedStream raw=new TrackedStream(Bytes);Lease lease=new Lease(raw);Leases.Add(lease);return OpticalMount.Open(Root,raw,lease.Verify,lease,View);}
        internal void CheckClosed(){Check(Leases.Count>0 && Leases.All(x=>x.Closes==1 && x.Raw.Closes==1 && !x.Raw.CanRead && !x.Raw.ReadAfterLease),"all selected readers release their raw source lease exactly once");}
    }
    private static string Dir(string name){return Directory.CreateDirectory(Path.Combine(work,name)).FullName;}
    private static Workstation Station(SyntheticReader reader,string name){return new Workstation(new Catalog(Dir(name+"-catalog")),reader);}
    private static Entry Await(Task<Entry> task){if(!task.Wait(30000))throw new TimeoutException("Synthetic optical workflow exceeded 30 seconds.");return task.GetAwaiter().GetResult();}
    private static Entry Import(Workstation station,string target){station.Register(photo,target,"",@"S:\","",true);return Await(station.Copy(delegate{}));}
    private static string[] Payload(string target,string file){return Directory.GetFiles(target,file,SearchOption.AllDirectories);}
    private static void Workflow(bool hfs)
    {
        string name=hfs?"hfs":"iso",target=Dir(name+"-target");SyntheticReader reader=new SyntheticReader(hfs?Hfs():Iso(),OpticalView.Auto);Workstation station=Station(reader,name);Entry first=Import(station,target);
        Check(first.Status==Workstation.Done && first.Failed==0 && first.Files==(hfs?2:1),name+" OpticalMount to automatic Workstation classification and copy: "+first.Status+" / "+first.Note);
        Check(first.Project.StartsWith("120304-") && first.NamingReason.Contains("明确文字"),name+" archive naming comes from real generated volume metadata");
        string[] main=Payload(target,hfs?"café-文.txt":"HELLO.TXT");Check(main.Length==1 && File.ReadAllText(main[0])=="hello",name+" ordinary file bytes survive the full pipeline");
        if(hfs){string[] fork=Payload(target,"item-*-rsrc");Check(fork.Length==1 && File.ReadAllText(fork[0])=="RSRC","known HFS resource fork is preserved as an associated sidecar");}
        Check(Directory.GetFiles(first.JobDir,"文件系统路径映射-*.tsv",SearchOption.AllDirectories).Length>0 && Directory.GetFiles(first.JobDir,"光盘布局.txt",SearchOption.AllDirectories).Length>0,name+" mount and path descriptions are written by Workstation");reader.CheckClosed();
        Dictionary<string,DateTime> original=main.ToDictionary(x=>x,File.GetLastWriteTimeUtc);station.Retry(first,@"S:\");Entry resumed=Await(station.Copy(delegate{}));
        Check(resumed.Status==Workstation.Done && resumed.ReusedFiles==first.Files && resumed.Files==first.Files,name+" resume verifies and reuses every original file");
        Check(original.All(x=>File.Exists(x.Key) && File.GetLastWriteTimeUtc(x.Key)==x.Value),name+" resume keeps original publications and paths");reader.CheckClosed();
    }
    private static void Lifecycle()
    {
        SyntheticReader reader=new SyntheticReader(Iso(),OpticalView.Auto);MountedDisc mount=reader.Open(@"S:\");List<string> entries=new List<string>(mount.Source.Entries(mount.Root));Stream item=mount.Source.OpenRead(entries[0]);
        Check(reader.Leases[0].Raw.CanRead && item.ReadByte()=='h',"reader and item stream stay usable while the selected raw lease is held");mount.Dispose();bool closed=false;try{item.ReadByte();}catch(ObjectDisposedException){closed=true;}
        Check(closed,"disposing a mount invalidates an outstanding reader item stream");item.Dispose();reader.CheckClosed();
        TrackedStream invalid=new TrackedStream(new byte[8192]);Lease failure=new Lease(invalid);bool rejected=false;try{OpticalMount.Open(Path.Combine(work,"absent-invalid"),invalid,failure.Verify,failure,OpticalView.Auto);}catch(IOException){rejected=true;}
        Check(rejected && failure.Closes==1 && !invalid.CanRead,"mount-open failure also releases the owned lease");
    }
    private static void NativePlaceholder()
    {
        byte[] image=Hfs();int record=6144+14+(8+"120304-合成拍摄".Length*2+88)+8+"café-文.txt".Length*2;
        BE(image,record+44,1234,4);BE(image,record+48,0x686C6E6B,4);BE(image,record+52,0x6866732B,4);Array.Clear(image,record+88,80);
        string target=Dir("native-placeholder-target");SyntheticReader reader=new SyntheticReader(image,OpticalView.Mac);Entry entry=Import(Station(reader,"native-placeholder"),target);
        Check(entry.Status==Workstation.Review && entry.Files==0 && entry.Failed>=1,"native HFS hardlink placeholder never becomes a successful zero-byte publication");
        Check(Payload(target,"café-文.txt").Length==0 && Payload(target,"item-*-rsrc").Length==0,"native placeholder and its resource fork remain unconfirmed rather than copied as normal payloads");
        string failure=File.ReadAllText(Path.Combine(entry.Report,"失败清单.tsv"));Check(failure.Contains("CNID=16") && failure.Contains("硬链接占位"),"native catalog guard reaches real Workstation failure receipts");reader.CheckClosed();
    }
    private static void ViewsAndPartitions()
    {
        byte[] unclean=Hfs(); BE(unclean,1028,0x2000,4);
        SyntheticReader needsReview=new SyntheticReader(unclean,OpticalView.Mac);
        Entry recovered=Import(Station(needsReview,"unclean"),Dir("unclean-target"));
        Check(recovered.Status==Workstation.Partial && recovered.Files==2 && recovered.Failed>0,
            "unclean journaled volume keeps readable bytes and an explicit incomplete result without replay");
        Check(File.ReadAllText(Path.Combine(recovered.Report,"失败清单.tsv")).Contains("不重放日志"),"volume consistency is recorded");
        needsReview.CheckClosed();
        byte[] image=Multiple(true,false);DiscLayout layout;using(MemoryStream raw=new MemoryStream(image,false))layout=OpticalLayout.Inspect(raw);
        Check(layout.HasIso && layout.Volumes.Count(x=>x.Format=="HFS")==2 && OpticalMount.Describe(layout).Contains("多个视图"),"hybrid generated image identifies ISO plus two independent Mac partitions");
        string target=Dir("multiple-target");SyntheticReader mac=new SyntheticReader(image,OpticalView.Mac);Workstation multiStation=Station(mac,"multiple");int projectChoices=0;
        multiStation.ChooseProject=delegate(Entry entry,string proposed,string[] suggestions,CancellationToken token){projectChoices++;return Task.FromResult(proposed);};
        Entry copied=Import(multiStation,target);
        Check(copied.Status==Workstation.Done && copied.Files==4 && copied.Failed==0,"two selected Mac volumes copy their ordinary files and resource forks independently: "+copied.Status+" / "+copied.Note);Check(projectChoices>0,"similar generated partition project names receive an explicit simulated keep-separate choice");Check(Payload(target,"café-文.txt").Length==2 && Payload(target,"HELLO.TXT").Length==0,"Mac view retains both partitions and does not silently merge in ISO files");mac.CheckClosed();
        string isoTarget=Dir("hybrid-iso-target");SyntheticReader iso=new SyntheticReader(image,OpticalView.Iso);Entry onlyIso=Import(Station(iso,"hybrid-iso"),isoTarget);
        Check(onlyIso.Status==Workstation.Done && onlyIso.Files==1 && Payload(isoTarget,"HELLO.TXT").Length==1 && Payload(isoTarget,"café-文.txt").Length==0,"explicit ISO view reads ISO rather than selected Mac partition data");
        string report=File.ReadAllText(Directory.GetFiles(onlyIso.JobDir,"光盘布局.txt",SearchOption.AllDirectories)[0]);Check(report.Contains("多个视图") && report.Contains("本次读取 ISO"),"hybrid report identifies unselected alternative views and the view actually copied");iso.CheckClosed();
        string partialTarget=Dir("partial-target");SyntheticReader partial=new SyntheticReader(Multiple(false,true),OpticalView.Mac);Entry incomplete=Import(Station(partial,"partial"),partialTarget);
        Check(incomplete.Status==Workstation.Partial && incomplete.Files==2 && incomplete.Failed>0,"unreadable classic-HFS partition cannot produce a fully successful multi-volume result");
        string failureText=File.ReadAllText(Path.Combine(incomplete.Report,"失败清单.tsv"));Check(failureText.Contains("传统 HFS") && failureText.Contains("未提取"),"unread partition has an explicit failure receipt while readable HFS files survive");partial.CheckClosed();
    }
    [STAThread]
    public static int Main()
    {
        work=DirRoot();
        try
        {
            string dll=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native",IntPtr.Size==8?"x64":"x86","7z.dll");if(!File.Exists(dll))throw new FileNotFoundException("Place the official 7z.dll under native/<architecture> beside this temporary test executable.",dll);
            photo=Path.Combine(work,"generated-disc.jpg");using(Bitmap bitmap=new Bitmap(32,24)){using(Graphics g=Graphics.FromImage(bitmap))g.Clear(Color.Azure);bitmap.Save(photo,ImageFormat.Jpeg);}
            using(MemoryStream raw=new MemoryStream(Hfs(),false)){HfsGuardResult guard=HfsCatalogGuard.Inspect(raw);Check(guard.Audited && guard.Complete && guard.Links==0,"generated HFS fixture has a fully audited clean catalog");}
            Lifecycle();Workflow(false);Workflow(true);NativePlaceholder();ViewsAndPartitions();Console.WriteLine("PASS "+checks+" synthetic OpticalMount workflow checks; fixtures: "+work);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Fixtures: "+work);return 1;}
    }
    private static string DirRoot(){string root=Path.Combine(Path.GetTempPath(),"OpticalMountTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
}
