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
        internal string Source,Token="worker-process:provider:disc-A";
        internal bool EmptyTray,ReadyFlag=true,ChangeWhenOpened;
        internal int Opens;
        internal MacInventory Snapshot()=>new MacInventory{drives=new[]{new MacDrive{id="test-optical",ready=ReadyFlag,empty=EmptyTray,token=Token}},targets=Array.Empty<MacTarget>()};
        public string[] Drives()=>new[]{"test-optical"};
        public bool Empty(string drive)=>EmptyTray;
        public bool Ready(string drive)=>ReadyFlag;
        public MountedDisc Open(string drive){Interlocked.Increment(ref Opens);if(ChangeWhenOpened)Token="disc-B";return new MountedDisc(Source,"240102-Test",delegate{},null);}
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
        input.Send(new HostRequest{photo=photo,target=target,drive="test-optical",project="240102-Test",emptyAtShutter=true,driveToken="foreign-ui-token",mediaHint="ui-old-media",mode=name.StartsWith("recognition",StringComparison.Ordinal)?"ocr":"metadata"});
        await test(host,device,input,output);
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"discarchive-protocol-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        PortableSupport.ThumbnailForTests=s=>new byte[]{255,216,255,217};
        await Case(root,"confirm",async(host,device,input,output)=>{
            string area=Path.GetDirectoryName(device.Source);
            var book=new Catalog(Path.Combine(area,"state"));
            Entry unrelated=book.Create(Path.Combine(area,"photo.bin"),Path.Combine(area,"backup"),"other-optical","untouched");
            Task<int> running=host.Run("job");await output.Event("registered");await Task.Delay(650);
            Check(device.Opens==0,"UI token and shutter-empty claim do not start mounted media");
            input.Send(new{action="confirm"});var result=await output.Event("result");
            Check(result.GetProperty("entry").GetProperty("Status").GetString()==Workstation.Done,"confirmed medium reaches shared successful result");
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
        PortableSupport.ThumbnailForTests=null;
        Check(!JsonSerializer.Serialize(new Entry{Id="TEST"},Host.Json).Contains("\"id\""),"Entry fields keep PascalCase in protocol");
        Console.WriteLine("PASS host protocol checks="+checks+" fixtures="+root);
    }
}
