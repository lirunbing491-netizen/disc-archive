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
        internal readonly Action Verify;
        private readonly IDisposable lease;
        internal MountedDisc(string root,string label,Action verify,IDisposable guard)
        { Root=root; VolumeLabel=label; Verify=verify; lease=guard; }
        public void Dispose() { if(lease!=null) lease.Dispose(); }
    }
    internal sealed class SystemReader : IReader
    {
        public string[] Drives() { return Optical.Drives(); }
        public bool Empty(string drive) { return Optical.IsEmpty(drive); }
        public bool Ready(string drive) { return Optical.IsReady(drive); }
        public MountedDisc Open(string drive)
        {
            OpticalLease lease = Optical.Acquire(drive);
            try { return new MountedDisc(drive,new DriveInfo(drive).VolumeLabel,lease.Verify,lease); }
            catch { lease.Dispose(); throw; }
        }
    }

    internal sealed class Workstation
    {
        internal const string Waiting = "等待放盘", Planning = "正在归类", Running = "抢救中", Done = "已拷出", Partial = "部分拷出", Review = "待复查";
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
        internal bool RecognitionFinished { get { return reading!=null && reading.IsCompleted; } }

        internal Workstation(Catalog book, IReader reader, Func<string,CancellationToken,string> recognizer=null,Func<string> mode=null)
        {
            Book = book;
            Reader = reader;
            recognize = recognizer;
            readMode = mode ?? delegate { return "metadata"; };
            foreach (Entry e in book.List())
            {
                if (e.Status != Running && e.Status != Waiting && e.Status != Planning && e.Status != "复制校验中") continue;
                if(e.Status==Running && book.IsOriginalTarget(e)) RecoverCounts(e);
                e.Status = e.Files>0 ? Partial : Review;
                e.Note = "上次在登记或读取期间退出。请核对盘面，再用原盘号重新读取。 " + e.Note;
                Book.Save(e);
            }
        }

        internal Entry Register(string photo, string target, string targetId, string drive, string project, bool emptyAtShutter=false)
        {
            if (Current != null) throw new InvalidOperationException("请先结束当前盘的任务。");
            if (String.IsNullOrEmpty(drive) || Array.IndexOf(Reader.Drives(), drive) < 0)
                throw new IOException("请连接并选择光驱。");
            if (!String.IsNullOrEmpty(targetId) && !Book.IsOriginalTarget(new Entry { TargetRoot = target, TargetId = targetId }))
                throw new IOException("归档硬盘已掉线或被更换。请接回原硬盘，或重新选择归档位置。");
            // Record tray state at the shutter or at intake, before the photo is written.
            bool observedEmpty = emptyAtShutter || Reader.Empty(drive);
            Entry created = Book.Create(photo, target, drive, project.Trim(), targetId);
            Arm(created,observedEmpty);
            return created;
        }

        internal void Retry(Entry e, string drive)
        {
            if (Current != null || e.Status == Done || e.Status == "已校验") throw new InvalidOperationException("当前不能重新读取这张盘。");
            if (String.IsNullOrEmpty(drive) || Array.IndexOf(Reader.Drives(), drive) < 0) throw new IOException("请先在导盘页选择光驱。");
            RequireTarget(e);
            e.Drive = drive;
            e.Status = Waiting;
            e.Note = "等待重新读取。原有尝试记录保留。";
            Book.Save(e);
            Arm(e,Reader.Empty(drive));
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
            try
            {
                RequireTarget(e);
                e.Status=Planning; e.Note=attemptName; e.Files=e.Bytes=e.Failed=e.Warnings=0; e.Report=""; Book.Save(e);
                progress("光盘已就绪，正在等待盘面判读和归类。");
                using(MountedDisc disc = await Task.Run(delegate { return Reader.Open(e.Drive); }))
                {
                    e.DiscLabel=disc.VolumeLabel;
                    PhotoResult photo=await reading;
                    e.PhotoText=photo.Text;
                    e.VisionNote=photo.Note;
                    token.ThrowIfCancellationRequested();
                    Action verify=delegate { disc.Verify(); RequireTarget(e); };
                    string hint=e.ProjectHint ?? "";
                    DiscPlan plan=await Task.Run(delegate {
                        verify(); progress("正在核对卷标、原目录和可用日期。");
                        return DiscPlan.Decide(disc.Root,e.DiscLabel,e.PhotoText,hint,e.Id,DateTime.Today,token);
                    });
                    token.ThrowIfCancellationRequested();
                    e.NamingReason=plan.Reason;
                    e.ArchiveFolders=await Task.Run(delegate {
                        verify();
                        bool fallback=plan.TopFolders.Count==0;
                        if(!fallback)
                        {
                            try { fallback=Directory.EnumerateFileSystemEntries(disc.Root).Any(p=>!plan.TopFolders.ContainsKey(Path.GetFileName(p))); }
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
                        foreach(string folder in e.ArchiveFolders) ProjectPhoto.Store(e,folder,verify);
                        progress("归档位置已确定：" + e.Project);
                        return RescueCopy.Run(disc.Root,e.TargetRoot,plan.Folder,plan.TopFolders,e.Id,attempt,progress,verify,token);
                    });
                    RequireTarget(e);
                }
                e.Status=result.Files==0 ? Review : (result.Failed==0 ? Done : Partial);
                e.Note=result.Files==0 ? "没有完整拷出文件，请查看本盘记录。" : (result.Failed==0 ? "全部可见文件已拷出。" : "有条目读取失败，详情见失败清单。");
                Counts(e,result);
                Book.Save(e);
            }
            catch (Exception ex)
            {
                RescueStoppedException stopped=ex as RescueStoppedException;
                RescueCanceledException canceled=ex as RescueCanceledException;
                Counts(e,stopped!=null ? stopped.Result : canceled!=null ? canceled.Result : result);
                e.Status = e.Files>0 ? Partial : Review;
                e.Note = attemptName + "  " + (ex is OperationCanceledException ? "已停止，保留已读取的文件。" : ex.Message);
                Book.Save(e);
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                EndPhoto();
                Current = null;
            }
            return e;
        }

        private static void Counts(Entry e,RescueResult result)
        {
            if(result==null) return;
            e.Files=result.Files; e.Bytes=result.Bytes; e.Failed=result.Failed;
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
            Book.Save(Current);
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
