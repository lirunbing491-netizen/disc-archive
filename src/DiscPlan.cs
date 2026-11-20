using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiscArchive
{
    internal sealed class DiscPlan
    {
        public string Folder, Reason;
        // Keys are original top-level names; values are safe destination project folders.
        public Dictionary<string,string> TopFolders = new Dictionary<string,string>(ArchivePath.Comparer);
        private sealed class Hint { public DateTime? Day; public string Name; public string Source; }
        private sealed class Sample { public string Path; public DateTime Modified; }
        private sealed class Evidence
        {
            public List<DateTime> Raw = new List<DateTime>(), Exif = new List<DateTime>(), Names = new List<DateTime>();
            public List<DateTime> RawDigitized = new List<DateTime>(), ExifDigitized = new List<DateTime>();
            public List<Sample> Files = new List<Sample>(); public int Errors, Dirs, Heads, CacheDirs; public bool Limited, Early;
        }
        private static readonly Regex DatePattern = new Regex(@"(?<!\d)(?:(?<y>19\d{2}|20\d{2})[-_.年 /]?(?<m>0?[1-9]|1[0-2])[-_.月 /]?(?<d>0?[1-9]|[12]\d|3[01])日?|(?<ys>\d{2})[-_.年 /](?<ms>0?[1-9]|1[0-2])[-_.月 /](?<ds>0?[1-9]|[12]\d|3[01])日?|(?<s>\d{6}))(?!\d)", RegexOptions.Compiled);
        private static readonly Regex Generic = new Regex(@"^(?:raw|jpg|jpeg|tif|tiff|psd|psb|capture\s*one|captureone|capture|output|selects|trash|session|sessions|images?|photos?|dcim|backup|new\s*folder|untitled|disc|dvd(?:[\s-]*r[wm]?)?|cd(?:[\s-]*r[wm]?)?|data|img|dsc|test|素材|照片|图片|新建文件夹|原片|成片|备份|导出|拍摄|测试|未命名|光盘|新卷|无标题)(?:[\s_\-\d]*)$",RegexOptions.IgnoreCase);
        public static DiscPlan Decide(string root,string volumeLabel,string photoText,string userProject,string discId,DateTime importDay,CancellationToken cancel,DiscSource media = null)
        {
            media = media ?? DiscSource.Windows;
            cancel.ThrowIfCancellationRequested();
            DiscPlan p = new DiscPlan(); List<string> reasons = new List<string>();
            string id = Safe(String.IsNullOrWhiteSpace(discId) ? "无盘号" : discId);
            Hint user = Parse(userProject,"用户显式项目",true), volume = Parse(volumeLabel,"卷标",false), photo = Parse(photoText,"盘面文字",false);
            List<KeyValuePair<string,Hint>> top = new List<KeyValuePair<string,Hint>>();
            try { foreach(string d in Directories(media,root).Take(129)) { cancel.ThrowIfCancellationRequested(); if(top.Count==128) { reasons.Add("顶层目录仅检查前128项"); break; } top.Add(new KeyValuePair<string,Hint>(Path.GetFileName(d),Parse(Path.GetFileName(d),"顶层目录",false))); } }
            catch(OperationCanceledException) { throw; } catch { reasons.Add("部分顶层目录不可读"); }
            List<KeyValuePair<string,Hint>> strong = top.Where(x=>x.Value.Day.HasValue && x.Value.Name!=null).ToList();
            var projects = strong.Select(x=>x.Value.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool volumeConflict=volume.Day.HasValue && volume.Name!=null && strong.Any(x=>!String.Equals(x.Value.Name,volume.Name,StringComparison.OrdinalIgnoreCase));
            if((projects.Count>1 || volumeConflict) && user.Name==null)
            {
                p.Folder = importDay.ToString("yyMMdd")+"-混装待归属-"+id;
                foreach(var t in strong) p.TopFolders[t.Key]=Key(t.Value);
                reasons.Add("卷标或顶层目录明确出现不同项目名称，按有明确日期项目的目录分组；卷标不能证明根文件归属，其余条目保留到带盘号的混装待归属文件夹");
                p.Reason=String.Join("；",reasons); return p;
            }
            Hint chosen = user.Name!=null ? user : (volume.Name!=null ? volume : (strong.Count>0 ? strong[0].Value : photo));
            string name = chosen.Name;
            if(name==null && !String.IsNullOrWhiteSpace(photoText))
            {
                string candidate=photoText.Split(new[]{'\n','\r'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if(candidate!=null && candidate.Length<=44 && !Regex.IsMatch(candidate,@"[?？]|不清|无法|不详|未知|疑似|可能|unclear|unknown|mitsubishi|verbatim|dvd|speed|4\.7|错字",RegexOptions.IgnoreCase))
                {
                    string clue=Parse(candidate,"卷标",false).Name;
                    if(clue!=null) { name=clue+"-"+id; reasons.Add("仅盘面有可读名称，作为单盘描述保留，避免误并同名项目"); }
                }
            }
            DateTime? day = user.Day ?? volume.Day ?? (strong.Count>0 ? strong[0].Value.Day : null) ?? photo.Day;
            if(name!=null) reasons.Add("项目来源："+chosen.Source);
            if(chosen==volume && name!=null) reasons.Add("采用卷标可读项目线索，未核实客户身份");
            if(day.HasValue) reasons.Add("日期来自明确文字："+(user.Day.HasValue?user.Source:volume.Day.HasValue?volume.Source:strong.Count>0?"顶层目录":photo.Source));
            if(volume.Name!=null && photo.Name!=null && !String.Equals(volume.Name,photo.Name,StringComparison.OrdinalIgnoreCase)) reasons.Add("卷标与盘面文字不一致，优先卷标");
            Evidence ev = (!day.HasValue || name==null) ? ReadEvidence(root,cancel,!day.HasValue,media) : null;
            if(name==null && ev!=null)
            {
                var namedDirs=top.Where(x=>Parse(x.Key,"卷标",false).Name!=null).Select(x=>x.Key).ToArray();
                string material=null;
                if(namedDirs.Length==1) { material=Safe(namedDirs[0]); reasons.Add("采用原目录文字作为描述，项目归属未确认"); }
                else if(ev.Files.Any(x=>IsRaw(x.Path))) material="RAW原档";
                else if(ev.Files.Any(x=>Regex.IsMatch(Path.GetExtension(x.Path),@"^\.jpe?g$",RegexOptions.IgnoreCase))) material="JPG照片";
                else if(ev.Files.Any(x=>IsImage(x.Path))) material="影像素材";
                else if(ev.Files.Any(x=>Video(x.Path))) material="视频素材";
                else if(ev.Files.Any(x=>Regex.IsMatch(Path.GetExtension(x.Path),@"^\.(psd|psb|ai|indd)$",RegexOptions.IgnoreCase))) material="设计文件";
                if(material!=null) { name=material+"-"+id; reasons.Add("采用可读描述与盘号，未据此认定客户"); }
            }
            // Scan even when text supplies a name: capture dates are needed when no date is stated.
            if(!day.HasValue)
            {
                List<DateTime> dates = ev.Raw.Count>0 ? ev.Raw : ev.Exif;
                if(dates.Count>0)
                {
                    day=Representative(dates);
                    reasons.Add((ev.Raw.Count>0?"相机原档拍摄元数据":"照片EXIF拍摄元数据")+"："+day.Value.ToString("yyyy-MM-dd"));
                    reasons.Add("采样多数日优先，同票取最早日（"+Distribution(dates)+"），未据此分拆项目");
                    if(dates.Select(x=>x.Date).Distinct().Count()>1) reasons.Add("采样拍摄日期有分歧");
                }
                else if(ev.RawDigitized.Count>0 || ev.ExifDigitized.Count>0)
                {
                    List<DateTime> digitized=ev.RawDigitized.Count>0?ev.RawDigitized:ev.ExifDigitized;
                    day=Representative(digitized);
                    reasons.Add("弱来源：EXIF数字化日期（0x9004），未找到真实拍摄日期，仅作归档日期，不能视为拍摄日期；多数日优先，同票取最早日（"+Distribution(digitized)+"）");
                }
                else if(ev.Names.Count>0) { day=Representative(ev.Names); reasons.Add("弱来源：可识别文件名日期，未找到拍摄元数据；多数日优先，同票取最早日（"+Distribution(ev.Names)+"）"); }
                else {
                    var valid=ev.Files.Where(x=>ValidDate(x.Modified)).ToList();
                    var preferred=valid.Where(x=>IsRaw(x.Path)).ToList();
                    if(preferred.Count==0) preferred=valid.Where(x=>IsImage(x.Path)).ToList();
                    if(preferred.Count==0) preferred=valid.Where(x=>OtherMaterial(x.Path)).ToList();
                    if(preferred.Count>0) {
                        var modified=preferred.Select(x=>x.Modified).ToList();
                        day=Representative(modified);
                        reasons.Add("弱来源：原素材文件修改日期，多数日优先，同票取最早日（"+Distribution(modified)+"），仅作归档日期，不能视为拍摄日期");
                    }
                }
                if(ev.Errors>0) reasons.Add("采样遇到"+ev.Errors+"处读取失败，已跳过");
                reasons.Add("有界采样：检查"+ev.Dirs+"目录、"+ev.Files.Count+"文件、定位读取"+ev.Heads+"个影像元数据；每个最多256KiB（按IFD偏移定位，不读取像素），总计最多12个文件"+(ev.Early?"；跨目录优先采样，多张拍摄日期一致后提前结束":""));
                if(ev.Limited) reasons.Add("采样未覆盖全部文件");
                if(ev.CacheDirs>0) reasons.Add("已排除"+ev.CacheDirs+"个缓存/缩略图/设置目录，防止软件缓存日期影响归档");
            }
            if(day.HasValue) p.Folder=day.Value.ToString("yyMMdd")+"-"+(name??("项目未知-"+id));
            else {
                string description=name ?? id;
                if(name!=null && !name.EndsWith("-"+id,StringComparison.OrdinalIgnoreCase)) description+="-"+id;
                p.Folder=importDay.ToString("yyMMdd")+"-拍摄日未知-"+description;
            }
            p.Folder=Safe(p.Folder); if(name==null) reasons.Add("无可靠项目名称，保留盘号防止误合并");
            if(!day.HasValue) reasons.Add("未取得原项目日期，前缀仅为导入日；盘面照片拍摄日期未参与判断");
            reasons.Add("单项目完整保留原目录结构"); p.Reason=String.Join("；",reasons); return p;
        }
        private static string Key(Hint h) { return Safe(h.Day.Value.ToString("yyMMdd")+"-"+h.Name); }
        private static Hint Parse(string text,string source,bool explicitName)
        {
            Hint h=new Hint(); h.Source=source; if(String.IsNullOrWhiteSpace(text)) return h;
            string s=text.Trim(); Match m=DatePattern.Match(s); if(m.Success) { h.Day=MatchDate(m); if(h.Day.HasValue) s=s.Remove(m.Index,m.Length); }
            bool marked=Regex.IsMatch(s,@"(?:项目|客户|品牌)\s*[:：=]");
            if(marked) s=Regex.Replace(s,@"^.*?(?:项目|客户|品牌)\s*[:：=]","");
            s=s.Trim(' ','\t','\r','\n','-','_','.',':','：','/','\\');
            // Newlines separate OCR fields, not a freely generated path.
            if(s.IndexOfAny(new char[]{'\r','\n'})>=0) s=s.Split(new char[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            bool plausible=s.Length>=2 && (explicitName || s.Length<=100) && Regex.IsMatch(s,@"[\p{L}]") && !Generic.IsMatch(s) && !Regex.IsMatch(s,@"^[\d\s\-_./]+$");
            bool shortVolume=source=="卷标" && Regex.IsMatch(s,@"^[\p{L}][\p{L}\p{N} &\x27\-（）()]{1,79}$");
            if(plausible && (explicitName || marked || h.Day.HasValue || shortVolume)) h.Name=Safe(s);
            return h;
        }
        private static DateTime? MatchDate(Match m)
        {
            int y,mo,d; string six=m.Groups["s"].Value;
            if(six.Length==6) { y=2000+Int32.Parse(six.Substring(0,2)); if(y>2049)y-=100; mo=Int32.Parse(six.Substring(2,2)); d=Int32.Parse(six.Substring(4,2)); }
            else if(m.Groups["ys"].Success) { y=2000+Int32.Parse(m.Groups["ys"].Value); if(y>2049)y-=100; mo=Int32.Parse(m.Groups["ms"].Value); d=Int32.Parse(m.Groups["ds"].Value); }
            else { y=Int32.Parse(m.Groups["y"].Value); mo=Int32.Parse(m.Groups["m"].Value); d=Int32.Parse(m.Groups["d"].Value); }
            try { return new DateTime(y,mo,d); } catch { return null; }
        }
        internal static string Safe(string value)
        {
            string s=Regex.Replace(value??"",@"[<>:""/\\|?*\p{Cc}]","_").Trim().TrimEnd('.',' ');
            if(s.Length==0)s="未命名"; if(Regex.IsMatch(s,@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9]|LPT[0-9])(?:\.|$)",RegexOptions.IgnoreCase))s="_"+s;
            if(s.Length>110)s=s.Substring(0,94).TrimEnd('.',' ')+"-"+Hash(s); return s;
        }
        private static string Hash(string s) { unchecked { uint n=2166136261; foreach(char c in s)n=(n^c)*16777619; return n.ToString("x8"); } }
        private static bool IsRaw(string path) { return Regex.IsMatch(Path.GetExtension(path),@"^\.(iiq|cr2|cr3|nef|arw|dng|orf|rw2|raw)$",RegexOptions.IgnoreCase); }
        private static bool IsImage(string path) { return IsRaw(path)||Regex.IsMatch(Path.GetExtension(path),@"^\.(jpg|jpeg|tif|tiff)$",RegexOptions.IgnoreCase); }
        private static bool Video(string path) { return Regex.IsMatch(Path.GetExtension(path),@"^\.(mov|mp4|mts|m2ts|mxf|avi|vob|mpg|mpeg|wmv|mkv)$",RegexOptions.IgnoreCase); }
        private static bool OtherMaterial(string path) { return Video(path)||Regex.IsMatch(Path.GetExtension(path),@"^\.(psd|psb|ai|indd|pdf|aif|aiff|wav|flac|mp3|docx?|xlsx?|pptx?|png|bmp|zip|7z)$",RegexOptions.IgnoreCase); }
        private static DateTime Representative(List<DateTime> dates) { return dates.GroupBy(x=>x.Date).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key).First().Key; }
        private static string Distribution(List<DateTime> dates) { return String.Join("、",dates.GroupBy(x=>x.Date).OrderBy(x=>x.Key).Take(8).Select(x=>x.Key.ToString("yyyy-MM-dd")+"×"+x.Count())); }
        private static bool ValidDate(DateTime date) { return date.Year>=1981 && date.Date<=DateTime.Today.AddDays(2); }
        private static Evidence ReadEvidence(string root,CancellationToken cancel,bool readHeads,DiscSource media)
        {
            Evidence e=new Evidence(); Queue<string> dirs=new Queue<string>(); dirs.Enqueue(root);
            while(dirs.Count>0 && e.Dirs<64 && e.Files.Count<256)
            {
                cancel.ThrowIfCancellationRequested(); string dir=dirs.Dequeue(); e.Dirs++;
                int local=0;
                try
                {
                    foreach(string entry in media.Entries(dir))
                    {
                        cancel.ThrowIfCancellationRequested();
                        try
                        {
                            FileAttributes attributes=media.Attributes(entry);
                            if((attributes&FileAttributes.ReparsePoint)!=0) { e.Errors++; continue; }
                            if((attributes&FileAttributes.Directory)!=0)
                            {
                                if(Regex.IsMatch(Path.GetFileName(entry),@"^(Cache|Thumbnails|Proxies|Settings\d*)$",RegexOptions.IgnoreCase)) {e.CacheDirs++;continue;}
                                if(dirs.Count+e.Dirs>=64) { e.Limited=true; continue; }
                                dirs.Enqueue(entry);
                            }
                            else
                            {
                                if(e.Files.Count>=256 || local>=32) { e.Limited=true; continue; }
                                local++; e.Files.Add(new Sample { Path=entry,Modified=media.Modified(entry) });
                            }
                        }
                        catch(OperationCanceledException){throw;} catch {e.Errors++;}
                    }
                }
                catch(OperationCanceledException){throw;} catch {e.Errors++;}
            }
            if(dirs.Count>0)e.Limited=true;
            List<Sample> images=Spread(e.Files.Where(x=>IsImage(x.Path)).ToList());
            if(readHeads && images.Count>12)e.Limited=true;
            foreach(Sample f in readHeads ? images.Take(12) : Enumerable.Empty<Sample>())
            {
                cancel.ThrowIfCancellationRequested();
                if(!IsRaw(f.Path) && e.Raw.Count>0)break;
                e.Heads++;
                try {
                    DateTime? digitized;
                    DateTime? date=CameraFileDate.Read(f.Path,cancel,out digitized,media);
                    if(date.HasValue && ValidDate(date.Value)) (IsRaw(f.Path)?e.Raw:e.Exif).Add(date.Value);
                    else if(digitized.HasValue && ValidDate(digitized.Value)) (IsRaw(f.Path)?e.RawDigitized:e.ExifDigitized).Add(digitized.Value);
                } catch(OperationCanceledException){throw;}catch{e.Errors++;}
                List<DateTime> collected=e.Raw.Count>0?e.Raw:e.Exif;
                if(collected.Count>=6 && collected.Select(x=>x.Date).Distinct().Count()==1) {e.Early=true;break;}
            }
            foreach(Sample f in e.Files.Where(x=>IsImage(x.Path)||OtherMaterial(x.Path))) { Match m=DatePattern.Match(Path.GetFileNameWithoutExtension(f.Path)); if(m.Success) {DateTime? d=MatchDate(m);if(d.HasValue&&ValidDate(d.Value))e.Names.Add(d.Value);} }
            return e;
        }
        private static IEnumerable<string> Directories(DiscSource media,string root)
        { foreach(string path in media.Entries(root)) if((media.Attributes(path)&FileAttributes.Directory)!=0) yield return path; }
        private static List<Sample> Spread(List<Sample> files)
        {
            List<Sample> result=new List<Sample>();
            foreach(bool raw in new bool[]{true,false})
            {
                List<Queue<Sample>> groups=files.Where(x=>IsRaw(x.Path)==raw).GroupBy(x=>Path.GetDirectoryName(x.Path),StringComparer.OrdinalIgnoreCase).OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>new Queue<Sample>(x.OrderBy(y=>y.Path,StringComparer.OrdinalIgnoreCase))).ToList();
                while(groups.Any(x=>x.Count>0)) foreach(Queue<Sample> group in groups)if(group.Count>0)result.Add(group.Dequeue());
            }
            return result;
        }
    }
}
