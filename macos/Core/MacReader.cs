using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive;

internal sealed class MacVolume {
    public string id="",mount="",label="",format="",raw="";
    public long bytes,offset;
    public int sector;
}
internal sealed class MacDrive {
    public string id="",name="",token="",mount="",label="",format="",raw="",state="";
    public bool empty,ready;
    public long bytes;
    public int sector;
    public MacVolume[] partitions=Array.Empty<MacVolume>();
}
internal sealed class MacTarget { public string name="",path="",id=""; }
internal sealed class MacInventory {
    public MacDrive[] drives=Array.Empty<MacDrive>(); public MacTarget[] targets=Array.Empty<MacTarget>();
}
internal sealed class MacReader : IReader
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InventoryFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VerifyFn([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string token);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TracksFn([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string token);
    private readonly InventoryFn inventory;
    private readonly FreeFn free;
    private readonly VerifyFn verify;
    private readonly TracksFn tracks;
    internal OpticalView View=OpticalView.Auto;
    internal MacReader()
    {
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("需要在 Mac 上连接光驱。");
        string lib=Environment.GetEnvironmentVariable("DISCARC_DEVICE_LIB");
        if(string.IsNullOrEmpty(lib)||!Path.IsPathFullyQualified(lib))throw new IOException("应用包缺少设备读取组件。");
        IntPtr h=NativeLibrary.Load(lib);
        inventory=Marshal.GetDelegateForFunctionPointer<InventoryFn>(NativeLibrary.GetExport(h,"da_devices"));
        free=Marshal.GetDelegateForFunctionPointer<FreeFn>(NativeLibrary.GetExport(h,"da_free"));
        verify=Marshal.GetDelegateForFunctionPointer<VerifyFn>(NativeLibrary.GetExport(h,"da_verify"));
        tracks=Marshal.GetDelegateForFunctionPointer<TracksFn>(NativeLibrary.GetExport(h,"da_tracks"));
    }
    private string Take(IntPtr p) {if(p==IntPtr.Zero)throw new IOException("macOS 无法枚举设备。");try{return Marshal.PtrToStringUTF8(p);}finally{free(p);}}
    internal MacInventory Snapshot()=>JsonSerializer.Deserialize<MacInventory>(Take(inventory()),Host.Json)??throw new IOException("设备清单无效。");
    public string[] Drives()=>Snapshot().drives.Select(d=>d.id).ToArray();
    private MacDrive Find(string id)=>Snapshot().drives.FirstOrDefault(d=>d.id==id);
    public bool Empty(string id)=>Find(id)?.empty==true;
    public bool Ready(string id)=>Find(id)?.ready==true;
    internal static OpticalView ParseView(string view)=>view switch {"mac"=>OpticalView.Mac,"udf"=>OpticalView.Udf,"iso"=>OpticalView.Iso,_=>OpticalView.Auto};
    private static string VirtualRoot(string id)=>"/DiscArchiveOpticalView/"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).Substring(0,20)+"/";
    internal string Inspect(string id){
        MacDrive d=Find(id)??throw new IOException("所选光驱已断开。");
        if(!d.ready)return d.empty?"光驱已连接，当前没有光盘。":"光驱已连接，介质尚未就绪。";
        Action guard=Guard(d);
        string mounted=string.Join("\n",d.partitions.Where(p=>p.mount.Length>0).Select(p=>p.format+"　"+p.label+"　"+p.mount));
        try {using var raw=new DarwinOpticalStream(d,guard); string layout=OpticalMount.Describe(OpticalLayout.Inspect(raw));guard();return mounted+"\n"+layout+TrackNote(d,false);}
        catch(IOException ex){return mounted+"\n原始扇区检测不可用："+ex.Message+TrackNote(d,false);}
    }
    private Action Guard(MacDrive d)=>()=>{if(verify(d.id,d.token)!=1)throw new IOException("光盘已更换或光驱已断开。");};
    private string TrackNote(MacDrive d,bool warningsOnly){
        using var info=JsonDocument.Parse(Take(tracks(d.id,d.token)));
        if(warningsOnly && (!info.RootElement.TryGetProperty("uncovered",out var b)||b.ValueKind!=JsonValueKind.True))return "";
        return info.RootElement.TryGetProperty("note",out var n)?n.GetString()??"":"";
    }
    private sealed class CoverageRange {
        internal long Start,End;
        internal readonly HashSet<string> Families=new(StringComparer.Ordinal);
    }
    private static string CoverageFamily(string format) {
        string f=(format??"").Trim().ToLowerInvariant();
        if(f.Length==0)return "";
        if(f.Contains("hfs"))return "HFS";
        if(f.Contains("apfs"))return "APFS";
        if(f.Contains("udf"))return "UDF";
        if(f=="iso"||f.Contains("9660")||f.Contains("joliet"))return "ISO";
        return "other:"+f;
    }
    // This proves only a lower bound on identified omissions. Unknown registry
    // offsets never become zero; unknown mounted formats can cover any one range.
    // Overlapping filesystem views are one possible coverage unit, not extra
    // partitions. Whole-disc ISO/UDF bridge views must not hide real Apple-map
    // partitions, which the existing parser has independently validated.
    internal static string MountedCoverage(DiscLayout layout,IList<MacVolume> mounted,long sourceSize) {
        if(layout==null||sourceSize<=0)return "";
        var candidates=layout.Volumes.Where(v=>v.Offset>=0&&v.Length>0&&v.Offset<=sourceSize&&v.Length<=sourceSize-v.Offset).ToList();
        bool appleMap=layout.HasPartitionMap&&candidates.Any(v=>v.Offset>0&&(CoverageFamily(v.Format)=="HFS"||CoverageFamily(v.Format)=="APFS"));
        if(appleMap)candidates=candidates.Where(v=>CoverageFamily(v.Format)=="HFS"||CoverageFamily(v.Format)=="APFS").ToList();
        var ranges=new List<CoverageRange>();
        foreach(var v in candidates.OrderBy(v=>v.Offset).ThenBy(v=>v.Length)) {
            CoverageRange range=ranges.LastOrDefault();
            if(range==null||v.Offset>=range.End){range=new CoverageRange{Start=v.Offset,End=v.Offset+v.Length};ranges.Add(range);}
            else range.End=Math.Max(range.End,v.Offset+v.Length);
            range.Families.Add(CoverageFamily(v.Format));
        }
        if(ranges.Count==0)return "";
        var mounts=mounted.Where(m=>!string.IsNullOrEmpty(m.mount)).GroupBy(m=>m.mount,StringComparer.Ordinal).Select(g=>g.First()).ToList();
        int[] owner=Enumerable.Repeat(-1,ranges.Count).ToArray();
        bool Assign(int index,bool[] seen) {
            string family=CoverageFamily(mounts[index].format);
            for(int i=0;i<ranges.Count;i++) {
                if(seen[i]||(family.Length>0&&!ranges[i].Families.Contains(family)))continue;
                seen[i]=true;
                if(owner[i]<0||Assign(owner[i],seen)){owner[i]=index;return true;}
            }
            return false;
        }
        int covered=0;
        for(int i=0;i<mounts.Count;i++)if(Assign(i,new bool[ranges.Count]))covered++;
        int missing=ranges.Count-covered;
        if(missing<=0)return "";
        string evidence=string.Join("；",ranges.Take(16).Select(r=>r.Start+".."+r.End+" 字节（"+string.Join("/",r.Families.OrderBy(f=>f,StringComparer.Ordinal))+"）"));
        return "同一光盘已确认 "+ranges.Count+" 个互不重叠的数据范围，本次已挂载视图最多对应其中 "+covered+" 个；至少 "+missing+" 个已识别数据范围尚未提取。布局证据："+evidence+"。未知范围未据此判失败。";
    }
    public MountedDisc Open(string id){
        var d=Find(id)??throw new IOException("光驱已断开。");
        if(!d.ready||string.IsNullOrEmpty(d.token))throw new IOException("光盘尚未就绪。");
        Action guard=Guard(d);guard();
        string root=VirtualRoot(d.id);
        var volumes=d.partitions.Where(p=>!string.IsNullOrEmpty(p.mount)&&Path.IsPathFullyQualified(p.mount))
            .GroupBy(p=>p.mount,StringComparer.Ordinal).Select(g=>g.First()).OrderBy(p=>p.offset).ThenBy(p=>p.mount,StringComparer.Ordinal).ToList();
        if(volumes.Count==0 && !string.IsNullOrEmpty(d.mount))volumes.Add(new MacVolume{mount=d.mount,label=d.label,format=d.format});
        MountedDisc opened=null;
        try {
            if(View==OpticalView.Auto && volumes.Count>0) {
                try {
                    var tree=new MacMountedSource(root,volumes,guard);
                    opened=new MountedDisc(root,volumes.Count==1?volumes[0].label:"",guard,null,tree,string.Join(" + ",volumes.Select(p=>p.format).Distinct())+"（macOS 挂载视图）");
                    // Same physical provider/token as the mounted view. Failure
                    // to access raw or identify a layout is unknown, not proof
                    // that another partition was omitted.
                    try {
                        using var raw=new DarwinOpticalStream(d,guard);
                        DiscLayout layout=OpticalLayout.Inspect(raw);guard();
                        string omission=MountedCoverage(layout,volumes,raw.Length);
                        if(omission.Length>0)opened.UncoveredTracks=omission;
                    }catch(IOException){guard();}
                     catch(UnauthorizedAccessException){guard();}
                }catch(UnauthorizedAccessException){guard();}
                 catch(IOException){guard();}
            }
            if(opened==null) {
                var raw=new DarwinOpticalStream(d,guard);
                opened=OpticalMount.Open(root,raw,guard,raw,View);
            }
            string note=TrackNote(d,true);if(note.Length>0) opened.UncoveredTracks=((opened.UncoveredTracks??"")+" "+note).Trim();
            guard();return opened;
        }catch{opened?.Dispose();throw;}
    }
}

internal sealed class DarwinOpticalStream : Stream
{
    [DllImport("/usr/lib/libSystem.B.dylib",SetLastError=true,EntryPoint="open")] static extern int OpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)]string p,int flags);
    [DllImport("/usr/lib/libSystem.B.dylib",SetLastError=true,EntryPoint="pread")] static extern nint Pread(int fd,byte[] data,nuint count,long offset);
    private readonly SafeFileHandle fd;
    private readonly Action verify;
    private readonly int block;
    private readonly long size;
    private long position;
    internal DarwinOpticalStream(MacDrive d,Action verify) {
        this.verify=verify;block=d.sector;size=d.bytes;verify();
        if(!Regex.IsMatch(d.raw??"",@"^/dev/r?disk[0-9]+(?:s[0-9]+)*$")||block<512||block>65536||(block&(block-1))!=0||size<=0||size%block!=0)
            throw new IOException("光驱没有提供可信的数据扇区边界。");
        int h=OpenDevice(d.raw,0x1000000|0x100); // Darwin O_CLOEXEC | O_NOFOLLOW; O_RDONLY is zero.
        if(h<0)throw new IOException("当前账户无法只读打开光驱数据节点（errno "+Marshal.GetLastWin32Error()+"）。可用已挂载的系统视图。");
        fd=new SafeFileHandle(new IntPtr(h),true);try{verify();}catch{fd.Dispose();throw;}
    }
    public override bool CanRead=>!fd.IsClosed; public override bool CanSeek=>CanRead;public override bool CanWrite=>false;
    public override long Length=>size;public override long Position{get=>position;set=>Seek(value,SeekOrigin.Begin);}
    public override long Seek(long offset,SeekOrigin origin){
        long next=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:origin==SeekOrigin.End?size:throw new ArgumentException())+offset);
        if(next<0||next>size)throw new IOException("光盘定位超界。");return position=next;
    }
    private readonly byte[] buffer=new byte[256*1024];
    public override int Read(byte[] b,int off,int count){
        ArgumentNullException.ThrowIfNull(b); if(off<0||count<0||count>b.Length-off)throw new ArgumentOutOfRangeException();
        if(fd.IsClosed)throw new ObjectDisposedException(nameof(DarwinOpticalStream));
        verify();int goal=(int)Math.Min(count,size-position),done=0;
        while(done<goal){
            int skip=(int)(position%block);long start=position-skip;
            int useful=Math.Min(goal-done,buffer.Length-skip);int rounded=((skip+useful+block-1)/block)*block;
            long n=Pread(fd.DangerousGetHandle().ToInt32(),buffer,(nuint)rounded,start);
            if(n!=rounded)throw new IOException("光盘数据扇区读取失败或短读（errno "+Marshal.GetLastWin32Error()+"）。");
            Buffer.BlockCopy(buffer,skip,b,off+done,useful);position+=useful;done+=useful;
        }
        verify();return done;
    }
    protected override void Dispose(bool disposing){if(disposing)fd.Dispose();base.Dispose(disposing);}
    public override void Flush(){} public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int i,int n)=>throw new NotSupportedException();
}
