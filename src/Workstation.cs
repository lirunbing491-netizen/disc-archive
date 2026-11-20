using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiscArchive
{
    // All state changes run on the UI thread. Only device reads and file copying run on a worker.
    internal interface IReader
    {
        string[] Drives();
        bool Empty(string drive);
        bool Ready(string drive);
        MountedDisc Open(string drive);
    }

    internal sealed class MountedDisc : IDisposable
    {
        internal readonly string Root, VolumeLabel;
        internal readonly DiscSource Source;
        internal readonly string Format;
        internal string UncoveredTracks;
        internal readonly Action Verify;
        private readonly IDisposable lease;
        internal MountedDisc(string root,string label,Action verify,IDisposable guard,DiscSource source=null,string format="Windows 文件视图")
        { Root=root; VolumeLabel=label; Verify=verify; lease=guard; Source=source??DiscSource.Windows; Format=format; }
        public void Dispose() { if(lease!=null) lease.Dispose(); }
    }
    internal sealed class SystemReader : IReader
    {
        public string[] Drives() { return Optical.Drives(); }
        public bool Empty(string drive) { return Optical.IsEmpty(drive); }
        private readonly Dictionary<string,OpticalView> selected=new Dictionary<string,OpticalView>(StringComparer.OrdinalIgnoreCase);
        internal void SetView(string drive,OpticalView view) {lock(selected)selected[drive]=view;}
        internal OpticalView View(string drive) {lock(selected){OpticalView v;return selected.TryGetValue(drive,out v)?v:OpticalView.Auto;}}
        public bool Ready(string drive) { return (View(drive)==OpticalView.Auto && Optical.IsReady(drive)) || RawOptical.CanRead(drive); }
        public MountedDisc Open(string drive)
        {
            MountedDisc disc=OpenView(drive);
            try {
                TrackInfo tracks=OpticalTracks.Inspect(drive);
                if(tracks.HasUncoveredTracks) disc.UncoveredTracks=((disc.UncoveredTracks??"")+" "+tracks.Note).Trim();
                disc.Verify();return disc;
            } catch {disc.Dispose();throw;}
        }
        private MountedDisc OpenView(string drive)
        {
            OpticalView view=View(drive);
            if(view==OpticalView.Auto && Optical.IsReady(drive)) {
                using(IEnumerator<string> probe=DirectoryProbe(drive)) {
                    if(probe!=null) {
                        OpticalLease lease=Optical.Acquire(drive);
                        try { var info=new DriveInfo(drive);return new MountedDisc(drive,info.VolumeLabel,lease.Verify,lease,null,info.DriveFormat+"（Windows 文件视图）"); }
                        catch { lease.Dispose(); throw; }
                    }
                }
            }
            RawOptical raw=RawOptical.Open(drive);
            return OpticalMount.Open(drive,raw.Data,raw.Verify,raw,view);
        }
        private static IEnumerator<string> DirectoryProbe(string drive)
        {
            IEnumerator<string> items=null;
            try {
                items=Directory.EnumerateFileSystemEntries(drive).GetEnumerator();
                if(items.MoveNext())return items;
                items.Dispose(); return null;
            }
            catch(IOException){if(items!=null)items.Dispose();return null;}
            catch(UnauthorizedAccessException){if(items!=null)items.Dispose();return null;}
        }
    }

    internal sealed class Workstation
    {
        internal const string Waiting = "等待放盘", Planning = "正在归类", Running = "抢救中", Done = "已拷出", Partial = "部分拷出", Paused="暂停待续", Review = "待复查";
        internal readonly Catalog Book;
        internal readonly IReader Reader;
        internal Entry Current { get; private set; }
        internal bool EmptySeen { get; private set; }
        internal bool ManualOnly { get; private set; }
        internal bool Busy { get { return cancellation != null; } }
        private CancellationTokenSource cancellation;
        private sealed class PhotoResult { internal string Text, Note; }
        private Task<PhotoResult> reading;
        private CancellationTokenSource photoCancel;
        private readonly Func<string,CancellationToken,string> recognize;
        private readonly Func<string> readMode;
        private bool retry, protectPrevious;
        internal Func<Entry,List<DiscMatch>,CancellationToken,Task<DiscMatch>> ChooseMatch;
        internal Func<Entry,string,string[],CancellationToken,Task<string>> ChooseProject;
        internal bool RecognitionFinished { get { return reading!=null && reading.IsCompleted; } }

        internal Workstation(Catalog book, IReader reader, Func<string,CancellationToken,string> recognizer=null,Func<string> mode=null,bool recoverInterrupted=true)
        {
            Book = book;
            Reader = reader;
            recognize = recognizer;
            readMode = mode ?? delegate { return "metadata"; };
            if(!recoverInterrupted) return;
            RecoverInterrupted(book);
        }

        internal static void RecoverInterrupted(Catalog book)
        {
            foreach (Entry e in book.List())
            {
                if (e.Status != Running && e.Status != Waiting && e.Status != Planning && e.Status != "等待选择" && e.Status != "复制校验中") continue;
                if(e.Status==Running && book.IsOriginalTarget(e)) RecoverCounts(e);
                e.Status = e.Files>0 ? Paused : Review;
                e.Note = "上次在登记或读取期间退出。请使用原盘号核对并补缺。 " + e.Note;
                book.Save(e);
            }
        }

        internal Entry Register(string photo, string target, string targetId, string drive, string project, bool emptyAtShutter=false,string existingProject=null)
        {
            if (Current != null) throw new InvalidOperationException("请先结束当前盘的任务。");
            if (String.IsNullOrEmpty(drive) || Array.IndexOf(Reader.Drives(), drive) < 0)
                throw new IOException("请连接并选择光驱。");
            if (!String.IsNullOrEmpty(targetId) && !Book.IsOriginalTarget(new Entry { TargetRoot = target, TargetId = targetId }))
                throw new IOException("归档硬盘已掉线或被更换。请接回原硬盘，或重新选择归档位置。");
            // Record tray state at the shutter or at intake, before the photo is written.
            bool observedEmpty = emptyAtShutter || Reader.Empty(drive);
            Entry created = Book.Create(photo, target, drive, project.Trim(), targetId);
            if(!String.IsNullOrEmpty(existingProject)) { created.ExistingProject=existingProject; Book.Save(created); }
            retry=false; protectPrevious=false; Arm(created,observedEmpty);
            return created;
        }

        internal void Retry(Entry e, string drive)
        {
            if (Current != null) throw new InvalidOperationException("当前光驱已有任务。");
            if (String.IsNullOrEmpty(drive) || Array.IndexOf(Reader.Drives(), drive) < 0) throw new IOException("请先在导盘页选择光驱。");
            RequireTarget(e);
            e=e.ForAttempt(); retry=true; protectPrevious=true;
            e.Drive = drive;
            e.Status = Waiting;
            e.Note = "等待重新读取。原有尝试记录保留。";
            Arm(e,Reader.Empty(drive));
        }

        internal Entry ResumeKnown(string root,string id,string drive)
        {
            if(Current!=null)throw new InvalidOperationException("当前光驱已有任务。");
            Entry owner=Book.LookupOriginal(root,id.Trim());
            var local=Book.List().Find(x=>x.Id==owner.Id && x.TargetId==owner.TargetId && x.RegistrationId==owner.RegistrationId && x.Created==owner.Created);
            if(local!=null && Book.IsOriginalTarget(local) && Directory.Exists(local.JobDir)) {
                Retry(local,drive); return Current;
            }
            // A different workstation or drive mapping owns the old local catalog. Keep
            // its ID and original photo and use a new local insertion receipt to access it.
            if(String.IsNullOrEmpty(drive) || Array.IndexOf(Reader.Drives(),drive)<0)throw new IOException("请选择已连接的光驱。");
            bool empty=Reader.Empty(drive);
            var e=Book.Create(Catalog.OriginalPhotoAt(root,owner),root,drive,"",owner.TargetId);
            e.CanonicalId=owner.Id; e.Note="按原盘号 "+owner.Id+" 续导。"; Book.Save(e);
            retry=true; protectPrevious=false; Arm(e,empty); return e;
        }

        private void Arm(Entry e,bool confirmedEmpty)
        {
            Current = e;
            EmptySeen = confirmedEmpty;
            ManualOnly = false;
            string mode=readMode();
            photoCancel=new CancellationTokenSource();
            CancellationToken token=photoCancel.Token;
            reading = Task.Run(delegate {
                if(mode=="metadata") return new PhotoResult {Text="",Note="仅卷标、目录和素材元数据。"};
                try {
                    token.ThrowIfCancellationRequested();
                    string path = Book.IsOriginalTarget(e) ? (e.OriginalPhoto ?? e.Photo) : e.Photo;
                    string found=(recognize(path,token) ?? "").Trim();
                    string method=mode=="model" ? "自配看图模型" : "本机文字识别";
                    return new PhotoResult {Text=found,Note=method+(found.Length==0 ? "未给出可用文字，按盘上信息归类。" : "已返回盘面候选。")};
                } catch(OperationCanceledException) { return new PhotoResult{Text="",Note="已取消盘面识别。"}; }
                  catch(Exception ex) { return new PhotoResult{Text="",Note="盘面识别未成功，按盘上信息归类。"+ex.Message}; }
            });
        }

        internal void BlockAutomatic() { if (!Busy && Current != null) ManualOnly = true; }

        internal async Task<bool> CanStart(bool explicitlyMatched)
        {
            if (Current == null || Busy) return false;
            Entry e = Current;
            bool testEmpty = !EmptySeen && !explicitlyMatched;
            if (ManualOnly && !explicitlyMatched) return false;
            int state = await Task.Run(delegate {
                if (Array.IndexOf(Reader.Drives(), e.Drive) < 0) return -1;
                if (testEmpty) return Reader.Empty(e.Drive) ? 1 : 0;
                return Reader.Ready(e.Drive) ? 2 : 0;
            });
            if (Current != e || Busy || (ManualOnly && !explicitlyMatched)) return false;
            if (state < 0)
            {
                EmptySeen = false;
                ManualOnly = true;
                return false;
            }
            if (state == 1) EmptySeen = true;
            return state == 2;
        }

        private void RequireTarget(Entry e)
        {
            if (!Book.IsOriginalTarget(e) || !Directory.Exists(e.JobDir))
                throw new IOException("归档硬盘已断开、盘符已变或任务目录不可用。");
        }

        internal async Task<Entry> Copy(Action<string> progress)
        {
            if (Busy || Current == null) throw new InvalidOperationException("没有可读取的盘。");
            Entry e = Current;
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            string attemptName = "抢救-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string attempt = Path.Combine(e.JobDir, attemptName);
            RescueResult result=null;
            DiscSession session=null;
            bool accepted=!protectPrevious;
            try
            {
                RequireTarget(e);
                e.Status=Planning; e.Note=attemptName; e.Files=e.Bytes=e.Failed=e.Warnings=e.ReusedFiles=e.ReusedBytes=0; e.Report=""; e.CloseWarning="";
                if(accepted) Book.Save(e);
                progress("光盘已就绪，正在等待盘面判读和归类。");
                MountedDisc disc=await Task.Run(delegate { return Reader.Open(e.Drive); });
                System.Runtime.ExceptionServices.ExceptionDispatchInfo transferError=null;
                try
                {
                    e.DiscLabel=disc.VolumeLabel;
                    e.SourceFormat=disc.Format;
                    e.SourceCoverage=disc.UncoveredTracks;
                    PhotoResult photo=await reading;
                    e.PhotoText=photo.Text;
                    e.VisionNote=photo.Note;
                    token.ThrowIfCancellationRequested();
                    Action verify=delegate { disc.Verify(); RequireTarget(e); };
                    progress("正在比较原盘目录和少量内容样本，查找已导或未完成的盘。");
                    DiscIdentity identity=await Task.Run(delegate {
                        var clock=System.Diagnostics.Stopwatch.StartNew(); verify();
                        var probe=DiscIdentity.Inspect(disc.Root,delegate { if(clock.ElapsedMilliseconds>=900) { verify(); clock.Restart(); } },token,disc.Source);
                        verify(); return probe;
                    });
                    string canonical=retry && !String.IsNullOrEmpty(e.CanonicalId) ? e.CanonicalId : e.Id;
                    if(!retry) session=await Task.Run(()=>new DiscSession(Book,e,e.Id));
                    List<DiscMatch> matches=await Task.Run(delegate {
                        try { return retry ? identity.Find(e.TargetRoot,"",e.DiscLabel,token) : DiscSession.Discover(identity,e,token); }
                        catch(Exception error) {
                            if(!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                            verify(); token.ThrowIfCancellationRequested();
                            identity.Evidence+=" 重复盘索引本次不可用，可按已知旧盘号核对："+error.Message;
                            return new List<DiscMatch>();
                        }
                    });
                    bool strong=false;
                    e.IdentityEvidence=identity.Evidence;
                    if(retry) strong=matches.Any(x=>x.Id==canonical && x.Strong);
                    else if(matches.Count!=0 && ChooseMatch!=null)
                    {
                        e.Status="等待选择"; Book.Save(e); progress("发现可能的旧盘，请选择原盘号或作为另一张盘导入。");
                        DiscMatch selected=await ChooseMatch(e,matches,token);
                        if(selected!=null) {
                            if(!matches.Contains(selected)) throw new IOException("所选盘号已不在候选列表。");
                            canonical=selected.Id; strong=selected.Strong;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if((retry || canonical!=e.Id) && identity.ConflictsWith(e.TargetRoot,canonical))
                        throw new IOException("这张光盘与旧记录的目录或内容抽样冲突。请核对实物，或重新拍照作为新盘登记。");
                    if(session==null || canonical!=e.Id)
                    {
                        if(session!=null) { session.Dispose(); session=null; }
                        session=await Task.Run(()=>new DiscSession(Book,e,canonical));
                    }
                    e.CanonicalId=canonical; e.LatestRunId=e.Id; accepted=true;
                    var context=session;
                    bool continuation=retry || canonical!=e.Id;
                    DiscPlan plan=continuation ? await Task.Run(()=>context.ReadPlan()) : null;
                    bool storedPlan=plan!=null;
                    if(plan==null && continuation && context.Original.ArchiveFolders!=null && context.Original.ArchiveFolders.Length==1) {
                        string originalProject=context.Original.ArchiveFolders[0];
                        ProjectPhoto.DirectoryFor(e.TargetRoot,originalProject);
                        plan=new DiscPlan {Folder=originalProject,Reason="沿用旧盘登记的项目目录："+originalProject};
                    }
                    if(plan==null) plan=await ResolvePlan(e,disc,verify,progress,token);
                    token.ThrowIfCancellationRequested();
                    await Task.Run(delegate {
                        verify(); if(!storedPlan) context.SavePlan(plan);
                        try { identity.Remember(e.TargetRoot,canonical); }
                        catch(Exception error) {
                            if(!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                            verify();
                            e.IdentityEvidence+=" 本次索引未更新，下次可使用盘号核对："+error.Message;
                        }
                    });
                    e.NamingReason=plan.Reason;
                    e.ArchiveFolders=await Task.Run(delegate {
                        verify();
                        bool fallback=plan.TopFolders.Count==0;
                        if(!fallback)
                        {
                            try { fallback=disc.Source.Entries(disc.Root).Any(p=>!plan.TopFolders.ContainsKey(Path.GetFileName(p))); }
                            catch(IOException) { fallback=true; } catch(UnauthorizedAccessException) { fallback=true; }
                        }
                        return plan.TopFolders.Values.Concat(fallback ? new[]{plan.Folder} : new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    });
                    e.Project=String.Join(" / ",e.ArchiveFolders);
                    if(e.TitleState!="已确认") { e.Title=e.Project; e.TitleState="自动命名"; }
                    e.Status=Running;
                    e.Note=attemptName; e.Report=attempt; Book.Save(e);
                    result=await Task.Run(delegate {
                        verify(); token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(attempt);
                        disc.Source.WriteDescription(attempt);
                        foreach(string folder in e.ArchiveFolders) ProjectPhoto.Store(e,folder,verify);
                        progress("归档位置已确定：" + e.Project);
                        return RescueCopy.Run(disc.Root,e.TargetRoot,plan.Folder,plan.TopFolders,canonical,attempt,progress,verify,token,context.Folder,continuation && !strong,disc.Source);
                    });
                    RequireTarget(e);
                }
                catch(Exception ex)
                {
                    transferError=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
                // The raw reader is opened by a worker. Close it on a worker as well;
                // releasing the source must not replace a read, cancel or target error.
                try { await Task.Run((Action)disc.Dispose); }
                catch(Exception ex) {
                    if(ex is OutOfMemoryException || ex is AccessViolationException || ex is ThreadAbortException) throw;
                    e.CloseWarning=CloseMessage(ex);
                    RecordCloseWarning(e,attempt);
                }
                if(transferError!=null)transferError.Throw();
                e.Status=result.Files==0 ? Review : (result.Failed==0 ? Done : Partial);
                if(e.Status==Done && !String.IsNullOrEmpty(e.SourceCoverage))e.Status=Partial;
                Counts(e,result);
                e.Note=e.Files==0 ? "没有完整文件，请查看本盘记录。" :
                    "原盘号 "+e.CanonicalId+"。已核对沿用 "+e.ReusedFiles+" 个，本轮新拷 "+(e.Files-e.ReusedFiles)+" 个，失败条目 "+e.Failed+"。";
                if(!String.IsNullOrEmpty(e.SourceCoverage))e.Note+=" "+e.SourceCoverage;
                if(!String.IsNullOrEmpty(e.SourceFormat) && e.SourceFormat.Contains("扇区只读"))e.Note+=" 读取格式："+e.SourceFormat+"。";
                if(!String.IsNullOrEmpty(e.CloseWarning))e.Note+=" 关闭读盘资源时有提示，详见本盘登记。";
                Book.Save(e);
                try {
                    RequireTarget(e);
                    session.Publish(e);
                    Book.CompleteOrigin(e,session.Original);
                } catch(IOException ex) {
                    e.Note+=" 原盘汇总写入未完成："+ex.Message; Book.Save(e);
                }
            }
            catch (Exception ex)
            {
                for(Exception cause=ex;cause!=null;cause=cause.InnerException) {
                    string cleanup=cause.Data["DiscArchive.Cleanup"] as string;
                    if(String.IsNullOrWhiteSpace(cleanup))continue;
                    e.CloseWarning=(e.CloseWarning+" 读盘资源清理提示："+cleanup).Trim();
                    RecordCloseWarning(e,attempt);
                    break;
                }
                RescueStoppedException stopped=ex as RescueStoppedException;
                RescueCanceledException canceled=ex as RescueCanceledException;
                Counts(e,stopped!=null ? stopped.Result : canceled!=null ? canceled.Result : result);
                e.Status = ex is OperationCanceledException && cancellation.IsCancellationRequested ? Paused : (e.Files>0 ? Partial : Review);
                e.Note = attemptName + "  " + (ex is OperationCanceledException ? "本次已停止。下次可使用原盘号核对并补缺，完整文件和残片均保留。" : ex.Message);
                if(!String.IsNullOrEmpty(e.CloseWarning))e.Note+=" 另有读盘收尾提示，详见本盘登记。";
                if(accepted) Book.Save(e);
            }
            finally
            {
                if(session!=null) session.Dispose();
                cancellation.Dispose();
                cancellation = null;
                EndPhoto();
                Current = null;
            }
            return e;
        }

        private static string CloseMessage(Exception error)
        {
            var chain=new List<string>();
            for(Exception e=error;e!=null && chain.Count<6;e=e.InnerException)
                chain.Add(e.Message+" (0x"+e.HResult.ToString("X8")+")");
            return "读盘收尾提示："+String.Join(" → ",chain.ToArray());
        }

        private void RecordCloseWarning(Entry e,string attempt)
        {
            try {
                if(!Book.IsOriginalTarget(e) || !Directory.Exists(attempt))return;
                string path=Path.Combine(attempt,"读盘收尾提示.txt");
                Catalog.CheckNoLinks(path);
                using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                using(var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(true)))
                    writer.WriteLine(e.CloseWarning);
            } catch(IOException) { /* The local catalog also keeps the warning. */ }
              catch(UnauthorizedAccessException) { }
        }

        private async Task<DiscPlan> ResolvePlan(Entry e,MountedDisc disc,Action verify,Action<string> progress,CancellationToken token)
        {
            progress("正在核对卷标、目录、拍摄日期和已有项目名称。");
            bool manual=!String.IsNullOrEmpty(e.ExistingProject);
            DiscPlan plan=await Task.Run(delegate {
                verify();
                return DiscPlan.Decide(disc.Root,e.DiscLabel,e.PhotoText,manual ? "" : e.ProjectHint,e.Id,DateTime.Today,token,disc.Source);
            });
            var resolved=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var names=plan.TopFolders.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool needsDefault=plan.TopFolders.Count==0 || await Task.Run(delegate {
                try { return disc.Source.Entries(disc.Root).Any(p=>!plan.TopFolders.ContainsKey(Path.GetFileName(p))); }
                catch(IOException) { return true; } catch(UnauthorizedAccessException) { return true; }
            });
            if(needsDefault && !names.Contains(plan.Folder,StringComparer.OrdinalIgnoreCase))names.Add(plan.Folder);
            resolved[plan.Folder]=plan.Folder;
            foreach(string proposed in names)
            {
                token.ThrowIfCancellationRequested();
                string project;
                if(manual && String.Equals(proposed,plan.Folder,StringComparison.OrdinalIgnoreCase)) {
                    project=e.ExistingProject;
                    if(!Directory.Exists(ProjectPhoto.DirectoryFor(e.TargetRoot,project)))
                        throw new IOException("所选项目目录已不可用，请重新选择。");
                    plan.Reason+=" 当前盘明确指定已有项目 "+project+"。";
                    if(project!=proposed) await Task.Run(()=>ProjectAliases.Remember(e.TargetRoot,proposed,project));
                } else {
                    AliasChoice a=await Task.Run(()=>ProjectAliases.ResolveForWrite(e.TargetRoot,proposed,false,token));
                    project=a.Resolved;
                    if(a.NeedsDecision) {
                        if(ChooseProject==null) throw new IOException("项目名有歧义，需要明确选择归档目录。");
                        e.Status="等待选择"; progress("有相似项目名，等待选择归档目录。");
                        project=await ChooseProject(e,proposed,a.Suggestions,token);
                        if(project!=proposed) {
                            if(!a.Suggestions.Contains(project,StringComparer.OrdinalIgnoreCase)) throw new IOException("无效的项目选择。");
                            await Task.Run(()=>ProjectAliases.Remember(e.TargetRoot,proposed,project));
                            plan.Reason+=" 人工确认 "+proposed+" 归入 "+project+"。";
                        }
                        else project=(await Task.Run(()=>ProjectAliases.ResolveForWrite(e.TargetRoot,proposed,true,token))).Resolved;
                    }
                    if(project!=proposed) plan.Reason+=" "+a.Reason;
                }
                ProjectPhoto.DirectoryFor(e.TargetRoot,project);
                resolved[proposed]=project;
            }
            foreach(string top in plan.TopFolders.Keys.ToArray()) plan.TopFolders[top]=resolved[plan.TopFolders[top]];
            plan.Folder=resolved[plan.Folder];
            return plan;
        }

        private static void Counts(Entry e,RescueResult result)
        {
            if(result==null) return;
            e.Files=result.Files; e.Bytes=result.Bytes; e.Failed=result.Failed;
            e.ReusedFiles=result.ReusedFiles; e.ReusedBytes=result.ReusedBytes;
            e.Report=String.IsNullOrEmpty(result.Report) ? "" : Path.GetDirectoryName(result.Report);
        }

        private static void RecoverCounts(Entry e)
        {
            if(String.IsNullOrEmpty(e.Report) || !Directory.Exists(e.Report)) return;
            try
            {
                string f=Directory.EnumerateFiles(e.Report,"进度.tsv",SearchOption.AllDirectories).FirstOrDefault();
                if(f==null) return;
                using(var input=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                using(var r=new StreamReader(input))
                {
                    string row;
                    while((row=r.ReadLine())!=null)
                    {
                        string[] cells=row.Split('\t'); long files,bytes,failed;
                        if(cells.Length<5 || !long.TryParse(cells[1],out files) || !long.TryParse(cells[2],out bytes) || !long.TryParse(cells[3],out failed)) continue;
                        e.Files=files; e.Bytes=bytes; e.Failed=failed;
                        long warnings;
                        if(cells.Length>5 && long.TryParse(cells[5],out warnings)) e.Warnings=warnings;
                        if(cells.Length>=8) {
                            long fReuse,bReuse;
                            if(long.TryParse(cells[6],out fReuse) && long.TryParse(cells[7],out bReuse)) {e.ReusedFiles=fReuse; e.ReusedBytes=bReuse;}
                        }
                    }
                }
                e.Report=Path.GetDirectoryName(f);
            }
            catch(IOException) {} catch(UnauthorizedAccessException) {}
        }

        internal void Stop()
        {
            if (Busy) { cancellation.Cancel(); if(photoCancel!=null) photoCancel.Cancel(); return; }
            if (Current == null) return;
            EndPhoto();
            Current.Status = Review;
            Current.Note = "在读取前结束，照片和盘号已保留。";
            if(!protectPrevious) Book.Save(Current);
            Current = null;
        }
        private void EndPhoto()
        {
            if(photoCancel==null) return;
            var c=photoCancel; photoCancel=null; c.Cancel();
            var task=reading;
            if(task==null || task.IsCompleted) c.Dispose();
            else task.ContinueWith(delegate { c.Dispose(); },TaskScheduler.Default);
        }
    }
}
