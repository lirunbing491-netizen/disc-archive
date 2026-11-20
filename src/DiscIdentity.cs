using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace DiscArchive
{
    internal sealed class DiscMatch
    {
        public string Id, Status, Project, Reason, JobDir;
        public bool Strong;
    }

    // An identity is evidence for a human decision, never a whole-disc equality claim.
    internal sealed class DiscIdentity
    {
        public string Key, Evidence;
        public bool Complete;
        public const int MaximumEntries = 200000, MaximumSamples = 12, SampleBytes = 2048;
        private readonly SortedDictionary<string, long> files = new SortedDictionary<string, long>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> samples = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private const string IndexName = ".discarchive-identity-v1";
        // Replace is isolated so synthetic tests can emulate exFAT's unsupported-operation code.
        internal static Action<string,string> ReplaceFile = delegate(string source,string destination) {File.Replace(source,destination,null);};
        private int errors;

        public static DiscIdentity Inspect(string root, Action verify, CancellationToken cancel, DiscSource media = null)
        {
            media = media ?? DiscSource.Windows;
            DiscIdentity result = new DiscIdentity { Complete = true };
            string full = Path.GetFullPath(root);
            List<string> structure = new List<string>();
            Stack<string> pending = new Stack<string>(); pending.Push(full);
            int count = 0;
            while (pending.Count > 0 && count < MaximumEntries)
            {
                Check(verify, cancel);
                string dir = pending.Pop();
                try
                {
                    using (IEnumerator<string> entries = media.Entries(dir).GetEnumerator())
                    while (true)
                    {
                        Check(verify, cancel);
                        if (!entries.MoveNext()) break;
                        if (++count > MaximumEntries) { result.Complete = false; break; }
                        string path = entries.Current;
                        try
                        {
                            FileAttributes a = media.Attributes(path);
                            if ((a & FileAttributes.ReparsePoint) != 0) { result.Complete = false; result.errors++; continue; }
                            string relative = Relative(full, path);
                            if ((a & FileAttributes.Directory) != 0) { structure.Add("D\t" + relative); pending.Push(path); }
                            else { long length = media.Length(path); result.files[relative] = length; structure.Add("F\t" + relative + "\t" + length.ToString(CultureInfo.InvariantCulture)); }
                        }
                        catch (IOException) { result.Complete = false; result.errors++; }
                        catch (UnauthorizedAccessException) { result.Complete = false; result.errors++; }
                    }
                }
                catch (IOException) { result.Complete = false; result.errors++; }
                catch (UnauthorizedAccessException) { result.Complete = false; result.errors++; }
            }
            if (pending.Count > 0) result.Complete = false;
            structure.Sort(StringComparer.Ordinal);
            result.Key = Hash(Encoding.UTF8.GetBytes(String.Join("\n", structure.ToArray())));
            // Rank by a hash of the relative name, independently of enumeration order.
            List<string> ranked = new List<string>(result.files.Keys);
            Dictionary<string,string> ranks=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string name in ranked)ranks[name]=Hash(Encoding.UTF8.GetBytes(name));
            ranked.Sort(delegate(string a, string b) { int c = String.CompareOrdinal(ranks[a],ranks[b]); return c != 0 ? c : String.CompareOrdinal(a,b); });
            foreach (string relative in ranked.GetRange(0, Math.Min(MaximumSamples, ranked.Count)))
            {
                Check(verify, cancel);
                try
                {
                    using (Stream stream = media.OpenRead(Path.Combine(full, relative.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        long length = result.files[relative];
                        if (stream.Length != length) throw new IOException("文件长度变化");
                        List<byte> data = new List<byte>();
                        foreach (long offset in new long[] { 0, Math.Max(0, (length - SampleBytes) / 2), Math.Max(0, length - SampleBytes) })
                        {
                            Check(verify, cancel); stream.Position = offset;
                            int wanted = (int)Math.Min(SampleBytes, length - offset); byte[] part = new byte[wanted]; int read = 0;
                            while (read < wanted) { Check(verify, cancel); int n = stream.Read(part,read,wanted-read); if(n==0) throw new EndOfStreamException(); read += n; }
                            data.AddRange(BitConverter.GetBytes(offset)); data.AddRange(part);
                        }
                        if(stream.Length != length) throw new IOException("文件长度变化");
                        result.samples[relative] = Hash(data.ToArray());
                    }
                }
                catch(IOException) { result.Complete = false; result.errors++; }
                catch(UnauthorizedAccessException) { result.Complete = false; result.errors++; }
            }
            Check(verify, cancel);
            result.Evidence = "可见文件 " + result.files.Count + "；内容抽样 " + result.samples.Count + "（最多12文件，每文件头/中/尾各2KiB，总计不超过72KiB）。" +
                (result.Complete ? "可见结构枚举完成。" : "识别不完整：枚举/读取失败或达到20万条上限；不妨碍继续抢救。") + " 错误 " + result.errors + "。仅为疑似同盘线索，不代表全盘全字节相同；匹配后仍须用户确认。";
            return result;
        }

        public List<DiscMatch> Find(string targetRoot, string currentId, string volumeLabel, CancellationToken cancel)
        {
            List<DiscMatch> result = new List<DiscMatch>();
            try
            {
                string root = SafeRoot(targetRoot), index = SafeChild(root,IndexName);
                HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ReadBucket(index,"k-"+Key,candidates,cancel);
                foreach(KeyValuePair<string,string> sample in samples) ReadBucket(index,"s-"+SampleKey(sample.Key,files[sample.Key],sample.Value),candidates,cancel);
                foreach(string candidateId in candidates)
                {
                    string id=candidateId; cancel.ThrowIfCancellationRequested(); if(id==currentId || !ValidId(id)) continue;
                    string job = SafeChild(SafeChild(root,"DiscArchive"),id);
                    Dictionary<string,string> meta = ReadMetadata(job);
                    if(meta==null || IsObservation(meta)) continue;
                    string canonical = Value(meta,"CanonicalId"); if(ValidId(canonical)) id = canonical;
                    if(id==currentId) continue;
                    string record=SafeChild(SafeChild(index,"records"),id+".txt");
                    string[] lines=ReadLines(record,256,128*1024); if(lines==null || lines.Length<3) continue;
                    bool exact=lines[0]==Key, conflict=false; int overlap=0;
                    if(Complete && lines[1]=="1" && !exact)continue;
                    for(int i=3;i<lines.Length;i++)
                    {
                        string[] p=lines[i].Split('\t'); long length;
                        if(p.Length!=3 || !Int64.TryParse(p[1],out length)) continue;
                        string path=Decode(p[0]), hash;
                        if(samples.TryGetValue(path,out hash)) { if(files[path]!=length || hash!=p[2]) conflict=true; else overlap++; }
                    }
                    if(conflict || overlap==0) continue;
                    string canonicalJob=SafeChild(SafeChild(root,"DiscArchive"),id);
                    Dictionary<string,string> canonicalMeta=ReadMetadata(canonicalJob); if(canonicalMeta==null || IsObservation(canonicalMeta))continue;
                    Add(result,new DiscMatch { Id=id, JobDir=canonicalJob, Status=ReadStatus(canonicalJob,id,canonicalMeta), Project=Value(canonicalMeta,"Project"), Strong=exact && lines[1]=="1" && Complete,
                        Reason=(exact?"可见相对路径/长度摘要一致；":"可读部分存在重叠；")+overlap+" 个文件内容抽样一致，未发现重叠抽样冲突。仍需确认原盘。" });
                }
                DiscoverLegacy(root,index,currentId,volumeLabel,result,cancel);
            }
            catch(IOException ex) { Evidence += " 历史识别受限："+ex.Message; }
            catch(UnauthorizedAccessException ex) { Evidence += " 历史识别受限："+ex.Message; }
            catch(FormatException) { Evidence += " 历史索引损坏，无法作为可靠证据；可按原盘号人工重试。"; }
            if(result.Count==0) Evidence += " 未找到有足够线索的候选，不表示从未归档；可在登记册按原盘号重试。";
            return result;
        }

        public void Remember(string targetRoot, string canonicalId)
        {
            if(!ValidId(canonicalId)) throw new ArgumentException("盘号无效。");
            string root=SafeRoot(targetRoot), job=SafeChild(SafeChild(root,"DiscArchive"),canonicalId);
            Dictionary<string,string> meta=ReadMetadata(job);
            if(meta==null || IsObservation(meta)) return;
            string linked=Value(meta,"CanonicalId"); if(ValidId(linked)) { canonicalId=linked; job=SafeChild(SafeChild(root,"DiscArchive"),canonicalId); meta=ReadMetadata(job); if(meta==null || IsObservation(meta))return; }
            string index=SafeChild(root,IndexName); Directory.CreateDirectory(index);
            using(FileStream gate=Acquire(SafeChild(index,"write.lock")))
            {
                string recordPath=SafeChild(SafeChild(index,"records"),canonicalId+".txt");
                string[] previous=ReadLines(recordPath,256,128*1024);
                if(!Complete && previous!=null && previous.Length>=4 && previous[1]=="1")return;
                StringBuilder record=new StringBuilder(Key+"\n"+(Complete?"1":"0")+"\n"+Encode(Evidence)+"\n");
                foreach(KeyValuePair<string,string> p in samples) record.Append(Encode(p.Key)).Append('\t').Append(files[p.Key].ToString(CultureInfo.InvariantCulture)).Append('\t').Append(p.Value).Append('\n');
                Atomic(recordPath,record.ToString());
                Bucket(index,"k-"+Key,canonicalId);
                foreach(KeyValuePair<string,string> p in samples) Bucket(index,"s-"+SampleKey(p.Key,files[p.Key],p.Value),canonicalId);
            }
        }

        public bool ConflictsWith(string targetRoot,string canonicalId)
        {
            if(!Complete || samples.Count==0 || !ValidId(canonicalId))return false;
            try {
                string root=SafeRoot(targetRoot);
                string path=SafeChild(SafeChild(SafeChild(root,IndexName),"records"),canonicalId+".txt");
                string[] lines=ReadLines(path,256,128*1024);
                if(lines==null || lines.Length<4 || lines[1]!="1")return false;
                if(lines[0]!=Key)return true;
                for(int i=3;i<lines.Length;i++) {string[] p=lines[i].Split('\t'); string hash; long length;
                    if(p.Length==3 && Int64.TryParse(p[1],out length) && samples.TryGetValue(Decode(p[0]),out hash) && (files[Decode(p[0])]!=length || hash!=p[2]))return true;
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(FormatException) {}
            return false;
        }
        private static string ReadStatus(string job,string id,Dictionary<string,string> meta)
        {
            string status=Value(meta,"Status"), path=SafeChild(job,"续导结果.xml");
            if(!File.Exists(path) || new FileInfo(path).Length>65536)return status;
            try { XmlReaderSettings settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536};
                using(XmlReader r=XmlReader.Create(path,settings)) {r.MoveToContent();if(r.Name=="continuation" && r.GetAttribute("canonical")==id && !String.IsNullOrEmpty(r.GetAttribute("status")))status=r.GetAttribute("status");}
            } catch(XmlException) {} catch(IOException) {}
            return status;
        }
        private void DiscoverLegacy(string root,string index,string currentId,string label,List<DiscMatch> result,CancellationToken cancel)
        {
            string archive=SafeChild(root,"DiscArchive"); if(!Directory.Exists(archive))return;
            Directory.CreateDirectory(index);
            int position=0,total=0;
            using(FileStream gate=Acquire(SafeChild(index,"legacy.lock")))
            {
                string inventory=SafeChild(index,"legacy-inventory.txt"), cursor=SafeChild(index,"legacy-cursor.txt");
                string[] names=ReadLines(inventory,50000,16*1024*1024);
                string[] saved=ReadLines(cursor,2,128); if(saved!=null && saved.Length>0)Int32.TryParse(saved[0],out position);
                // Refresh directory names once per complete round, not all old reports on every insertion.
                if(names==null || position>=names.Length)
                {
                    List<string> snapshot=new List<string>();
                    foreach(string path in Directory.EnumerateDirectories(archive)) {cancel.ThrowIfCancellationRequested();if(snapshot.Count>=50000)break;string id=Path.GetFileName(path);if(ValidId(id))snapshot.Add(id);}
                    snapshot.Sort(StringComparer.OrdinalIgnoreCase);names=snapshot.ToArray();position=0;
                    Atomic(inventory,String.Join("\n",names));
                }
                total=names.Length;int inspected=0;
                while(position<names.Length && inspected++<64)
                {
                    cancel.ThrowIfCancellationRequested();string id=names[position++];
                    try
                    {
                        string job=SafeChild(archive,id),cache=SafeChild(SafeChild(index,"legacy-v2"),id+".txt");
                        if(File.Exists(SafeChild(SafeChild(index,"records"),id+".txt")))continue;
                        string[] entries=ReadLines(cache,4100,4*1024*1024);
                        if(entries==null)
                        {
                            Dictionary<string,string> meta=ReadMetadata(job);
                            List<string> legacy=new List<string>();
                            if(meta!=null && !IsObservation(meta))
                            {
                                legacy.Add(Encode(Value(meta,"DiscLabel")));legacy.Add(Encode(Value(meta,"CanonicalId")));
                                string[] manifest=ReadLines(SafeChild(job,"校验清单.tsv"),4096,4*1024*1024);
                                if(manifest==null)manifest=ReadLines(SafeChild(job,"未完成-校验清单.tsv"),4096,4*1024*1024);
                                if(manifest!=null)foreach(string line in manifest) {string[] parts=line.Split('\t');long len;if(parts.Length>=3 && parts[0]=="F" && Int64.TryParse(parts[2],out len))legacy.Add(Encode(Unescape(parts[1]).Replace('\\','/'))+"\t"+len.ToString(CultureInfo.InvariantCulture));}
                                if(manifest==null)ReadRescuePaths(job,legacy,cancel);
                            }
                            entries=legacy.ToArray();Atomic(cache,String.Join("\n",entries));
                        }
                        // Only eight substantial paths per old disc are indexed. No old material is opened.
                        List<KeyValuePair<string,long>> material=LegacyMaterial(entries);
                        for(int n=0;n<Math.Min(8,material.Count);n++)Bucket(index,"l-"+PathKey(material[n].Key,material[n].Value),id);
                    }catch(IOException){}catch(UnauthorizedAccessException){}catch(FormatException){}
                }
                Atomic(cursor,position.ToString(CultureInfo.InvariantCulture));
            }
            HashSet<string> candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string,long>> current=new List<KeyValuePair<string,long>>();
            foreach(KeyValuePair<string,long> item in files)if(Material(item.Key,item.Value))current.Add(item);
            SortMaterial(current);
            for(int n=0;n<Math.Min(256,current.Count);n++)ReadBucket(index,"l-"+PathKey(current[n].Key,current[n].Value),candidates,cancel);
            List<DiscMatch> weak=new List<DiscMatch>();
            foreach(string id in candidates)
            {
                cancel.ThrowIfCancellationRequested();if(id==currentId)continue;
                if(weak.Count>=32)break;
                try {
                    if(File.Exists(SafeChild(SafeChild(index,"records"),id+".txt")))continue;
                    string job=SafeChild(archive,id);Dictionary<string,string> meta=ReadMetadata(job);if(meta==null || IsObservation(meta))continue;
                    string[] entries=ReadLines(SafeChild(SafeChild(index,"legacy-v2"),id+".txt"),4100,4*1024*1024);if(entries==null || entries.Length<2)continue;
                    int overlap=0,conflict=0;long bytes=0;
                    foreach(KeyValuePair<string,long> item in LegacyMaterial(entries)) {long actual;if(files.TryGetValue(item.Key,out actual)) {if(actual==item.Value){overlap++;bytes+=actual;}else conflict++;}}
                    if(conflict>0 || bytes<65536 || overlap==0)continue;
                    bool sameLabel=!String.IsNullOrWhiteSpace(label) && String.Equals(label,Value(meta,"DiscLabel"),StringComparison.OrdinalIgnoreCase);
                    weak.Add(new DiscMatch {Id=id,JobDir=job,Status=ReadStatus(job,id,meta),Project=Value(meta,"Project"),Strong=false,
                        Reason=(sameLabel?"卷标相同，仅作佐证；":"")+"旧版清单中 "+overlap+" 个有效素材路径/长度重叠，共 "+bytes+" 字节；未校验历史内容，须核对照片、盘号和分盘编号。"});
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(FormatException){}
            }
            weak.Sort(delegate(DiscMatch a,DiscMatch b){return b.Reason.StartsWith("卷标相同",StringComparison.Ordinal).CompareTo(a.Reason.StartsWith("卷标相同",StringComparison.Ordinal));});
            foreach(DiscMatch match in weak)Add(result,match);
            Evidence+=" 旧库增量进度 "+position+"/"+total+"：每次最多64条旧登记；目录名快照每轮刷新，上限5万盘，不遍历旧素材。每份清单最多4096行/4MiB，抢救报告最多4份；每盘索引8个有效素材路径，当前盘查询最多256个路径、合计512候选，最多提示32条弱候选。缓存/占位不作线索；未覆盖或缺清单可在登记册按原盘号直接重试，不依赖索引。";
        }
        private static string PathKey(string path,long length) {return Hash(Encoding.UTF8.GetBytes(path+"\t"+length.ToString(CultureInfo.InvariantCulture)));}
        private static bool Material(string path,long length)
        {
            if(length<=0)return false;
            string normalized=path.Replace('\\','/').ToLowerInvariant(),name=Path.GetFileName(normalized),ext=Path.GetExtension(name);
            if(name=="desktop.ini"||name=="thumbs.db"||name==".ds_store"||name=="autorun.inf"||name=="folder.ini"||name==".gitkeep"||name==".keep"||name=="readme.txt"||name=="readme.md"||name=="index.dat")return false;
            if(ext==".tmp"||ext==".temp"||ext==".cache"||ext==".lock"||ext==".lnk"||name.StartsWith("~$",StringComparison.Ordinal))return false;
            return normalized.IndexOf("/cache/",StringComparison.Ordinal)<0 && !normalized.StartsWith("cache/",StringComparison.Ordinal) && normalized.IndexOf("/temporary internet files/",StringComparison.Ordinal)<0;
        }
        private static void SortMaterial(List<KeyValuePair<string,long>> material) {material.Sort(delegate(KeyValuePair<string,long> a,KeyValuePair<string,long> b){int c=b.Value.CompareTo(a.Value);return c!=0?c:String.CompareOrdinal(a.Key,b.Key);});}
        private static List<KeyValuePair<string,long>> LegacyMaterial(string[] entries)
        {
            List<KeyValuePair<string,long>> result=new List<KeyValuePair<string,long>>();HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
            for(int i=2;i<entries.Length;i++){string[] parts=entries[i].Split('\t');long len;if(parts.Length!=2 || !Int64.TryParse(parts[1],out len))continue;string path=Decode(parts[0]);if(Material(path,len)&&seen.Add(path))result.Add(new KeyValuePair<string,long>(path,len));}
            SortMaterial(result);return result;
        }
        private static void Add(List<DiscMatch> result,DiscMatch item) { if(!result.Exists(delegate(DiscMatch m){return m.Id==item.Id;}))result.Add(item); }
        private static void ReadRescuePaths(string job,List<string> legacy,CancellationToken cancel)
        {
            Queue<string> dirs=new Queue<string>(); dirs.Enqueue(job); int visited=0, reports=0;
            while(dirs.Count>0 && visited++<32 && reports<4)
            {
                cancel.ThrowIfCancellationRequested(); string dir=dirs.Dequeue();
                string[] lines=ReadLines(SafeChild(dir,"路径清单.tsv"),4096,4*1024*1024);
                if(lines!=null) {reports++; foreach(string line in lines) {string[] p=line.Split('\t'); if(p.Length<4 || p[0]!="文件")continue;
                    string source=Unescape(p[1]); string desc=Unescape(p[3]); long len;
                    if(!desc.StartsWith("完整 ",StringComparison.Ordinal) || !desc.EndsWith(" 字节",StringComparison.Ordinal) || !Int64.TryParse(desc.Substring(3,desc.Length-6),out len))continue;
                    // Historical source is only a label to compare; it is never opened.
                    if(source.Length>=3 && source[1]==':' && (source[2]=='\\'||source[2]=='/'))source=source.Substring(3);
                    else continue;
                    legacy.Add(Encode(source.Replace('\\','/'))+"\t"+len.ToString(CultureInfo.InvariantCulture));
                }}
                int children=0; foreach(string child in Directory.EnumerateDirectories(dir)) {if(++children>32)break; string name=Path.GetFileName(child); if(name.StartsWith("抢救",StringComparison.Ordinal)||name.StartsWith("续导",StringComparison.Ordinal)||name.StartsWith("attempt",StringComparison.OrdinalIgnoreCase))dirs.Enqueue(SafeChild(dir,name));}
            }
        }
        private static string Value(Dictionary<string,string> m,string key) {string value;return m.TryGetValue(key,out value)?value:"";}
        private static bool IsObservation(Dictionary<string,string> m) { return (!String.IsNullOrEmpty(Value(m,"CanonicalId")) && Value(m,"CanonicalId")!=Value(m,"Id")) || Value(m,"ObservationOnly")=="true" || Value(m,"RecordKind")=="Observation" || Value(m,"Status").IndexOf("观察",StringComparison.Ordinal)>=0; }
        private static Dictionary<string,string> ReadMetadata(string job)
        {
            string file=SafeChild(job,"entry.xml"); if(!File.Exists(file))return null;
            if(new FileInfo(file).Length>1024*1024)return null;
            Dictionary<string,string> result=new Dictionary<string,string>(StringComparer.Ordinal);
            XmlReaderSettings settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=1024*1024 };
            try { XmlDocument doc=new XmlDocument(); doc.XmlResolver=null; using(XmlReader reader=XmlReader.Create(file,settings))doc.Load(reader);
                XmlNode entry=doc.SelectSingleNode("/ArrayOfEntry/Entry"); if(entry==null)entry=doc.SelectSingleNode("/Entry"); if(entry==null)return null;
                foreach(XmlNode child in entry.ChildNodes)if(child.NodeType==XmlNodeType.Element)result[child.Name]=child.InnerText;
                return Value(result,"Id")==Path.GetFileName(job)?result:null;
            } catch(XmlException) {return null;}
        }
        private static void ReadBucket(string index,string key,HashSet<string> ids,CancellationToken cancel)
        {
            if(String.IsNullOrEmpty(key)||ids.Count>=512)return; string bucket=SafeChild(SafeChild(index,"buckets"),key);
            if(!Directory.Exists(bucket))return; int count=0;
            foreach(string file in Directory.EnumerateFiles(bucket,"*.ref")) { cancel.ThrowIfCancellationRequested(); if(++count>512 || ids.Count>=512)break; string id=Path.GetFileNameWithoutExtension(file); if(ValidId(id))ids.Add(id); }
        }
        private static void Bucket(string index,string key,string id) { string path=SafeChild(SafeChild(SafeChild(index,"buckets"),key),id+".ref"); Atomic(path,""); }
        private static string SampleKey(string path,long length,string hash) {return Hash(Encoding.UTF8.GetBytes(path+"\t"+length.ToString(CultureInfo.InvariantCulture)+"\t"+hash));}
        private static string[] ReadLines(string path,int maxLines,int maxBytes) { if(!File.Exists(path)) {string recovery=path+".recover";if(!File.Exists(recovery))return null;path=recovery;} if(new FileInfo(path).Length>maxBytes)return null; List<string> lines=new List<string>(); using(StreamReader r=new StreamReader(path,Encoding.UTF8,true)) {string s;while(lines.Count<maxLines && (s=r.ReadLine())!=null)lines.Add(s);} return lines.ToArray(); }
        private static FileStream Acquire(string path) { for(int i=0;;i++)try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){if(i>=100)throw;Thread.Sleep(20);} }
        private static void Atomic(string path,string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Ref buckets are immutable; avoid replacing existing files on exFAT/SMB.
            if(File.Exists(path) && new FileInfo(path).Length<16*1024*1024 && File.ReadAllText(path,Encoding.UTF8)==text)return;
            string temp=path+".tmp-"+Guid.NewGuid().ToString("N");
            try {
                using(FileStream output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {byte[] bytes=new UTF8Encoding(false).GetBytes(text);output.Write(bytes,0,bytes.Length);output.Flush(true);}
                if(File.Exists(path))
                {
                    try {ReplaceFile(temp,path);}
                    catch(IOException ex) {
                        int code=System.Runtime.InteropServices.Marshal.GetHRForException(ex)&65535;
                        if(code!=1 && code!=50)throw;
                        // Caller holds the corresponding process-wide file lock. Keep the last
                        // complete version at a fixed recovery name across a non-atomic rename gap.
                        string recovery=path+".recover";
                        if(File.Exists(recovery))File.Delete(recovery);
                        File.Move(path,recovery);
                        try {File.Move(temp,path);}catch {if(!File.Exists(path)&&File.Exists(recovery))File.Copy(recovery,path);throw;}
                    }
                }
                else {try {File.Move(temp,path);}catch(IOException){if(!File.Exists(path))throw;}}
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        private static string SafeRoot(string root) { string full=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); if(full.EndsWith(":"))full+=Path.DirectorySeparatorChar; CheckLinks(full);return full; }
        private static string SafeChild(string parent,string name) { if(String.IsNullOrEmpty(name)||name=="."||name==".."||name.IndexOfAny(new char[]{'\\','/',':'})>=0)throw new IOException("索引路径无效。"); string path=Path.Combine(parent,name);CheckLinks(path);return path; }
        private static void CheckLinks(string path) { for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)) {try{if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("识别索引拒绝目录链接。");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}} }
        private static bool ValidId(string id) {return !String.IsNullOrWhiteSpace(id)&&id!="."&&id!=".."&&id.IndexOfAny(Path.GetInvalidFileNameChars())<0&&id.IndexOfAny(new char[]{'\\','/',':'})<0&&!id.Contains(".pending-")&&id.Length<160;}
        private static string Relative(string root,string path) {return path.Substring(root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Replace('\\','/');}
        private static string Hash(byte[] bytes) {using(SHA256 sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        private static string Encode(string s) {return Convert.ToBase64String(Encoding.UTF8.GetBytes(s??""));}
        private static string Decode(string s) {return Encoding.UTF8.GetString(Convert.FromBase64String(s));}
        private static string Unescape(string s) {StringBuilder b=new StringBuilder();for(int i=0;i<s.Length;i++){if(s[i]=='\\'&&i+1<s.Length){char c=s[++i];b.Append(c=='t'?'\t':c=='n'?'\n':c=='r'?'\r':c);}else b.Append(s[i]);}return b.ToString();}
        private static void Check(Action verify,CancellationToken cancel) {cancel.ThrowIfCancellationRequested();if(verify!=null) { try {verify();} catch(OperationCanceledException) {throw;} catch(Exception ex) {throw new InvalidOperationException("介质身份校验失败，停止识别。",ex);} }cancel.ThrowIfCancellationRequested();}
    }
}
