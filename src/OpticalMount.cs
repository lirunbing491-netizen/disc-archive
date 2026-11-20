using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DiscArchive
{
    internal enum OpticalView { Auto, Mac, Udf, Iso }

    // One live read lease for this disc. A partition slice and its archive reader
    // share that lease until every consumer has finished with the selected view.
    internal sealed class OpticalMount : DiscSource, IDisposable
    {
        private sealed class Volume
        {
            internal string Root;
            internal ArchiveDiscSource Tree;
        }
        private readonly List<Volume> volumes=new List<Volume>();
        private readonly List<IDisposable> resources=new List<IDisposable>();
        private readonly List<string> missing=new List<string>();
        private readonly List<string> details=new List<string>();
        private readonly string root;
        private bool oneAtRoot;
        private OpticalMount(string rootPath) { root=Path.GetFullPath(rootPath); }

        internal static string NameOf(string type) { return type=="HFS" ? "HFS+" : type=="ISO" ? "ISO 9660 / Joliet" : type=="ClassicHFS" ? "传统 HFS" : type=="APFS" ? "APFS（实验性）" : type; }
        private static bool Apple(string format) { return format=="HFS" || format=="HFSX" || format=="APFS" || format=="ClassicHFS"; }
        internal static string Describe(DiscLayout layout)
        {
            var names=layout.Volumes.Select(x=>NameOf(x.Format)+"（偏移 "+x.Offset+"，"+x.Length+" 字节）").ToArray();
            string body=names.Length==0 ? "未确认可读取的文件系统。可能是特殊轨道、其他格式或元数据不可读。" : String.Join("\r\n",names);
            if(layout.Volumes.Any(x=>x.Format=="ClassicHFS")) body+="\r\n传统 HFS 只能识别，本版没有提取器。";
            if(layout.Volumes.Any(x=>Apple(x.Format)) && (layout.HasIso||layout.HasUdf))
                body+="\r\n此盘有多个视图；Mac、ISO 和 UDF 的目录可能不同。可在导盘页选择要读取的视图。";
            if(layout.HasIso && layout.HasUdf) body+="\r\n这是 ISO/UDF 混合视图，可分别选择。";
            if(!String.IsNullOrWhiteSpace(layout.Note)) body+="\r\n"+layout.Note;
            if(!String.IsNullOrWhiteSpace(layout.IncompleteApple))body+="\r\n"+layout.IncompleteApple;
            foreach(var v in layout.Volumes)if(!String.IsNullOrWhiteSpace(v.ConsistencyNote))body+="\r\n"+v.ConsistencyNote;
            return body;
        }
        internal static MountedDisc Open(string rootPath,Stream raw,Action verify,IDisposable lease,OpticalView view)
        {
            OpticalMount result=new OpticalMount(rootPath);
            if(lease!=null) result.resources.Add(lease);
            try {
                verify();
                DiscLayout layout=OpticalLayout.Inspect(raw);
                var apple=layout.Volumes.Where(v=>Apple(v.Format)).ToList();
                List<DiscVolume> selected;
                if(view==OpticalView.Mac || (view==OpticalView.Auto && apple.Any(v=>v.Format!="ClassicHFS"))) selected=apple;
                else if(view==OpticalView.Iso) selected=layout.Volumes.Where(v=>v.Format=="ISO").ToList();
                else if(view==OpticalView.Udf || layout.HasUdf) selected=layout.Volumes.Where(v=>v.Format=="UDF").ToList();
                else selected=layout.Volumes.Where(v=>v.Format=="ISO").ToList();
                if(selected.Count==0) throw new IOException("光驱可以读取数据扇区，当前视图未识别出可提取的卷。\r\n"+Describe(layout));
                result.oneAtRoot=selected.Count==1;
                result.details.Add(Describe(layout));
                if(selected.Any(v=>Apple(v.Format)) && !String.IsNullOrWhiteSpace(layout.IncompleteApple))
                    result.missing.Add(layout.IncompleteApple);
                List<string> labels=new List<string>(),formats=new List<string>();
                for(int i=0;i<selected.Count;i++) {
                    DiscVolume v=selected[i]; verify();
                    if(v.Format=="ClassicHFS") { result.missing.Add("发现传统 HFS，未提取此分区。");continue; }
                    try {
                        var slice=new DiscSlice(raw,v.Offset,v.Length); result.resources.Add(slice);
                        var reader=SevenZipReader.Open(slice,v.Format); result.resources.Add(reader);
                        HfsGuardResult guard=null;
                        if(v.Format=="HFS" || v.Format=="HFSX") guard=HfsCatalogGuard.Inspect(slice);
                        string label=reader.Label;
                        if(String.IsNullOrWhiteSpace(label) && (v.Format=="ISO" || v.Format=="UDF"))label=v.Name;
                        string volumeRoot=result.oneAtRoot ? result.root : Path.Combine(result.root,
                            "分区-"+(i+1).ToString("D2")+(String.IsNullOrWhiteSpace(label) ? "" : "-"+DiscPlan.Safe(label)));
                        result.volumes.Add(new Volume{ Root=volumeRoot, Tree=new ArchiveDiscSource(volumeRoot,reader,v.Format,guard,label) });
                        if(!String.IsNullOrWhiteSpace(v.ConsistencyNote))result.missing.Add(v.ConsistencyNote);
                        if(!String.IsNullOrWhiteSpace(label)) labels.Add(label);
                        formats.Add(NameOf(v.Format)); result.details.Add("本次读取 "+NameOf(v.Format)+"　"+label);
                        if(guard!=null) result.details.Add(guard.Note);
                    } catch(Exception error) {
                        if(!(error is IOException) && !(error is NotSupportedException) && !(error is UnauthorizedAccessException)) throw;
                        verify(); result.missing.Add("分区 "+(i+1)+" "+NameOf(v.Format)+" 未能读取："+error.Message);
                    }
                }
                if(result.volumes.Count==0)
                {
                    // Auto may salvage a readable alternative on a hybrid disc.
                    // A manually selected view is never silently switched.
                    if(view==OpticalView.Auto)
                    {
                        foreach(var other in new[]{OpticalView.Udf,OpticalView.Iso})
                        {
                            string type=other==OpticalView.Udf?"UDF":"ISO";
                            if(selected.Any(v=>v.Format==type) || !layout.Volumes.Any(v=>v.Format==type))continue;
                            verify();
                            try {
                                var alternate=Open(rootPath,raw,verify,null,other);
                                result.resources.Add(alternate);
                                return new MountedDisc(result.root,alternate.VolumeLabel,verify,result,alternate.Source,alternate.Format)
                                { UncoveredTracks="首选文件系统未能读取，已改读 "+NameOf(type)+"。"+String.Join(" ",result.missing.ToArray()) };
                            } catch(IOException ex){result.missing.Add(type+"："+ex.Message);}
                        }
                    }
                    throw new IOException("已能读取扇区，但所选卷无法提取。\r\n"+String.Join("\r\n",result.missing.ToArray()));
                }
                verify();
                return new MountedDisc(result.root,selected.Count==1 && labels.Count==1 ? labels[0] : "",verify,result,result,
                    String.Join(" + ",formats.Distinct().ToArray())+"（扇区只读）"+
                    (layout.Volumes.Any(v=>!selected.Contains(v)) ? "，仅本次所选视图" : ""));
            } catch(Exception error) { ResourceCleanup.Preserve(error,result.Dispose); throw; }
        }
        private bool IsCombinedRoot(string path) { return !oneAtRoot && String.Equals(Path.GetFullPath(path).TrimEnd('\\'),root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase); }
        private ArchiveDiscSource Tree(string path)
        {
            string p=Path.GetFullPath(path).TrimEnd('\\');
            foreach(Volume volume in volumes) {
                string v=volume.Root.TrimEnd('\\');
                if(p.Equals(v,StringComparison.OrdinalIgnoreCase) || p.StartsWith(v+"\\",StringComparison.OrdinalIgnoreCase))return volume.Tree;
            }
            throw new DirectoryNotFoundException("虚拟卷路径无效。");
        }
        internal override IEnumerable<string> Entries(string path)
        {
            if(IsCombinedRoot(path)) {
                foreach(Volume volume in volumes) yield return volume.Root;
            } else foreach(string entry in Tree(path).Entries(path)) yield return entry;
            if(missing.Count>0 && String.Equals(Path.GetFullPath(path).TrimEnd('\\'),root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
                throw new IOException(String.Join(" ",missing.ToArray()));
        }
        internal override FileAttributes Attributes(string path) {return IsCombinedRoot(path)?FileAttributes.Directory:Tree(path).Attributes(path);}
        internal override long Length(string path) {return Tree(path).Length(path);}
        internal override DateTime Modified(string path) {return IsCombinedRoot(path)?DateTime.MinValue:Tree(path).Modified(path);}
        internal override Stream OpenRead(string path) {return Tree(path).OpenRead(path);}
        internal override void WriteDescription(string attempt)
        {
            Catalog.CheckNoLinks(attempt);
            using(var f=new FileStream(Path.Combine(attempt,"光盘布局.txt"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            using(var w=new StreamWriter(f,new UTF8Encoding(true))) {
                foreach(string s in details)w.WriteLine(s);
                foreach(string s in missing)w.WriteLine(s);
            }
            foreach(Volume v in volumes)v.Tree.WriteDescription(attempt);
        }
        public void Dispose()
        {
            var cleanup=new ResourceCleanup();
            for(int i=resources.Count-1;i>=0;i--) {
                IDisposable resource=resources[i];
                cleanup.Run("mount."+resource.GetType().Name+"["+i+"]",resource.Dispose);
            }
            resources.Clear();
            cleanup.ThrowIfAny();
        }
    }

    // Only fixed stage names, exception types and numeric HRESULTs enter the
    // public diagnostic. Native exception messages may contain source paths.
    internal sealed class ResourceCleanup
    {
        private readonly List<Exception> failures=new List<Exception>();
        internal bool Run(string stage,Action close)
        {
            try {close();return true;}
            catch(Exception error) {
                var known=error as CleanupException;
                failures.Add(known ?? new IOException(stage+": "+error.GetType().Name+" HRESULT 0x"+error.HResult.ToString("X8")));
                return false;
            }
        }
        internal void ThrowIfAny()
        {
            if(failures.Count>0)throw new CleanupException(failures);
        }
        internal static void Preserve(Exception primary,Action close)
        {
            var cleanup=new ResourceCleanup();
            cleanup.Run("open.rollback",close);
            if(cleanup.failures.Count>0)
            {
                string previous=primary.Data["DiscArchive.Cleanup"] as string;
                primary.Data["DiscArchive.Cleanup"]=(String.IsNullOrEmpty(previous)?"":previous+"; ")+
                    String.Join("; ",cleanup.failures.Select(x=>x.Message).ToArray());
            }
        }
        private sealed class CleanupException : IOException
        {
            internal CleanupException(List<Exception> failures)
                :base("关闭光盘读句柄时出错。 "+String.Join("; ",failures.Select(x=>x.Message).ToArray()),new AggregateException(failures)) {}
        }
    }
}
