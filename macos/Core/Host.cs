using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DiscArchive;

internal sealed class HostRequest
{
    public string photo="",target="",targetId="",targetDeviceId="",drive="",project="",existingProject="",mode="metadata",view="auto",resumeId="",driveToken="",mediaHint="";
    public string path="",id="",title="";
    public bool emptyAtShutter,hidden;
}
internal sealed class HostControl
{
    public string action="",value="",text="",error="";
}
internal sealed class Host
{
    internal static readonly JsonSerializerOptions Json=new JsonSerializerOptions {IncludeFields=true};
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getppid();
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly object outputGate=new object(),choiceGate=new object();
    private readonly Func<IReader> createReader;
    private readonly Func<MacInventory> snapshot;
    private readonly Func<string,string> inspect;
    private readonly string state;
    private TaskCompletionSource<DiscMatch> match;
    private List<DiscMatch> matches;
    private TaskCompletionSource<string> projectChoice;
    private string[] projects;
    private string proposed;
    private TaskCompletionSource<HostControl> recognition;
    internal Host(TextReader input,TextWriter output,string state,Func<IReader> reader,Func<MacInventory> snapshot,Func<string,string> inspect)
    {this.input=input;this.output=output;this.state=state;createReader=reader;this.snapshot=snapshot;this.inspect=inspect;}
    private void Emit(object value){lock(outputGate){output.WriteLine(JsonSerializer.Serialize(value,Json));output.Flush();}}
    private void Status(string text)=>Emit(new{ @event="status",text});
    private Catalog Book()=>new Catalog(state);
    private static T Parse<T>(string line) where T:class
    {if(string.IsNullOrWhiteSpace(line)||line.Length>1048576)throw new IOException("缺少或无效的请求。");return JsonSerializer.Deserialize<T>(line,Json)??throw new IOException("请求不能为空。");}
    private async Task<HostRequest> Request()=>Parse<HostRequest>(await input.ReadLineAsync());
    internal static string StateDirectory()
    {
        string custom=Environment.GetEnvironmentVariable("DISCARC_STATE_DIR");
        if(!string.IsNullOrEmpty(custom)){if(!Path.IsPathFullyQualified(custom))throw new IOException("本地登记目录必须是绝对路径。");return Path.GetFullPath(custom);}
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","DiscArchive","Mac");
    }
    internal static async Task<int> Main(string[] args)
    {
        if(args.Length==0||args[0]=="--help"||args[0]=="help")
        {Console.WriteLine("DiscArchive macOS core 0.4.4-beta.1\nCommands: devices, recover, list, projects, export, exportTarget, edit, addManual, setHidden, inspect, job\nRequests and job controls use JSON lines on standard input. Events use JSON lines on standard output.");return 0;}
        if(args[0]=="--version"){Console.WriteLine("0.4.4-beta.1");return 0;}
        Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
        MacReader device=null;
        MacReader Device()=>device??=new MacReader();
        try
        {
            var host=new Host(Console.In,Console.Out,StateDirectory(),()=>Device(),()=>Device().Snapshot(),id=>Device().Inspect(id));
            return await host.Run(args[0]);
        }
        catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{@event="error",text=error.Message},Json));return 1;}
    }
    internal async Task<int> Run(string command)
    {
        try
        {
            switch(command)
            {
                case "devices":var inventory=snapshot();Emit(new{ok=true,drives=inventory.drives,targets=inventory.targets});break;
                case "recover":Workstation.RecoverInterrupted(Book());Emit(new{ok=true});break;
                case "list":var rows=Book().List();Emit(new{ok=true,entries=rows,importedCount=Catalog.LocalImportedCount(rows)});break;
                case "addManual":var add=await Request();Emit(new{ok=true,entry=Book().AddManual(add.title,add.project)});break;
                case "setHidden":var visibility=await Request();Book().SetHidden(visibility.id,visibility.hidden);Emit(new{ok=true});break;
                case "projects":var p=await Request();ValidateTarget(p.target,snapshot());Emit(new{ok=true,projects=ProjectAliases.ListKnownProjects(p.target,CancellationToken.None)});break;
                case "export":var e=await Request();ValidateOutput(e.path);Emit(new{ok=true,count=Book().ExportLocal(e.path)});break;
                case "exportTarget":var t=await Request();ValidateTarget(t.target,snapshot());ValidateOutput(t.path);Emit(new{ok=true,count=Book().ExportTarget(t.target,t.path)});break;
                case "edit":var edit=await Request();var catalog=Book();var row=catalog.List().SingleOrDefault(x=>x.Id==edit.id)??throw new IOException("未找到本机盘号。");catalog.Edit(row,edit.title,edit.project);Emit(new{ok=true});break;
                case "inspect":var request=await Request();if(!snapshot().drives.Any(x=>x.id==request.drive))throw new IOException("所选光驱已断开。");Emit(new{ok=true,text=inspect(request.drive)});break;
                case "job":return await Job(await Request());
                default:throw new IOException("未知命令。可使用 --help 查看。");
            }
            return 0;
        }
        catch(Exception error){Emit(new{ @event="error",text=error.Message});return 1;}
    }
    private static bool Under(string root,string path)
    {root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;path=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;return path.StartsWith(root,ArchivePath.Comparison);}
    private static void ValidateTarget(string target,MacInventory inventory)
    {
        if(string.IsNullOrWhiteSpace(target)||!Path.IsPathFullyQualified(target)||!Directory.Exists(target))throw new IOException("请选择已连接的归档目录。");
        Catalog.CheckNoLinks(Path.GetFullPath(target));
        foreach(var drive in inventory.drives)
        {
            var mounts=drive.partitions.Select(v=>v.mount).Concat(new[]{drive.mount});
            foreach(string mount in mounts)if(!string.IsNullOrEmpty(mount)&&Path.IsPathFullyQualified(mount)&&Under(mount,target))throw new IOException("归档位置不能是光盘本身或其子目录。");
        }
    }
    private static void ValidateTargetDevice(HostRequest request,MacInventory inventory)
    {
        if(string.IsNullOrEmpty(request.targetDeviceId))return;
        var volume=inventory.targets.Where(t=>!string.IsNullOrEmpty(t.path)&&Path.IsPathFullyQualified(t.path)&&Under(t.path,request.target))
            .OrderByDescending(t=>Path.GetFullPath(t.path).TrimEnd(Path.DirectorySeparatorChar).Length).FirstOrDefault();
        if(volume==null||volume.id!=request.targetDeviceId)throw new IOException("所选归档卷已断开或同一路径已被其他硬盘替换，请重新选择归档位置。");
    }
    private static string TargetMarker(HostRequest request)
    {
        Guid parsed;
        if(!string.IsNullOrEmpty(request.targetId)&&!Guid.TryParseExact(request.targetId,"N",out parsed))
            throw new IOException("归档目录身份必须是登记标记GUID；设备卷身份请使用targetDeviceId。");
        string marker=Path.Combine(request.target,".discarchive-target.id");Catalog.CheckNoLinks(marker);
        string existing=File.Exists(marker)?File.ReadAllText(marker).Trim():"";
        if(!string.IsNullOrEmpty(request.targetId)&&existing!=request.targetId)throw new IOException("归档目录登记标记已改变，请核对原备份位置。");
        if(existing.Length>0&&!Guid.TryParseExact(existing,"N",out parsed))throw new IOException("归档目录登记标记损坏。");
        return existing;
    }
    private static void ValidateOutput(string path)
    {if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||!Directory.Exists(Path.GetDirectoryName(path)))throw new IOException("请选择现有目录中的导出文件。");Catalog.CheckNoLinks(path);}
    // Internal injection is exclusively for same-assembly tests. Production has no directory-reader command or environment switch.
    private sealed class BoundReader:IReader
    {
        private readonly IReader inner;
        private readonly Func<MacInventory> snapshot;
        internal readonly string Drive;
        internal string Expected;
        internal bool WasEmpty;
        internal BoundReader(IReader inner,Func<MacInventory> snapshot,string drive){this.inner=inner;this.snapshot=snapshot;Drive=drive;}
        internal MacDrive Current()=>snapshot().drives.FirstOrDefault(x=>x.id==Drive);
        public string[] Drives()=>snapshot().drives.Select(x=>x.id).ToArray();
        public bool Empty(string drive){var now=Current();if(now?.empty==true){WasEmpty=true;Expected=null;return true;}return false;}
        public bool Ready(string drive){var now=Current();if(now==null||!now.ready||string.IsNullOrEmpty(now.token))return false;if(Expected==null&&WasEmpty)Expected=now.token;return Expected!=null&&Expected==now.token;}
        public MountedDisc Open(string drive)
        {
            var now=Current();if(drive!=Drive||now==null||!now.ready||Expected==null||now.token!=Expected)throw new IOException("光盘在确认后发生变化，请核对实物后重新开始。");
            var opened=inner.Open(drive);
            try {if(Current()?.token!=Expected)throw new IOException("光盘在打开期间发生变化。");return new MountedDisc(opened.Root,opened.VolumeLabel,()=>{if(Current()?.token!=Expected)throw new IOException("光盘已更换或光驱已断开。");opened.Verify();},opened,opened.Source,opened.Format){UncoveredTracks=opened.UncoveredTracks,PrepareEject=opened.PrepareEject};}
            catch{opened.Dispose();throw;}
        }
    }
    private static TaskCompletionSource<T> Completion<T>()=>new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task<int> Job(HostRequest request)
    {
        if(request.mode!="metadata"&&request.mode!="ocr"&&request.mode!="model")throw new IOException("未知盘面判读方式。");
        if(request.view!="auto"&&request.view!="mac"&&request.view!="udf"&&request.view!="iso")throw new IOException("未知文件系统视图。");
        MacInventory initial=snapshot();
        var initialDrive=initial.drives.FirstOrDefault(x=>x.id==request.drive)??throw new IOException("请选择当前已连接的光驱。");
        // A cross-process shutter hint is informational only. All live guards use
        // this worker's full token; neither this hint nor the UI token authorizes copying.
        int separator=(initialDrive.token??"").IndexOf(':');
        bool changedSinceShutter=!string.IsNullOrEmpty(request.mediaHint)&&separator>=0&&
            !String.Equals(initialDrive.token.Substring(separator+1),request.mediaHint,StringComparison.Ordinal);
        ValidateTarget(request.target,initial);ValidateTargetDevice(request,initial);
        if(string.IsNullOrEmpty(request.resumeId)&&(!Path.IsPathFullyQualified(request.photo)||!File.Exists(request.photo)))throw new IOException("登记照片不存在。");
        IReader native=createReader();if(native is MacReader mac)mac.View=MacReader.ParseView(request.view);
        var reader=new BoundReader(native,snapshot,request.drive);
        var book=Book();recognition=Completion<HostControl>();
        var registrationPublished=Completion<bool>();
        var station=new Workstation(book,reader,(photo,cancel)=>{
            registrationPublished.Task.WaitAsync(cancel).GetAwaiter().GetResult();
            Emit(new{@event="recognize",photo});
            HostControl response=recognition.Task.WaitAsync(cancel).GetAwaiter().GetResult();
            if(!string.IsNullOrEmpty(response.error))throw new IOException(response.error);
            return response.text??"";
        },()=>request.mode,false);
        station.ChooseMatch=(entry,options,cancel)=>{
            TaskCompletionSource<DiscMatch> pending=Completion<DiscMatch>();
            lock(choiceGate){match=pending;matches=options;}
            var choices=options.Select(x=>new{id=x.Id,status=x.Status,project=x.Project,reason=x.Reason,photo=MatchPhoto(book,entry,x.Id),strong=x.Strong}).ToArray();
            Emit(new{@event="match",options=choices,id=entry.Id,photo=entry.Photo});
            return pending.Task.WaitAsync(cancel);
        };
        station.ChooseProject=(entry,name,options,cancel)=>{
            var pending=Completion<string>();lock(choiceGate){projectChoice=pending;projects=options;proposed=name;}
            Emit(new{@event="project",proposed=name,options});return pending.Task.WaitAsync(cancel);
        };
        bool empty=reader.Empty(request.drive);
        Entry registered;
        try
        {
            MacInventory registrationInventory=snapshot();ValidateTarget(request.target,registrationInventory);ValidateTargetDevice(request,registrationInventory);
            string marker=TargetMarker(request);
            registered=string.IsNullOrEmpty(request.resumeId)
                ?station.Register(request.photo,request.target,marker,request.drive,request.project??"",empty,request.existingProject)
                :station.ResumeKnown(request.target,request.resumeId,request.drive);
            Emit(new{@event="registered",id=registered.Id,photo=registered.Photo,drive=registered.Drive,targetId=registered.TargetId});
            registrationPublished.TrySetResult(true);
            Status(empty?"登记已保留，等待放入光盘。":"登记已保留，请核对当前光盘并确认开始。");
            if(changedSinceShutter)Status("拍照时的介质观察与当前不同，请核对盘面照片和光盘。");
        }
        catch
        {
            registrationPublished.TrySetCanceled();recognition.TrySetCanceled();
            if(station.Current!=null)station.Stop();
            throw;
        }
        var controls=Channel.CreateUnbounded<HostControl>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=false});
        using var lifetime=new CancellationTokenSource();
        _=ReadControls(controls.Writer,lifetime.Token);
        int parent=OperatingSystem.IsMacOS()?getppid():0;
        if(parent>1)_=WatchParent(parent,controls.Writer,lifetime.Token);
        Task<Entry> copying=null;
        bool canceled=false,confirmed=false;
        string lastState="";
        try
        {
            while(true)
            {
                while(controls.Reader.TryRead(out var control))
                {
                    if(control.action=="cancel"||control.action=="eof")
                    {
                        if(!canceled){canceled=true;station.Stop();recognition.TrySetCanceled();lock(choiceGate){match?.TrySetCanceled();projectChoice?.TrySetCanceled();}}
                    }
                    else if(!canceled&&control.action=="confirm"&&copying==null)
                    {
                        var current=reader.Current();
                        if(current?.ready==true&&!string.IsNullOrEmpty(current.token)){reader.Expected=current.token;confirmed=true;}
                        else Status("光盘尚未就绪，请等光盘可读取后重新确认。");
                    }
                    else if(!canceled&&control.action=="recognition")recognition.TrySetResult(control);
                    else if(!canceled&&control.action=="match")
                    {
                        lock(choiceGate)
                        {
                            if(match!=null){var selected=matches.FirstOrDefault(x=>x.Id==control.value);if(control.value==""||selected!=null){match.TrySetResult(selected);match=null;}else Status("旧盘号不在当前候选中，请重新选择。");}
                        }
                    }
                    else if(!canceled&&control.action=="project")
                    {
                        lock(choiceGate){if(projectChoice!=null){if(control.value==proposed||projects.Contains(control.value,StringComparer.Ordinal)){projectChoice.TrySetResult(control.value);projectChoice=null;}else {Status("项目目录不在当前候选中，请重新选择。");Emit(new{@event="project",proposed,options=projects});}}}
                    }
                }
                if(copying!=null&&copying.IsCompleted){Entry result=await copying;Emit(new{@event="result",entry=result,ejectNote=station.LastEjectNote,ejectAccepted=station.LastEjectAccepted});return 0;}
                if(canceled&&copying==null){Emit(new{@event="result",entry=station.Current??registered,ejectNote="",ejectAccepted=false});return 0;}
                if(copying==null&&!canceled)
                {
                    var current=reader.Current();string state=current==null?"disconnected":current.empty?"empty":current.ready?"ready":"waiting";
                    if(state!=lastState){if(state=="disconnected")Status("所选光驱已断开，等待重新连接。 ");else if(state=="empty")Status("已检测到空托盘，等待放盘。 ");lastState=state;}
                    bool can=await station.CanStart(confirmed);
                    if(can)
                    {
                        MacInventory startInventory=snapshot();ValidateTarget(request.target,startInventory);ValidateTargetDevice(request,startInventory);
                        copying=station.Copy(Status);
                    }
                    else if(confirmed){confirmed=false;reader.Expected=null;Status("光盘在确认期间变化，请重新核对并确认。");}
                }
                await Task.Delay(250);
            }
        }
        finally
        {
            lifetime.Cancel();
            if(station.Current!=null||station.Busy)station.Stop();
            recognition.TrySetCanceled();lock(choiceGate){match?.TrySetCanceled();projectChoice?.TrySetCanceled();}
            if(copying!=null&&!copying.IsCompleted)try{await copying;}catch{}
        }
    }
    private static string MatchPhoto(Catalog book,Entry entry,string id)
    {try{return Catalog.OriginalPhotoAt(entry.TargetRoot,book.ReadOrigin(entry,id));}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";}}
    private async Task ReadControls(ChannelWriter<HostControl> writer,CancellationToken cancel)
    {
        try
        {
            while(!cancel.IsCancellationRequested)
            {
                string line=await input.ReadLineAsync(cancel);
                if(line==null){writer.TryWrite(new HostControl{action="eof"});return;}
                try{writer.TryWrite(Parse<HostControl>(line));}catch(Exception error){Status("控制消息无效："+error.Message);}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception){writer.TryWrite(new HostControl{action="eof"});}
    }
    private async Task WatchParent(int original,ChannelWriter<HostControl> writer,CancellationToken cancel)
    {
        try{while(!cancel.IsCancellationRequested){await Task.Delay(700,cancel);if(getppid()!=original){writer.TryWrite(new HostControl{action="eof"});return;}}}catch(OperationCanceledException){}
    }
}
