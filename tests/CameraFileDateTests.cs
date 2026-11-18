using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DiscArchive;

internal static class CameraFileDateTests
{
    private static string root;
    private static int passed;
    private static readonly DateTime Expected = new DateTime(2016,4,9,20,53,53);
    private static void Assert(bool value, string message) { if(!value) throw new Exception(message); }
    private static void P16(byte[] b,int o,int n,bool big) { b[o]=(byte)(big?n>>8:n); b[o+1]=(byte)(big?n:n>>8); }
    private static void P32(byte[] b,int o,uint n,bool big) { for(int i=0;i<4;i++) b[o+i]=(byte)(n>>(big?(3-i)*8:i*8)); }
    private static void Entry(byte[] b,int o,int tag,int type,uint count,uint value,bool big) { P16(b,o,tag,big);P16(b,o+2,type,big);P32(b,o+4,count,big);P32(b,o+8,value,big); }
    private static void Text(byte[] b,int o,string value) { byte[] text=Encoding.ASCII.GetBytes(value); Buffer.BlockCopy(text,0,b,o,text.Length); }
    private static byte[] Tiff(int offset,bool big,bool original,bool digitized)
    {
        byte[] b=new byte[offset+512]; b[0]=b[1]=(byte)(big?'M':'I'); P16(b,2,42,big);P32(b,4,(uint)offset,big);
        P16(b,offset,3,big); Entry(b,offset+2,0x0132,2,20,(uint)(offset+200),big);Entry(b,offset+14,0x8769,4,1,(uint)(offset+80),big);Entry(b,offset+26,0x0111,4,1,0xfffffff0,big);
        int count=(original?1:0)+(digitized?1:0); P16(b,offset+80,count,big);int index=offset+82;
        if(original) { Entry(b,index,0x9003,2,20,(uint)(offset+220),big); index+=12; }
        if(digitized) Entry(b,index,0x9004,2,20,(uint)(offset+240),big);
        Text(b,offset+200,"2020:01:02 03:04:05\0");Text(b,offset+220,"2016:04:09 20:53:53\0");Text(b,offset+240,"2014:01:01 00:00:00\0"); return b;
    }
    private static DateTime? Read(byte[] bytes,Action<string> evidence=null)
    { string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".raw");File.WriteAllBytes(path,bytes);return CameraFileDate.Read(path,CancellationToken.None,evidence); }
    private static void Test(string name,Action action) { action();passed++;Console.WriteLine("PASS "+name); }
    public static int Main(string[] args)
    {

        root=Path.Combine(Path.GetTempPath(),"CameraDateTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Test("little endian external IFD beyond first MiB",delegate { Assert(Read(Tiff(1048576,false,true,true))==Expected,"external offset"); });
            Test("big endian external IFD",delegate { Assert(Read(Tiff(600000,true,true,true))==Expected,"big endian"); });
            Test("SubIFD external pointer array reaches capture IFD",delegate {
                byte[] b=Tiff(32,false,true,true);Entry(b,46,0x014a,4,2,332,false);P32(b,332,112,false);P32(b,336,32,false);Assert(Read(b)==Expected,"SubIFD array");
            });
            Test("next IFD external pointer reaches capture IFD",delegate {
                byte[] b=Tiff(32,false,true,true);Entry(b,46,0x010e,2,1,0,false);P32(b,70,112,false);Assert(Read(b)==Expected,"next IFD");
            });
            Test("DateTimeOriginal wins over digitized and modified dates",delegate { Assert(Read(Tiff(32,false,true,true))==Expected,"capture precedence"); });
            Test("digitized and modified alone do not qualify as capture",delegate { Assert(!Read(Tiff(32,false,false,true)).HasValue,"no capture guess"); });
            Test("raw date-like text is not capture evidence",delegate { byte[] b=Tiff(32,false,false,false);Text(b,300,"2016:04:09 20:53:53");Assert(!Read(b).HasValue,"no string scan"); });
            Test("invalid calendar date rejected",delegate { byte[] b=Tiff(32,false,true,true);Text(b,252,"2016:02:31 20:53:53\0");Assert(!Read(b).HasValue,"invalid date"); });
            Test("cyclic next IFD bounded",delegate { byte[] b=Tiff(32,false,false,false);P32(b,70,32,false);Assert(!Read(b).HasValue,"cycle"); });
            Test("invalid huge offset bounded",delegate { byte[] b=Tiff(32,false,true,true);P32(b,4,0xfffffff0,false);Assert(!Read(b).HasValue,"bad offset"); });
            Test("malformed IFD count bounded",delegate { byte[] b=Tiff(32,false,true,true);P16(b,32,65535,false);Assert(!Read(b).HasValue,"bad count"); });
            Test("truncated file harmless",delegate { Assert(!Read(new byte[]{73,73,42,0,8,0,0,0}).HasValue,"truncated"); });
            Test("EXIF APP1 JPEG metadata only",delegate {
                byte[] t=Tiff(32,false,true,true);byte[] b=new byte[t.Length+14];b[0]=255;b[1]=216;b[2]=255;b[3]=225;int length=t.Length+8;b[4]=(byte)(length>>8);b[5]=(byte)length;Text(b,6,"Exif\0\0");Buffer.BlockCopy(t,0,b,12,t.Length);b[b.Length-2]=255;b[b.Length-1]=217;Assert(Read(b)==Expected,"jpeg");
            });
            Test("PhaseOne public 0x0112 timestamp fallback",delegate {
                byte[] b=new byte[256];b[0]=b[1]=73;P16(b,2,42,false);P32(b,4,128,false);Text(b,8,"IIIICwaR");P32(b,16,24,false);P32(b,32,2,false);
                P32(b,40,0x112,false);P32(b,44,4,false);P32(b,48,4,false);P32(b,52,1460235233,false);
                P32(b,56,0x212,false);P32(b,60,4,false);P32(b,64,4,false);P32(b,68,1460235097,false);
                List<string> evidence=new List<string>();Assert(Read(b,evidence.Add)==Expected && evidence[0].Contains("0x0112"),"PhaseOne capture");
                P32(b,40,0x212,false);Assert(!Read(b).HasValue,"unknown PhaseOne date ignored");
            });
            Test("standard EXIF capture takes precedence over PhaseOne fallback",delegate {
                byte[] b=Tiff(128,false,true,true);Text(b,8,"IIIICwaR");P32(b,16,24,false);P32(b,32,2,false);P32(b,40,0x112,false);P32(b,44,4,false);P32(b,48,4,false);P32(b,52,1200000000,false);
                List<string> evidence=new List<string>();Assert(Read(b,evidence.Add)==Expected && evidence[0].Contains("0x9003"),"EXIF precedence");
            });
            Test("pre-cancel propagated",delegate {
                CancellationTokenSource c=new CancellationTokenSource();c.Cancel();bool canceled=false;
                try { CameraFileDate.Read("not-read",c.Token); } catch(OperationCanceledException) { canceled=true; } Assert(canceled,"cancel");
            });
            Console.WriteLine("PASS "+passed+" isolated camera metadata tests");return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e);return 1; }
        finally { Directory.Delete(root,true); }
    }
}
