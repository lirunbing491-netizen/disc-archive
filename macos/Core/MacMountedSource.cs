using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiscArchive;

// Projects OS-mounted optical volumes; no mounting, source writes, or processes.
// Darwin openat walks relative to a volume FD with O_NOFOLLOW on every component.
// fgetxattr uses that same safely opened object, never a symlink pathname.
internal sealed class MacMountedSource : DiscSource
{
    private sealed class Backing {
        internal string Root="",Relative="",Attribute="";
        internal long Length;
    }
    private sealed class IdentityError:IOException { internal IdentityError(Exception inner):base("光盘身份校验失败，已停止本次读取。",inner){} }
    private readonly Action verify;
    private readonly ArchiveDiscSource tree;
    private readonly List<ArchiveItem> items=new();
    private readonly Dictionary<int,Backing> backing=new();
    private readonly List<string> metadata=new();
    private int next;
    private readonly string formats;
    internal MacMountedSource(string virtualRoot,List<MacVolume> volumes,Action verify)
    {
        if(verify==null)throw new ArgumentNullException(nameof(verify));
        this.verify=()=>{try{verify();}catch(Exception ex){throw new IdentityError(ex);}};
        if(volumes==null||volumes.Count==0)throw new IOException("没有已挂载的数据卷。");
        formats=string.Join(" + ",volumes.Select(v=>v.format??"").Distinct());
        foreach(var volume in volumes.OrderBy(v=>v.offset).ThenBy(v=>v.mount,StringComparer.Ordinal)) {
            this.verify();
            string root=Path.GetFullPath(volume.mount);
            if(!Path.IsPathFullyQualified(root))throw new IOException("挂载路径无效。");
            CheckLinks(root,"");
            int parent=-1;
            if(volumes.Count>1) parent=Add(volume.label.Length>0?volume.label:"Volume",root,-1,true,0,null).Index;
            var extras=new List<(ArchiveItem item,Backing source)>();
            Scan(root,"",parent,extras,0);
            if(extras.Count>0) {
                int auxiliary=Add("~macOS-metadata",root+" [native metadata]",parent,true,0,null).Index;
                foreach(var extra in extras) { extra.item.ParentIndex=auxiliary; items.Add(extra.item);backing[extra.item.Index]=extra.source; }
            }
            this.verify();
        }
        tree=new ArchiveDiscSource(virtualRoot,items,OpenItem,volumes.Count==1?volumes[0].label:"","POSIX");
        this.verify();
    }
    private T Read<T>(Func<T> action) {verify();try{return action();}finally{verify();}}
    private void Read(Action action) {Read(()=>{action();return 0;});}
    private ArchiveItem Add(string name,string original,int parent,bool directory,long length,string issue) {
        var item=new ArchiveItem{Index=next++,Name=name,Path=original,ParentIndex=parent,IsDirectory=directory,Size=length,Issue=issue??""};
        items.Add(item);return item;
    }
    private void Failure(string root,string relative,int parent,string message) {
        Add("~unreadable-"+next,Path.Combine(root,relative),parent,false,0,message);
    }
    private void Scan(string root,string relative,int parent,List<(ArchiveItem item,Backing source)> extras,int depth)
    {
        if(depth>512){Failure(root,relative,parent,"目录深度超过保护边界，尚未完成此子目录清点。");return;}
        List<string> names=new();
        Exception enumerationError=null;
        // Obtain names while the directory FD stays alive. Failure after some
        // entries does not discard those names or any other mounted volume.
        try {
            Read(()=> {
                CheckLinks(root,relative);
                if(OperatingSystem.IsMacOS()) {
                    using var handle=OpenDarwin(root,relative,true);
                    ReadDarwinNames(handle,names);
                } else {
                    using var iterator=Directory.EnumerateFileSystemEntries(Path.Combine(root,relative),"*",new EnumerationOptions{AttributesToSkip=0,IgnoreInaccessible=false,ReturnSpecialDirectories=false}).GetEnumerator();
                    while(iterator.MoveNext())names.Add(Path.GetFileName(iterator.Current));
                }
            });
        }catch(Exception ex) when(IsSourceError(ex)){verify();enumerationError=ex;}
        names.Sort(StringComparer.Ordinal);
        foreach(string name in names) {
            string child=Path.Combine(relative,name),full=Path.Combine(root,child);
            ArchiveItem item=null;
            try {
                var attributes=Read(()=>File.GetAttributes(full));
                bool link=(attributes&FileAttributes.ReparsePoint)!=0;
                bool directory=(attributes&FileAttributes.Directory)!=0;
                item=Add(name,full,parent,directory,0,link?"符号链接不跟随；其目标内容未读取。":null);
                if(link){item.IsLink=true;continue;}
                item.Modified=Read(()=>File.GetLastWriteTime(full));
                if(!directory) {
                    item.Size=Read(()=>new FileInfo(full).Length);
                    backing[item.Index]=new Backing{Root=root,Relative=child,Length=item.Size};
                }
                if(OperatingSystem.IsMacOS())ScanMetadata(root,child,item,directory,extras);
                if(directory)Scan(root,child,item.Index,extras,depth+1);
            }catch(Exception ex) when(IsSourceError(ex)) {
                verify();
                if(item==null)Add(name,full,parent,false,0,"无法读取此条目的元数据："+ex.Message);
                else if(item.IsDirectory)Failure(root,child,item.Index,"此目录未完成清点："+ex.Message);
                else item.Issue="此文件未完成清点："+ex.Message;
            }
        }
        if(enumerationError!=null)Failure(root,relative,parent,"目录遍历中断，已保留可见同级内容："+enumerationError.Message);
    }
    private static bool IsSourceError(Exception ex)=>ex is not IdentityError&&(ex is IOException||ex is UnauthorizedAccessException||ex is System.Security.SecurityException);
    private void ScanMetadata(string root,string relative,ArchiveItem original,bool directory,List<(ArchiveItem item,Backing source)> extras)
    {
        // Metadata errors are separate entries: a bad resource fork never hides
        // a readable photograph or the readable contents of its parent folder.
        foreach(string attribute in directory?new[]{"com.apple.FinderInfo"}:new[]{"com.apple.ResourceFork","com.apple.FinderInfo"}) {
            try {
                long length=Read(()=> {
                    using var handle=OpenDarwin(root,relative,directory);
                    long size=FGetXattr(handle,attribute,null,0,0,0);
                    if(size>=0)return size;
                    int error=Marshal.GetLastWin32Error();
                    if(error==93||error==45)return -1; // Darwin ENOATTR / ENOTSUP: ordinary absence.
                    throw new IOException("读取 "+attribute+" 属性大小失败（errno "+error+"）。");
                });
                if(length<0)continue;
                bool resource=attribute=="com.apple.ResourceFork";
                var item=new ArchiveItem{Index=next++,Name=original.Index+(resource?"-resource.rsrc":"-FinderInfo.bin"),Path=original.Path+" ["+attribute+"]",Size=length,Modified=original.Modified};
                if((resource&&length>uint.MaxValue)||(!resource&&length>1024*1024))item.Issue="属性长度超过安全读取边界，已保留失败记录。";
                extras.Add((item,new Backing{Root=root,Relative=relative,Attribute=attribute,Length=length}));
                metadata.Add(original.Index+"\t"+Escape(original.Path)+"\t"+attribute+"\t"+item.Index+"\t"+length);
            }catch(Exception ex) when(IsSourceError(ex)) {
                verify();
                var item=new ArchiveItem{Index=next++,Name=original.Index+"-metadata-error",Path=original.Path+" ["+attribute+"]",Size=0,Issue="原生辅助信息未读取："+ex.Message};
                extras.Add((item,new Backing()));
            }
        }
    }
    private Stream OpenItem(int index)
    {
        if(!backing.TryGetValue(index,out var b))throw new IOException("此条目没有可读取的数据流。");
        verify();
        Stream stream=null;
        try {
            CheckLinks(b.Root,b.Relative);
            if(b.Attribute.Length>0) {
                var handle=OpenDarwin(b.Root,b.Relative,(File.GetAttributes(Path.Combine(b.Root,b.Relative))&FileAttributes.Directory)!=0);
                try{stream=new AttributeStream(handle,b.Attribute,b.Length);}catch{handle.Dispose();throw;}
            } else if(OperatingSystem.IsMacOS()) {
                var handle=OpenDarwin(b.Root,b.Relative,false);
                try{stream=new FileStream(handle,FileAccess.Read);}catch{handle.Dispose();throw;}
            }else stream=new FileStream(Path.Combine(b.Root,b.Relative),FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length!=b.Length){stream.Dispose();throw new IOException("文件或属性长度已改变。");}
            verify();return new VerifiedStream(stream,verify);
        }catch{stream?.Dispose();verify();throw;}
    }
    private static void CheckLinks(string root,string relative)
    {
        if(Path.IsPathRooted(relative))throw new IOException("源相对路径无效。");
        string current=root;
        if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("挂载根是符号链接，已拒绝跟随。");
        foreach(string part in relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries)) {
            if(part=="."||part=="..")throw new IOException("源相对路径越界。");
            current=Path.Combine(current,part);
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("源路径包含符号链接，已拒绝跟随。");
        }
    }
    private const int NoFollow=0x100,DirectoryFlag=0x100000,CloseOnExec=0x1000000,NonBlock=4;
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="open",SetLastError=true)] private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)]string path,int flags);
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="openat",SetLastError=true)] private static extern int NativeOpenAt(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string path,int flags);
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="fgetxattr",SetLastError=true)] private static extern nint FGetXattr(SafeFileHandle fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,byte[] data,nuint length,uint position,int options);
    private sealed class DirectoryNative {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ListFn(int fd);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeFn(IntPtr pointer);
        internal readonly ListFn List;
        internal readonly FreeFn Free;
        // Keep the image and its delegates alive for the process. The loader's
        // architecture selection and SDK dirent ABI belong to this native image.
        private readonly IntPtr library;
        internal DirectoryNative() {
            string path=Environment.GetEnvironmentVariable("DISCARC_DEVICE_LIB");
            if(string.IsNullOrEmpty(path)||!Path.IsPathFullyQualified(path))throw new IOException("应用包缺少原生目录读取组件。");
            try {
                library=NativeLibrary.Load(path);
                List=Marshal.GetDelegateForFunctionPointer<ListFn>(NativeLibrary.GetExport(library,"da_list_directory"));
                Free=Marshal.GetDelegateForFunctionPointer<FreeFn>(NativeLibrary.GetExport(library,"da_free"));
            }catch(Exception ex) when(ex is DllNotFoundException||ex is EntryPointNotFoundException||ex is BadImageFormatException) {
                if(library!=IntPtr.Zero)NativeLibrary.Free(library);
                throw new IOException("原生目录读取组件不完整或架构不匹配。",ex);
            }
        }
    }
    private static readonly Lazy<DirectoryNative> DirectoryApi=new(()=>new DirectoryNative());
    private static void ReadDarwinNames(SafeFileHandle handle,List<string> names)
    {
        DirectoryNative api=DirectoryApi.Value;
        IntPtr pointer=api.List(handle.DangerousGetHandle().ToInt32());
        GC.KeepAlive(handle);
        if(pointer==IntPtr.Zero)throw new IOException("原生目录读取没有返回清单。");
        try { ApplyDirectoryReport(Marshal.PtrToStringUTF8(pointer)??throw new IOException("原生目录清单为空。"),names); }
        finally{api.Free(pointer);}
    }
    private static void ApplyDirectoryReport(string json,List<string> names)
    {
        try {
            using var report=JsonDocument.Parse(json);
            foreach(var entry in report.RootElement.GetProperty("names").EnumerateArray()) {
                string name=entry.GetString();
                if(string.IsNullOrEmpty(name)||name=="."||name==".."||name.IndexOf('\0')>=0||name.IndexOf('/')>=0)throw new IOException("原生目录清单含无效名称。");
                names.Add(name);
            }
            if(report.RootElement.TryGetProperty("note",out var note)&&note.ValueKind==JsonValueKind.String&&!string.IsNullOrEmpty(note.GetString())) {
                try{Console.Error.WriteLine("DiscArchive directory cleanup: "+note.GetString());}catch(IOException){}catch(ObjectDisposedException){}
            }
            string error=report.RootElement.GetProperty("error").GetString();
            if(!string.IsNullOrEmpty(error))throw new IOException(error); // Keep already-added sibling names.
        }catch(Exception ex) when(ex is JsonException||ex is KeyNotFoundException||ex is InvalidOperationException) {
            throw new IOException("原生目录清单格式无效。",ex);
        }
    }
    private static SafeFileHandle OpenDarwin(string root,string relative,bool directory)
    {
        int start=NativeOpen(root,NoFollow|DirectoryFlag|CloseOnExec|NonBlock);
        if(start<0)throw new IOException("无法安全打开挂载根（errno "+Marshal.GetLastWin32Error()+"）。");
        var current=new SafeFileHandle((IntPtr)start,true);
        try {
            string[] parts=relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries);
            for(int i=0;i<parts.Length;i++) {
                if(parts[i]=="."||parts[i]=="..")throw new IOException("源相对路径越界。");
                int fd=NativeOpenAt(current.DangerousGetHandle().ToInt32(),parts[i],NoFollow|CloseOnExec|NonBlock|((i<parts.Length-1||directory)?DirectoryFlag:0));
                if(fd<0)throw new IOException("无法安全只读打开源条目（errno "+Marshal.GetLastWin32Error()+"）。");
                current.Dispose();current=new SafeFileHandle((IntPtr)fd,true);
            }
            return current;
        }catch{current.Dispose();throw;}
    }
    internal override IEnumerable<string> Entries(string directory)=>tree.Entries(directory);
    internal override FileAttributes Attributes(string path)=>tree.Attributes(path);
    internal override long Length(string path)=>tree.Length(path);
    internal override DateTime Modified(string path)=>tree.Modified(path);
    internal override Stream OpenRead(string path)=>tree.OpenRead(path);
    internal override void WriteDescription(string attemptDirectory)
    {
        tree.WriteDescription(attemptDirectory);
        string destination=Path.Combine(attemptDirectory,"macOS原生挂载与旁存说明-"+Guid.NewGuid().ToString("N")+".tsv");
        ContinuationJournal.Safe(destination);
        using var w=new StreamWriter(new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(true));
        w.WriteLine("说明\t本次为 macOS 原生挂载视图，非7-Zip读取；通用路径映射表中的‘处理器’指此POSIX适配器。路径来自系统Unicode接口，未承诺盘上原始名字节。");
        w.WriteLine("格式\t"+Escape(formats));
        w.WriteLine("边界\t数据叉原样读取；可读资源叉和FinderInfo以编号旁存文件保留，不自动恢复到目标xattrs。未复制ACL、所有扩展属性、硬链接关系或Finder全部行为；未声称覆盖未挂载历史会话。无属性/文件系统不支持属性不算文件失败。符号链接不跟随。");
        w.WriteLine("源条目编号\t原始源路径\t属性名称\t旁存条目编号\t字节数");
        foreach(string line in metadata)w.WriteLine(line);
    }
    private static string Escape(string value)=>(value??"").Replace("\\","\\\\").Replace("\t","\\t").Replace("\r","\\r").Replace("\n","\\n");
    private sealed class AttributeStream:Stream {
        private readonly SafeFileHandle handle;private readonly string name;private readonly long length;private long position;private readonly byte[] whole;
        internal AttributeStream(SafeFileHandle handle,string name,long length){this.handle=handle;this.name=name;this.length=length;long size=FGetXattr(handle,name,null,0,0,0);if(size!=length)throw new IOException("原生属性长度变化或不可读。");if(name!="com.apple.ResourceFork"){if(length>1024*1024)throw new IOException("Finder属性长度超过安全边界。");whole=new byte[(int)length];if(FGetXattr(handle,name,whole,(nuint)length,0,0)!=length)throw new IOException("Finder属性读取失败。");}}
        public override bool CanRead=>!handle.IsClosed;public override bool CanWrite=>false;public override bool CanSeek=>CanRead;public override long Length=>length;public override long Position{get=>position;set=>Seek(value,SeekOrigin.Begin);}
        public override int Read(byte[] buffer,int offset,int count){ArgumentNullException.ThrowIfNull(buffer);if(offset<0||count<0||count>buffer.Length-offset)throw new ArgumentOutOfRangeException();int goal=(int)Math.Min(Math.Min(count,256*1024),length-position);if(goal==0)return 0;if(whole!=null){Buffer.BlockCopy(whole,(int)position,buffer,offset,goal);position+=goal;return goal;}byte[] temporary=new byte[goal];long got=FGetXattr(handle,name,temporary,(nuint)goal,checked((uint)position),0);if(got!=goal)throw new IOException("原生属性读取失败或短读（errno "+Marshal.GetLastWin32Error()+"）。");Buffer.BlockCopy(temporary,0,buffer,offset,goal);position+=goal;return goal;}
        public override long Seek(long offset,SeekOrigin origin){long next=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:origin==SeekOrigin.End?length:throw new ArgumentException())+offset);if(next<0||next>length)throw new IOException("属性定位越界。");return position=next;}
        protected override void Dispose(bool disposing){if(disposing)handle.Dispose();base.Dispose(disposing);}public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class VerifiedStream:Stream {
        private readonly Stream source;private readonly Action guard;
        internal VerifiedStream(Stream source,Action guard){this.source=source;this.guard=guard;}
        public override bool CanRead=>source.CanRead;public override bool CanSeek=>source.CanSeek;public override bool CanWrite=>false;public override long Length=>source.Length;public override long Position{get=>source.Position;set=>source.Position=value;}
        public override int Read(byte[] buffer,int offset,int count){guard();try{return source.Read(buffer,offset,count);}finally{guard();}}
        public override long Seek(long offset,SeekOrigin origin)=>source.Seek(offset,origin);
        protected override void Dispose(bool disposing){if(disposing)source.Dispose();base.Dispose(disposing);}public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
