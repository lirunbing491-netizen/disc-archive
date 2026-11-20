using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiscArchive
{
    // This is a metadata-only projection. Opening a file delegates to the reader
    // for that item; constructing or enumerating the tree never extracts payloads.
    internal sealed class ArchiveDiscSource : DiscSource
    {
        private const string Preserved = "~DiscArchive-preserved";
        private sealed class Node
        {
            internal string Path, Original, Problem;
            internal ArchiveItem Item;
            internal bool Directory;
            internal readonly List<Node> Children = new List<Node>();
            internal readonly Dictionary<string,Node> RawChildren = new Dictionary<string,Node>(StringComparer.Ordinal);
            internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        private readonly Dictionary<string,Node> nodes = new Dictionary<string,Node>(ArchivePath.Comparer);
        private readonly Dictionary<int,Node> byIndex = new Dictionary<int,Node>();
        private readonly Dictionary<int,ArchiveItem> items = new Dictionary<int,ArchiveItem>();
        private readonly HashSet<int> building = new HashSet<int>();
        private readonly Func<int,Stream> open;
        private readonly Node root;
        private readonly string format, label;
        private readonly List<string> notes = new List<string>();
        private Node preserved;
        private string rootProblem;
        internal string VolumeLabel { get { return label; } }
        internal string OriginalRoot { get; private set; }

        internal ArchiveDiscSource(string virtualRoot, SevenZipReader archive, string format,HfsGuardResult guard = null,string volumeLabel = null)
            : this(virtualRoot, archive.Items, archive.OpenItem, volumeLabel??archive.Label, format,guard)
        {
            if(archive.ErrorFlags!=0 || archive.WarningFlags!=0)
                rootProblem=Join(rootProblem,"文件系统解析报告错误/警告，部分内容可能不可见："+archive.ErrorFlags+"/"+archive.WarningFlags+" "+archive.Warning);
            if(format!=null && format.StartsWith("HFS",StringComparison.OrdinalIgnoreCase) && !archive.HardLinkSupportKnown) notes.Add("当前HFS处理器不能保证识别所有Finder硬链接；未声明完整硬链接支持。");
        }

        // Synthetic callers provide metadata and an item opener, never a real disc.
        internal ArchiveDiscSource(string virtualRoot,IList<ArchiveItem> archiveItems,Func<int,Stream> opener,string volumeLabel,string archiveFormat,HfsGuardResult guard = null)
        {
            if(archiveItems==null || opener==null)throw new ArgumentNullException();
            format=archiveFormat??"";label=volumeLabel??"";open=opener;
            string full=Key(virtualRoot);
            root=new Node {Path=full,Original="",Directory=true};nodes.Add(full,root);
            List<ArchiveItem> sorted=new List<ArchiveItem>(archiveItems);
            sorted.Sort(delegate(ArchiveItem a,ArchiveItem b){return a.Index.CompareTo(b.Index);});
            foreach(ArchiveItem item in sorted)
            {
                if(item.Index<0 || items.ContainsKey(item.Index))throw new InvalidDataException("文件系统条目编号重复或无效。");
                items.Add(item.Index,item);
            }
            ArchiveItem shell=VolumeShell(sorted);
            if(shell!=null) { OriginalRoot=shell.Name;byIndex.Add(shell.Index,root);notes.Add("已按原始父子关系和卷标，仅去除卷根壳："+shell.Name); }
            foreach(ArchiveItem item in sorted) Build(item);
            if(format.StartsWith("HFS",StringComparison.OrdinalIgnoreCase) && guard!=null)ApplyHfsGuard(guard);
        }

        private void ApplyHfsGuard(HfsGuardResult guard)
        {
            notes.Add("HFS原生链接审计："+(guard.Note??"")+"；审计叶链完成="+guard.Audited+"；发现占位="+guard.Links);
            Dictionary<string,List<int>> paths=new Dictionary<string,List<int>>(StringComparer.Ordinal);
            foreach(ArchiveItem item in items.Values)
            {
                if(item.IsAltStream || item.ParentIsAltStream || item.IsAux)continue;
                string key=RawComponentsKey(item);if(key==null)continue;
                List<int> matching;if(!paths.TryGetValue(key,out matching)){matching=new List<int>();paths.Add(key,matching);}matching.Add(item.Index);
            }
            HashSet<int> guarded=new HashSet<int>();int unresolved=0,known=0;
            foreach(HfsNativeLink link in guard.Entries??new HfsNativeLink[0])
            {
                known++;List<int> matching;string key=ComponentsKey(link.Components);
                notes.Add("原生链接证据：CNID="+link.CNID+"；目录="+link.Directory+"；引用="+link.LinkReference+"；长度前缀组件="+(key??"[组件缺失]")+"；诊断路径="+(link.Path??""));
                if(key==null || !paths.TryGetValue(key,out matching)) {unresolved++;continue;}
                if(matching.Count!=1)unresolved++;
                foreach(int index in matching)
                {
                    guarded.Add(index);Node node=byIndex[index];
                    node.Problem=Join(node.Problem,"HFS原生"+(link.Directory?"目录":"文件")+"硬链接占位（CNID="+link.CNID+"，引用="+link.LinkReference+"），不跟随且不按零字节文件计成功。");
                }
            }
            // Sidecars have their own export directory, but still belong to the
            // guarded native object and must not escape its refusal via relocation.
            foreach(ArchiveItem item in items.Values)
            {
                if(guarded.Contains(item.Index))continue;
                ArchiveItem current=item;HashSet<int> seen=new HashSet<int>();
                while(current.ParentIndex>=0 && seen.Add(current.Index))
                {
                    if(guarded.Contains(current.ParentIndex))
                    {byIndex[item.Index].Problem=Join(byIndex[item.Index].Problem,"所属HFS对象为原生硬链接占位，未读取其派生条目。");break;}
                    if(!items.TryGetValue(current.ParentIndex,out current))break;
                }
            }
            if(!guard.Audited || unresolved>0 || guard.Links!=known || (guard.Links==0 && !guard.Complete))
                rootProblem=Join(rootProblem,"HFS原生链接审计尚未确认完整：未精确关联="+unresolved+"；已知占位="+guard.Links+"；可核对记录="+known+"。"+(guard.Note??""));
        }
        private string RawComponentsKey(ArchiveItem item)
        {
            List<string> parts=new List<string>();HashSet<int> seen=new HashSet<int>();ArchiveItem current=item;
            while(true)
            {
                if(!seen.Add(current.Index) || String.IsNullOrEmpty(current.Name))return null;
                parts.Add(current.Name);if(current.ParentIndex<0)break;
                if(!items.TryGetValue(current.ParentIndex,out current))return null;
            }
            parts.Reverse();return ComponentsKey(parts);
        }
        private static string ComponentsKey(IList<string> parts)
        {
            if(parts==null || parts.Count==0)return null;StringBuilder b=new StringBuilder();
            foreach(string part in parts){if(part==null)return null;b.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);}return b.ToString();
        }

        private ArchiveItem VolumeShell(List<ArchiveItem> sorted)
        {
            if(!format.StartsWith("HFS",StringComparison.OrdinalIgnoreCase) || label.Length==0)return null;
            ArchiveItem candidate=null;
            foreach(ArchiveItem item in sorted)
                if(item.ParentIndex<0 && !item.IsAltStream && !item.IsAux && !item.IsDeleted)
                {
                    if(candidate!=null)return null;
                    candidate=item;
                }
            if(candidate==null || !candidate.IsDirectory || candidate.IsLink || candidate.IsHardLink || !String.IsNullOrEmpty(ItemProblem(candidate)) || !String.Equals(candidate.Name,label,StringComparison.Ordinal))return null;
            // At least one raw child proves this is an actual volume container.
            bool child=false;
            foreach(ArchiveItem item in sorted)
            {
                if(item==candidate)continue;
                HashSet<int> seen=new HashSet<int>();ArchiveItem current=item;
                while(current.ParentIndex>=0 && seen.Add(current.Index))
                {
                    if(current.ParentIndex==candidate.Index){child=true;break;}
                    if(!items.TryGetValue(current.ParentIndex,out current))return null;
                }
                if(current.ParentIndex!=candidate.Index)return null;
            }
            return child?candidate:null;
        }

        private Node Build(ArchiveItem item)
        {
            Node existing;if(byIndex.TryGetValue(item.Index,out existing))return existing;
            if(!building.Add(item.Index))throw new InvalidDataException("文件系统父子关系成环，不能安全建立目录。");
            Node parent=root;string name=item.Name;string problem=ItemProblem(item);
            bool auxiliary=item.IsAltStream || item.ParentIsAltStream || item.IsAux;
            bool rawTree=format.StartsWith("HFS",StringComparison.OrdinalIgnoreCase) || format.StartsWith("APFS",StringComparison.OrdinalIgnoreCase) || format=="POSIX";
            string parentProblem=ParentProblem(item);
            problem=Join(problem,parentProblem);
            if(auxiliary)
            {
                parent=PreservedRoot();
                name="item-"+item.Index.ToString(CultureInfo.InvariantCulture)+"-"+(String.IsNullOrEmpty(name)?Leaf(item.Path):name);
            }
            else if(parentProblem!=null)
            { parent=PreservedRoot(); }
            else if(item.ParentIndex>=0)
            {
                ArchiveItem parentItem;
                if(items.TryGetValue(item.ParentIndex,out parentItem))
                {
                    parent=Build(parentItem);
                    if(!parent.Directory) {parent=PreservedRoot();problem=Join(problem,"原始父条目不是目录，已隔离并拒读。");}
                }
                else {parent=PreservedRoot();problem=Join(problem,"原始父条目缺失，已隔离并拒读。");}
            }
            else if(!rawTree)
            {
                // Windows ISO/UDF handlers expose hierarchy through backslash
                // paths; HFS/APFS use their raw parent graph and literal Name.
                string[] parts=(item.Path??"").Split('\\');
                for(int i=0;i<parts.Length-1;i++) parent=Implicit(parent,parts[i]);
                name=parts[parts.Length-1];
            }
            if(rawTree && String.IsNullOrEmpty(item.Name))
            {
                parent=PreservedRoot();name=String.IsNullOrEmpty(item.Path)?"unnamed-item-"+item.Index:item.Path;
                problem=Join(problem,"缺少处理器原始名称，无法无歧义恢复层级，已隔离并拒读。");
            }
            if(String.IsNullOrEmpty(name)) {name="unnamed-item-"+item.Index;problem=Join(problem,"原始文件名缺失，已拒读。");}
            Node implicitNode;
            if(!rawTree && item.IsDirectory && parent.RawChildren.TryGetValue(name,out implicitNode) && implicitNode.Item==null)
            {
                implicitNode.Item=item;implicitNode.Original=item.Path;implicitNode.Problem=problem;byIndex.Add(item.Index,implicitNode);building.Remove(item.Index);return implicitNode;
            }
            Node logicalParent=parent;
            string safe=SafeName(name),full=Path.Combine(parent.Path,safe);
            if(full.Length>235)
            {
                parent=PreservedRoot();safe="long-item-"+item.Index+"-"+Digest(name);notes.Add("超长路径条目 "+item.Index+" 已映射到受保护旁存目录。");
            }
            safe=Unique(parent,safe,item.Index);full=Path.Combine(parent.Path,safe);
            Node node=new Node {Path=full,Original=item.Path??name,Item=item,Directory=item.IsDirectory,Problem=problem};
            parent.Children.Add(node);if(!logicalParent.RawChildren.ContainsKey(name))logicalParent.RawChildren.Add(name,node);nodes.Add(full,node);byIndex.Add(item.Index,node);building.Remove(item.Index);
            return node;
        }
        private string ParentProblem(ArchiveItem item)
        {
            HashSet<int> seen=new HashSet<int>();ArchiveItem current=item;
            while(current.ParentIndex>=0)
            {
                if(!seen.Add(current.Index))return "原始父子关系成环，已隔离并拒读。";
                if(!items.TryGetValue(current.ParentIndex,out current))return "原始父条目缺失，已隔离并拒读。";
            }
            return null;
        }
        private static string ItemProblem(ArchiveItem item)
        {
            string result=Join(item.Error,item.Issue);
            if(item.IsLink || item.IsHardLink)result=Join(result,"符号链接或硬链接不跟随，未提取链接内容。");
            if(item.IsEncrypted)result=Join(result,"加密条目未读取。");
            if(item.IsDeleted)result=Join(result,"已删除条目不能作为正常文件提取。");
            if(!item.IsDirectory && item.Size<0)result=Join(result,"缺少有效文件长度。");
            return result;
        }
        private Node PreservedRoot()
        {
            if(preserved!=null)return preserved;
            preserved=new Node {Path=Path.Combine(root.Path,Preserved),Original="",Directory=true};
            root.Children.Add(preserved);root.Names.Add(Canonical(Preserved));nodes.Add(preserved.Path,preserved);return preserved;
        }
        private Node Implicit(Node parent,string name)
        {
            Node found;
            if(parent.RawChildren.TryGetValue(name,out found) && found.Directory)return found;
            Node logicalParent=parent;
            string safe=SafeName(name),path=Path.Combine(parent.Path,safe);
            if(path.Length>235) { parent=PreservedRoot();safe="long-directory-"+Digest(path); }
            safe=Unique(parent,safe,-1);path=Path.Combine(parent.Path,safe);
            Node child=new Node {Path=path,Original=name,Directory=true};parent.Children.Add(child);logicalParent.RawChildren[name]=child;nodes.Add(path,child);return child;
        }
        private static string Join(string a,string b) { return String.IsNullOrEmpty(a)?b:String.IsNullOrEmpty(b)?a:a+"；"+b; }
        private static string Leaf(string path) { string s=path??"";int p=s.LastIndexOf('/');return p<0?s:s.Substring(p+1); }
        private static string Key(string path)
        {
            string full=Path.GetFullPath(path).TrimEnd('\\','/');return full.EndsWith(":")?full+Path.DirectorySeparatorChar:full;
        }
        private static string Canonical(string value)
        { try{return value.Normalize(NormalizationForm.FormC);}catch(ArgumentException){return value;} }
        private static string SafeName(string original)
        {
            StringBuilder b=new StringBuilder();
            foreach(char c in original)
                if(c=='~' || Char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c)>=0 || Char.IsSurrogate(c)) b.Append('~').Append(((int)c).ToString("X4"));else b.Append(c);
            string value=b.ToString();
            while(value.EndsWith(".") || value.EndsWith(" ")) {char c=value[value.Length-1];value=value.Substring(0,value.Length-1)+"~"+((int)c).ToString("X4");}
            if(value.Length==0)value="~empty";
            string stem=value.Split('.')[0].ToUpperInvariant();
            if(stem=="CON" || stem=="PRN" || stem=="AUX" || stem=="NUL" || stem=="CONIN$" || stem=="CONOUT$" || (stem.Length==4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && ((stem[3]>='0' && stem[3]<='9') || stem[3]=='¹' || stem[3]=='²' || stem[3]=='³')))value="~device-"+value;
            if(value.Length>100)value=value.Substring(0,65)+"~long-"+Digest(original);
            return value;
        }
        private static byte[] Utf16(string value)
        { byte[] b=new byte[value.Length*2];for(int i=0;i<value.Length;i++){b[i*2]=(byte)value[i];b[i*2+1]=(byte)(value[i]>>8);}return b; }
        private static string Digest(string value)
        { using(SHA256 hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Utf16(value))).Replace("-","").Substring(0,24); }
        private static string Unique(Node parent,string name,int index)
        {
            string result=name;int suffix=0;
            while(!parent.Names.Add(Canonical(result)))result=name+"~item-"+index.ToString(CultureInfo.InvariantCulture)+(suffix++==0?"":"-"+suffix);
            return result;
        }
        private Node Find(string path)
        { Node node;if(!nodes.TryGetValue(Key(path),out node))throw new FileNotFoundException("虚拟源条目不存在。",path);return node; }
        private static void Check(Node node)
        { if(!String.IsNullOrEmpty(node.Problem))throw new IOException("文件系统条目不可读："+node.Problem); }
        internal override IEnumerable<string> Entries(string directory)
        {
            Node node=Find(directory);Check(node);if(!node.Directory)throw new IOException("条目不是目录。");
            foreach(Node child in node.Children)yield return child.Path;
            if(node==root && !String.IsNullOrEmpty(rootProblem))throw new IOException(rootProblem);
        }
        internal override FileAttributes Attributes(string path)
        { Node node=Find(path);Check(node);return node.Directory?FileAttributes.Directory:FileAttributes.Normal; }
        internal override long Length(string path)
        { Node node=Find(path);Check(node);if(node.Directory)throw new IOException("目录没有文件长度。");return node.Item.Size; }
        internal override DateTime Modified(string path)
        { Node node=Find(path);Check(node);return node.Item!=null && node.Item.Modified.HasValue?node.Item.Modified.Value:DateTime.MinValue; }
        internal override Stream OpenRead(string path)
        {
            Node node=Find(path);Check(node);if(node.Directory)throw new IOException("不能按文件打开目录。");
            try { return open(node.Item.Index); }
            catch(NotSupportedException e) {throw new IOException("此文件系统条目不能提取。",e);}
        }
        internal override void WriteDescription(string attemptDirectory)
        {
            ContinuationJournal.Safe(attemptDirectory);Directory.CreateDirectory(attemptDirectory);
            string path=Path.Combine(attemptDirectory,"文件系统路径映射-"+Guid.NewGuid().ToString("N")+".tsv");ContinuationJournal.Safe(path);
            using(StreamWriter w=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(true)))
            {
                w.WriteLine("说明\t路径和名称为7-Zip处理器提供值，不保证等同盘上原始文件名字节。资源叉、额外流和辅助项按标志保留，不根据文件名猜测。导出相对路径为虚拟源相对路径；最终目标位置以抢救路径清单为准。");
                w.WriteLine("格式\t"+Escape(format));w.WriteLine("物理卷标\t"+Escape(label));w.WriteLine("剥除卷根壳\t"+Escape(OriginalRoot));
                w.WriteLine("解析提示\t"+Escape(rootProblem));foreach(string note in notes)w.WriteLine("说明\t"+Escape(note));
                w.WriteLine("条目编号\t处理器路径\t处理器名称\t父编号\t导出相对路径\t目录\t额外流\t辅助项\t链接目标\t状态\t处理器路径UTF16-Base64");
                List<int> indexes=new List<int>(items.Keys);indexes.Sort();
                foreach(int index in indexes)
                {
                    ArchiveItem item=items[index];Node node=byIndex[index];string relative=node==root?"":node.Path.Substring(root.Path.Length).TrimStart('\\','/');
                    w.WriteLine(index+"\t"+Escape(item.Path)+"\t"+Escape(item.Name)+"\t"+item.ParentIndex+"\t"+Escape(relative)+"\t"+item.IsDirectory+"\t"+(item.IsAltStream||item.ParentIsAltStream)+"\t"+item.IsAux+"\t"+Escape(item.LinkTarget)+"\t"+Escape(node.Problem)+"\t"+Convert.ToBase64String(Utf16(item.Path??"")));
                }
            }
        }
        private static string Escape(string value)
        {
            StringBuilder b=new StringBuilder();foreach(char c in value??"")
                if(c=='\\')b.Append("\\\\");else if(Char.IsControl(c) || Char.IsSurrogate(c))b.Append("\\u").Append(((int)c).ToString("X4"));else b.Append(c);
            return b.ToString();
        }
    }
}
