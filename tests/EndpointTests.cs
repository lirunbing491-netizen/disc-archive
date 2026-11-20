using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DiscArchive;

class EndpointTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    // A loopback-only TCP HTTP fixture needs no URL reservation or administrator account.
    sealed class Server : IDisposable
    {
        private readonly TcpListener listener;
        public string Url; public int Calls; public string Body, Authorization, RequestPath;
        public Server(int status, string response, int delay, string redirect)
        {
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(() => {
                try {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream()) {
                        client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                        Interlocked.Increment(ref Calls);
                        var header = new StringBuilder();
                        while (header.Length < 65536 && !header.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)) {
                            int value=stream.ReadByte(); if(value<0)throw new IOException("Truncated HTTP headers"); header.Append((char)value);
                        }
                        string[] lines=header.ToString().Split(new string[]{"\r\n"},StringSplitOptions.None);
                        RequestPath=lines[0].Split(' ')[1]; int length=0; bool expect=false;
                        foreach(string line in lines) {
                            int colon=line.IndexOf(':'); if(colon<0)continue;
                            string key=line.Substring(0,colon).Trim(), value=line.Substring(colon+1).Trim();
                            if(key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase))length=Int32.Parse(value);
                            if(key.Equals("Authorization",StringComparison.OrdinalIgnoreCase))Authorization=value;
                            if(key.Equals("Expect",StringComparison.OrdinalIgnoreCase) && value=="100-continue")expect=true;
                        }
                        if(length<0 || length>16*1024*1024)throw new IOException("Fixture request too large");
                        if(expect){byte[] interim=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");stream.Write(interim,0,interim.Length);}
                        byte[] body=new byte[length]; int offset=0;
                        while(offset<length){int read=stream.Read(body,offset,length-offset);if(read==0)throw new IOException("Truncated HTTP body");offset+=read;}
                        Body=Encoding.UTF8.GetString(body);
                        if(delay>0)Thread.Sleep(delay);
                        byte[] bytes=Encoding.UTF8.GetBytes(response);
                        string headers="HTTP/1.1 "+status+" Test\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n";
                        if(redirect!=null)headers+="Location: "+redirect+"\r\n";
                        byte[] responseHeader=Encoding.ASCII.GetBytes(headers+"\r\n");
                        stream.Write(responseHeader,0,responseHeader.Length);stream.Write(bytes,0,bytes.Length);
                    }
                } catch (Exception) { }
            });
        }
        public void Dispose() { listener.Stop(); }
    }
    static VisionOptions Options(Server server) { return new VisionOptions { Mode = "model", Endpoint = server.Url + "/v1", Model = "local-test-model", ApiKey = "test-secret-not-real" }; }
    static string Fails(Action action) { try { action(); } catch (InvalidOperationException ex) { return ex.Message; } throw new Exception("Expected safe failure"); }
    static int Main()
    {
        try { Run(); return 0; } catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DiscArchive Endpoint " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string photo = Path.Combine(dir, "synthetic.png"), blank = Path.Combine(dir, "blank.png");
        using (Bitmap image = new Bitmap(1280,720)) using (Graphics g = Graphics.FromImage(image)) {
            g.Clear(Color.FromArgb(25,25,25)); image.Save(blank,ImageFormat.Png);
            g.FillEllipse(Brushes.LightGray,400,60,600,600);g.FillEllipse(Brushes.Black,670,330,60,60);
            using(Font font = new Font("Arial",26,FontStyle.Bold))g.DrawString("ARCHIVE TITLE",font,Brushes.Black,530,140);
            image.Save(photo,ImageFormat.Png);
        }
        VisionOptions initial = VisionOptions.Load(dir); Check(initial.Mode == "metadata" && !initial.AllowFullPhoto && Directory.GetFiles(dir,"*settings*").Length == 0,"load defaults without writing settings or full photo permission");
        VisionOptions save = new VisionOptions {Mode="model",Endpoint="http://127.0.0.1:12345/v1",Model="test",ApiKey="private-test-secret",AllowFullPhoto=true}; save.Save(dir);
        string settings = File.ReadAllText(Path.Combine(dir,"vision-settings.json")); Check(!settings.Contains(save.ApiKey),"API key absent from plaintext settings");
        VisionOptions loaded = VisionOptions.Load(dir); Check(loaded.ApiKey==save.ApiKey && loaded.Model=="test" && loaded.AllowFullPhoto,"DPAPI and explicit full photo permission roundtrip");
        loaded.Clone().Model="other"; Check(loaded.Model=="test","options clone independent");
        var broken = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(settings); broken["protectedApiKey"]="invalid";File.WriteAllText(Path.Combine(dir,"vision-settings.json"),new JavaScriptSerializer().Serialize(broken));
        loaded=VisionOptions.Load(dir);Check(loaded.ApiKey=="" && loaded.Mode=="metadata" && loaded.LoadWarning.Length>0,"unreadable key falls back to metadata with friendly warning");
        Check(Fails(()=>VisionEndpoint.ValidateEndpoint("http://example.com/v1")).Length>0,"external plain HTTP rejected");
        Check(Fails(()=>VisionEndpoint.ValidateEndpoint("https://user:pass@example.com/v1")).Length>0,"embedded URL credentials rejected");
        Check(VisionEndpoint.ValidateEndpoint("https://example.com").AbsolutePath=="/v1/chat/completions","base URL normalized");
        Check(VisionEndpoint.ValidateEndpoint("https://example.com/custom/chat/completions").AbsolutePath=="/custom/chat/completions","exact endpoint preserved");
        using(Server server=new Server(200,"{\"choices\":[{\"message\":{\"content\":\"Synthetic title\"}}]}",0,null)) {
            VisionOptions opt=Options(server);string result=VisionEndpoint.Read(opt,photo,CancellationToken.None);
            Check(result=="Synthetic title","OpenAI compatible response parsed");
            Check(server.Calls==1 && server.RequestPath=="/v1/chat/completions" && server.Authorization=="Bearer test-secret-not-real","one request with model authorization");
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(server.Body);Check((string)data["model"]==opt.Model,"configured model sent");
            Check(!data.ContainsKey("thinking"),"other compatible services receive no DeepSeek-specific parameter");
            var messages=(ArrayList)data["messages"];var message=(Dictionary<string,object>)messages[0];var content=(ArrayList)message["content"];var imageEntry=(Dictionary<string,object>)content[1];var imageUrl=(Dictionary<string,object>)imageEntry["image_url"];
            string uri=(string)imageUrl["url"];Check(uri.StartsWith("data:image/jpeg;base64,"),"actual image supplied inline");
            using(MemoryStream memory=new MemoryStream(Convert.FromBase64String(uri.Substring(uri.IndexOf(',')+1))))using(Bitmap sent=new Bitmap(memory)) {
                Color corner=sent.GetPixel(0,0),centre=sent.GetPixel(sent.Width/2,sent.Height/2);
                Check(corner.R>240 && centre.R>240,"surroundings and centre hole masked white");
                bool privateExif=false;foreach(int property in sent.PropertyIdList)if(property==0x112 || property<32)privateExif=true;
                Check(!privateExif,"reencoding strips orientation and GPS metadata");
            }
            Check(!server.Body.Contains(dir) && !server.Body.Contains("synthetic.png"),"no local path or filename sent");
        }
        using(Server server=new Server(200,"{}",0,null)) {
            VisionOptions opt=Options(server);opt.Mode="metadata";Check(VisionEndpoint.Read(opt,photo,CancellationToken.None)=="","metadata mode blank");
            opt.Mode="ocr";VisionEndpoint.Read(opt,photo,CancellationToken.None);Check(server.Calls==0,"metadata and OCR never request model");
        }
        using(Server target=new Server(200,"{}",0,null))using(Server redirect=new Server(302,"",0,target.Url+"/steal")) {
            string error=Fails(()=>VisionEndpoint.Read(Options(redirect),photo,CancellationToken.None));Thread.Sleep(100);
            Check(target.Calls==0 && error.Contains("跳转"),"redirect refused without forwarding key or image");
        }
        using(Server server=new Server(401,"private-response-secret",0,null)) {
            string error=Fails(()=>VisionEndpoint.Read(Options(server),photo,CancellationToken.None));Check(error.Contains("401")&&!error.Contains("secret"),"HTTP failure sanitized");
        }
        using(Server server=new Server(200,"{}",0,null))Check(Fails(()=>VisionEndpoint.Read(Options(server),photo,CancellationToken.None)).Contains("格式"),"malformed model response rejected");
        using(Server server=new Server(200,"{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"unfinished\"}}]}",0,null))
            Check(Fails(()=>VisionEndpoint.Read(Options(server),photo,CancellationToken.None)).Contains("输出额度"),"truncated transcript is not used to name a project");
        using(Server server=new Server(200,"{}",0,null)) {
            Check(Fails(()=>VisionEndpoint.Read(Options(server),blank,CancellationToken.None)).Contains("未发送")&&server.Calls==0,"uncertain disc never uploads full photo");
        }
        using(Server server=new Server(200,"{\"choices\":[{\"message\":{\"content\":\"Whole photo test\"}}]}",0,null)) {
            VisionOptions opt=Options(server);opt.AllowFullPhoto=true;
            Check(VisionEndpoint.Read(opt,blank,CancellationToken.None)=="Whole photo test" && server.Calls==1,"explicit permission allows non-disc photo reencoding");
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(server.Body);
            var messages=(ArrayList)data["messages"];var message=(Dictionary<string,object>)messages[0];var content=(ArrayList)message["content"];
            var entry=(Dictionary<string,object>)content[1];var imageUrl=(Dictionary<string,object>)entry["image_url"];string uri=(string)imageUrl["url"];
            using(MemoryStream memory=new MemoryStream(Convert.FromBase64String(uri.Substring(uri.IndexOf(',')+1))))using(Bitmap sent=new Bitmap(memory)) {
                Check(sent.Width==1280 && sent.Height==720 && sent.GetPixel(0,0).R<60,"authorized fallback preserves full photo framing");
            }
        }
        using(Server server=new Server(200,"{}",5000,null))using(CancellationTokenSource cancel=new CancellationTokenSource(500)) {
            Stopwatch timer=Stopwatch.StartNew();bool canceled=false;try {VisionEndpoint.Read(Options(server),photo,cancel.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled&&timer.ElapsedMilliseconds<2000,"cancellation interrupts wait");
        }
        using(Server server=new Server(200,"{}",22000,null)) {
            Stopwatch timer=Stopwatch.StartNew();string error=Fails(()=>VisionEndpoint.Read(Options(server),photo,CancellationToken.None));
            Check(timer.ElapsedMilliseconds<19500,"network timeout bounded under 20 seconds: "+timer.ElapsedMilliseconds+"ms");
            Check(!error.Contains("secret"),"timeout error contains no key or response");
        }
        Console.WriteLine("Evidence retained: "+dir);
    }
}
