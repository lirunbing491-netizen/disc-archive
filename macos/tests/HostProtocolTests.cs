using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DiscArchive;

internal static class HostProtocolTests
{
    private sealed class Input:TextReader
    {
        internal readonly Channel<string> Lines=Channel.CreateUnbounded<string>();
        public override async Task<string> ReadLineAsync()=>await ReadLineAsync(CancellationToken.None);
        public override async ValueTask<string> ReadLineAsync(CancellationToken token)
        {try{return await Lines.Reader.ReadAsync(token);}catch(ChannelClosedException){return null;}}
        internal void Send(object data)=>Lines.Writer.TryWrite(JsonSerializer.Serialize(data,Host.Json));
    }
    private sealed class Output:TextWriter
    {
        internal readonly Channel<string> Lines=Channel.CreateUnbounded<string>();
        internal readonly List<JsonElement> Seen=new List<JsonElement>();
        public override Encoding Encoding=>Encoding.UTF8;
        public override void WriteLine(string line)=>Lines.Writer.TryWrite(line);
        internal async Task<JsonElement> Event(string name)
        {
            using var timeout=new CancellationTokenSource(10000);
            while(true)
            {
                string line=await Lines.Reader.ReadAsync(timeout.Token);
                var item=JsonDocument.Parse(line).RootElement.Clone();Seen.Add(item);
                if(item.TryGetProperty("event",out var type)&&type.GetString()=="error")throw new Exception("Host error: "+item.GetProperty("text").GetString());
                if(item.TryGetProperty("event",out type)&&type.GetString()==name)return item;
            }
        }
    }
    private sealed class Device:IReader
    {
        private sealed class EjectProbe : IOpticalEjectTicket
        {
            private readonly Device owner;
            internal EjectProbe(Device owner){this.owner=owner;}
            public bool TryEject(out string reason){owner.Ejects++;reason="";return true;}
            public void Dispose(){}
        }
        internal string Source,Token="worker-process:provider:disc-A",TargetPath="",TargetDeviceId="12345";
        internal bool EmptyTray,ReadyFlag=true,ChangeWhenOpened;
        internal int Opens,Ejects;
        internal MacInventory Snapshot()=>new MacInventory{drives=new[]{new MacDrive{id="test-optical",ready=ReadyFlag,empty=EmptyTray,token=Token}},targets=TargetPath.Length==0?Array.Empty<MacTarget>():new[]{new MacTarget{path=Path.GetPathRoot(TargetPath),id="system-volume",name="Synthetic parent mount"},new MacTarget{path=TargetPath,id=TargetDeviceId,name="Synthetic external disk"}}};
        public string[] Drives()=>new[]{"test-optical"};
        public bool Empty(string drive)=>EmptyTray;
        public bool Ready(string drive)=>ReadyFlag;
        public MountedDisc Open(string drive){Interlocked.Increment(ref Opens);if(ChangeWhenOpened)Token="disc-B";return new MountedDisc(Source,"240102-Test",delegate{},null){PrepareEject=()=>new EjectProbe(this)};}
    }
    private static int checks;
    private static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS protocol "+text);}
    private static async Task Case(string root,string name,Func<Host,Device,Input,Output,Task> test)
    {
        string area=Path.Combine(root,name);Directory.CreateDirectory(area);
        string source=Path.Combine(area,"disc");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"file.bin"),"synthetic payload");
        var device=new Device{Source=source};var input=new Input();var output=new Output();
        var host=new Host(input,output,Path.Combine(area,"state"),()=>device,device.Snapshot,id=>"synthetic inspection");
        string target=Path.Combine(area,"backup");Directory.CreateDirectory(target);string photo=Path.Combine(area,"photo.bin");File.WriteAllText(photo,"full-photo");
        if(name.StartsWith("target-device",StringComparison.Ordinal))device.TargetPath=target;
        input.Send(new HostRequest{photo=photo,target=target,targetDeviceId=name.StartsWith("target-device",StringComparison.Ordinal)?"12345":"",drive="test-optical",project=name=="project-retry"?"240102-Client":"240102-Test",emptyAtShutter=true,driveToken="foreign-ui-token",mediaHint="ui-old-media",mode=name.StartsWith("recognition",StringComparison.Ordinal)?"ocr":"metadata"});
        await test(host,device,input,output);
    }
    private static async Task OneShotReplies(string root)
    {
        string target=Path.Combine(root,"one-shot-target");Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target,"240102-Project"));
        var device=new Device();
        foreach(string command in new[]{"list","projects","inspect"})
        {
            var request=new HostRequest{target=target,drive="test-optical"};
            using var input=new StringReader(JsonSerializer.Serialize(request,Host.Json)+"\n");
            using var output=new StringWriter();
            var host=new Host(input,output,Path.Combine(root,"one-shot-state"),()=>device,device.Snapshot,id=>"synthetic inspection");
            int code=await host.Run(command);
            var response=JsonDocument.Parse(output.ToString()).RootElement;
            // Match CoreIPC.run's actual success gate: process exit zero AND ok true.
            bool accepted=code==0&&response.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True;
            bool payload=command=="list" ? response.GetProperty("entries").ValueKind==JsonValueKind.Array :
                command=="projects" ? response.GetProperty("projects").EnumerateArray().Any(x=>x.GetString()=="240102-Project") :
                response.GetProperty("text").GetString()=="synthetic inspection";
            Check(accepted&&payload,command+" success envelope passes CoreIPC.run and preserves UI payload field");
        }
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"discarchive-protocol-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        PortableSupport.ThumbnailForTests=s=>new byte[]{255,216,255,217};
        await OneShotReplies(root);
        await Case(root,"confirm",async(host,device,input,output)=>{
            string area=Path.GetDirectoryName(device.Source);
            var book=new Catalog(Path.Combine(area,"state"));
            Entry unrelated=book.Create(Path.Combine(area,"photo.bin"),Path.Combine(area,"backup"),"other-optical","untouched");
            Task<int> running=host.Run("job");await output.Event("registered");await Task.Delay(650);
            Check(device.Opens==0,"UI token and shutter-empty claim do not start mounted media");
            input.Send(new{action="confirm"});var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Done,"confirmed medium reaches shared successful result");
            Check(result.GetProperty("ejectAccepted").GetBoolean() && device.Ejects==1 && result.GetProperty("ejectNote").GetString().Length>0,"job forwards bound ticket and returns per-drive eject outcome");
            Check(await running==0&&device.Opens==1,"confirmed job opens one bound source");
            Check(output.Seen.Any(x=>x.TryGetProperty("text",out var text)&&(text.GetString()??"").Contains("拍照时")),"cross-process media hint mismatch is reported without authorizing import");
            Check(book.List().Single(x=>x.Id==unrelated.Id).Status==Workstation.Waiting,"new job never recovers another drive's active registration");
            input.Lines.Writer.TryComplete();
        });
        await Case(root,"eof",async(host,device,input,output)=>{
            var running=host.Run("job");var registration=await output.Event("registered");input.Lines.Writer.TryComplete();
            var result=await output.Event("result");Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Review,"stdin EOF ends pending registration with visible review status");
            Check(await running==0&&device.Opens==0,"stdin EOF never opens unconfirmed media");
        });
        await Case(root,"recognition",async(host,device,input,output)=>{
            var running=host.Run("job");await output.Event("registered");await output.Event("recognize");
            Check(output.Seen[0].GetProperty("event").GetString()=="registered","registration precedes native recognition request");
            input.Send(new{action="recognition",text="240102-Test"});input.Send(new{action="confirm"});
            var result=await output.Event("result");Check(result.GetProperty("entry").GetProperty("PhotoText").GetString()=="240102-Test","GUI recognition response reaches shared classification");
            Check(await running==0,"recognition job finishes");input.Lines.Writer.TryComplete();
        });
        await Case(root,"media-race",async(host,device,input,output)=>{
            device.ChangeWhenOpened=true;var running=host.Run("job");await output.Event("registered");input.Send(new{action="confirm"});
            var result=await output.Event("result");Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Review,"media replacement during open never counts as archived");
            Check(result.GetProperty("entry").GetProperty("Files").GetInt64()==0,"media-token race copies no file");Check(await running==0,"media-race cleanup completes");input.Lines.Writer.TryComplete();
        });
        await Case(root,"auto-empty",async(host,device,input,output)=>{
            device.EmptyTray=true;device.ReadyFlag=false;
            var running=host.Run("job");await output.Event("registered");
            device.EmptyTray=false;device.ReadyFlag=true;
            var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Done,"backend-observed empty tray permits automatic import after insertion");
            Check(await running==0&&device.Opens==1,"automatic insertion opens exactly one source");input.Lines.Writer.TryComplete();
        });
        await Case(root,"recognition-cancel",async(host,device,input,output)=>{
            var running=host.Run("job");await output.Event("registered");await output.Event("recognize");input.Send(new{action="confirm"});
            for(int i=0;i<100&&device.Opens==0;i++)await Task.Delay(25);
            Check(device.Opens==1,"recognition-cancel fixture reached active copy");
            input.Lines.Writer.TryComplete();var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Paused,"stdin EOF cancels an active job waiting for recognition");
            Check(await running==0,"cancel closes asynchronous recognition without hanging");
        });
        await Case(root,"project-retry",async(host,device,input,output)=>{
            string target=Path.Combine(Path.GetDirectoryName(device.Source),"backup");
            Directory.CreateDirectory(Path.Combine(target,"240102-Clienx"));
            var running=host.Run("job");await output.Event("registered");input.Send(new{action="confirm"});
            var first=await output.Event("project");input.Send(new{action="project",value="240102-Unlisted"});
            var repeated=await output.Event("project");
            Check(first.GetProperty("proposed").GetString()==repeated.GetProperty("proposed").GetString()&&
                first.GetProperty("options").GetRawText()==repeated.GetProperty("options").GetRawText(),"invalid project reply republishes the same actionable choice");
            input.Send(new{action="project",value=repeated.GetProperty("proposed").GetString()});
            var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Done&&await running==0,"republished project choice can finish without a hung job");
            input.Lines.Writer.TryComplete();
        });
        await Case(root,"target-device-valid",async(host,device,input,output)=>{
            var running=host.Run("job");var registration=await output.Event("registered");
            Check(Guid.TryParseExact(registration.GetProperty("targetId").GetString(),"N",out var marker)&&registration.GetProperty("targetId").GetString()!="12345","numeric external volume id is distinct from the newly created archive marker GUID");
            input.Send(new{action="confirm"});var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Done&&await running==0,"numeric external volume identity permits registration and copying");input.Lines.Writer.TryComplete();
        });
        await Case(root,"target-device-replaced",async(host,device,input,output)=>{
            var running=host.Run("job");await output.Event("registered");device.TargetDeviceId="99999";input.Send(new{action="confirm"});
            using var timeout=new CancellationTokenSource(10000);JsonElement failure=default;
            while(await output.Lines.Reader.WaitToReadAsync(timeout.Token)){
                failure=JsonDocument.Parse(await output.Lines.Reader.ReadAsync(timeout.Token)).RootElement.Clone();
                if(failure.TryGetProperty("event",out var kind)&&kind.GetString()=="error")break;
            }
            Check(failure.GetProperty("text").GetString().Contains("其他硬盘替换")&&await running==1&&device.Opens==0,"replacement disk at the same target path is refused before opening source media");
            input.Lines.Writer.TryComplete();
        });
        PortableSupport.ThumbnailForTests=null;
        Check(!JsonSerializer.Serialize(new Entry{Id="TEST"},Host.Json).Contains("\"id\""),"Entry fields keep PascalCase in protocol");
        Console.WriteLine("PASS host protocol checks="+checks+" fixtures="+root);
    }
}
