using System;
using System.IO;
using System.Text;
using System.Threading;
using DiscArchive;
public static class PlanTests
{
    static string work;
    static void Check(bool ok,string name) { if(!ok)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name); }
    static string Fresh(string name) {string p=Path.Combine(work,name);Directory.CreateDirectory(p);return p;}
    static DiscPlan Plan(string root,string volume,string photo,string user,string id) {return DiscPlan.Decide(root,volume,photo,user,id,new DateTime(2024,8,15),CancellationToken.None);}
    static byte[] Tiff(string date,bool little,int tag)
    {
        byte[] b=new byte[70];b[0]=b[1]=(byte)(little?'I':'M');
        Action<int,int,int> n=(at,value,len)=>{for(int i=0;i<len;i++)b[at+(little?i:len-1-i)]=(byte)(value>>(8*i));};
        n(2,42,2);n(4,8,4);n(8,1,2);n(10,tag,2);n(12,2,2);n(14,20,4);n(18,30,4);byte[] s=Encoding.ASCII.GetBytes(date);Array.Copy(s,0,b,30,s.Length);return b;
    }
    static byte[] NestedTiff() { byte[] b=new byte[80];b[0]=b[1]=73;b[2]=42;b[4]=8;b[8]=1;b[10]=0x69;b[11]=0x87;b[12]=4;b[14]=1;b[18]=26;b[26]=1;b[28]=3;b[29]=0x90;b[30]=2;b[32]=20;b[36]=50;byte[] t=Encoding.ASCII.GetBytes("2010:02:03 04:05:06");Array.Copy(t,0,b,50,t.Length);return b;}
    static void Jpeg(string p,string day) {byte[] t=Tiff(day,true,0x9003);using(FileStream s=File.Create(p)){byte[] h={255,216,255,225,0,(byte)(t.Length+8),69,120,105,102,0,0};s.Write(h,0,h.Length);s.Write(t,0,t.Length);s.WriteByte(255);s.WriteByte(217);}}
    public static int Main()
    {
        work=Path.Combine(Path.GetTempPath(),"DiscPlanSynthetic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try {
            string a=Fresh("empty");
            Check(Plan(a,"170621-星河工作室","170621-星河工祚室",null,"D01").Folder=="170621-星河工作室","meaningful volume beats OCR misread");
            Check(Plan(a,"160409-云杉影像",null,null,"D01").Folder=="160409-云杉影像","second project pattern");
            Check(Plan(a,null,null,null,"D01").Folder=="240815-拍摄日未知-D01","honest no-data fallback");
            Check(Plan(a,null,null,null,"D02").Folder!=Plan(a,null,null,null,"D01").Folder,"unknown discs never merge");
            foreach(string bad in new string[]{"Raw 1-2","jpg","PSD","CaptureOne","1234567890123","12-2","13"})Check(Plan(a,bad,bad,null,"D01").Folder=="240815-拍摄日未知-D01","reject generic "+bad);
            Check(Plan(a,"170621-星河工作室",null,"用户确认项目","D01").Folder=="170621-用户确认项目","explicit user overrides project name");
            Check(Plan(a,"17-06-21-星河工作室",null,null,"D01").Folder=="170621-星河工作室","separated short date");
            DiscPlan photoOnly=Plan(a,null,"五六个普通字",null,"D01");Check(photoOnly.Folder.Contains("五六个普通字")&&photoOnly.Folder.Contains("D01")&&photoOnly.Reason.Contains("仅盘面有可读名称")&&photoOnly.Reason.Contains("避免误并同名项目"),"readable photo words retained as disc description without asserting customer");
            string b=Fresh("single");Directory.CreateDirectory(Path.Combine(b,"170621-星河工作室"));Directory.CreateDirectory(Path.Combine(b,"jpg"));Directory.CreateDirectory(Path.Combine(b,"Raw 1-2"));
            DiscPlan p=Plan(b,null,null,null,"D01");Check(p.Folder=="170621-星河工作室"&&p.TopFolders.Count==0,"single project keeps all structure");
            string c=Fresh("mixed");Directory.CreateDirectory(Path.Combine(c,"170621-星河工作室"));Directory.CreateDirectory(Path.Combine(c,"160409-云杉影像"));Directory.CreateDirectory(Path.Combine(c,"13"));File.WriteAllText(Path.Combine(c,"root.txt"),"sample");
            p=Plan(c,null,null,null,"D03");Check(p.TopFolders.Count==2&&p.Folder=="240815-混装待归属-D03"&&!p.TopFolders.ContainsKey("13"),"only strong top-level groups; root unknowns separated");
            string d=Fresh("raw");Jpeg(Path.Combine(d,"preview.jpg"),"2020:06:07 12:00:00");File.WriteAllBytes(Path.Combine(d,"camera.iiq"),Tiff("2017:06:21 10:20:30",true,0x9003));
            p=Plan(d,null,null,"原片项目","D04");Check(p.Folder=="170621-原片项目"&&p.Reason.Contains("相机原档拍摄元数据"),"IIQ TIFF capture preferred over JPEG preview");
            Check(Plan(d,"云杉影像",null,null,"D04").Folder=="170621-云杉影像","short Chinese volume plus capture date");
            Check(Plan(d,"SPRUCE",null,null,"D04").Folder=="170621-SPRUCE","short Latin volume plus capture date");
            Check(Plan(d,"DVD",null,null,"D04").Folder=="170621-RAW原档-D04","generic volume uses honest material description and disc ID");
            string e=Fresh("bigendian");File.WriteAllBytes(Path.Combine(e,"photo.tif"),Tiff("2011:11:12 10:00:00",false,0x9004));p=Plan(e,null,null,null,"D05");Check(p.Folder.StartsWith("111112-")&&p.Reason.Contains("数字化日期（0x9004）")&&p.Reason.Contains("不能视为拍摄日期")&&!p.Reason.Contains("照片EXIF拍摄元数据"),"big-endian digitized date remains weak evidence");
            string external=Fresh("far-offset-iiq");
            // Deliberately spaced synthetic offsets beyond a prefix-only metadata scan.
            const int dateOffset=45*1024*1024, exifOffset=dateOffset+128, rootOffset=dateOffset+512;
            byte[] far=new byte[rootOffset+128];byte[] near=Tiff("2016:04:09 20:53:53",true,0x9003);Array.Copy(near,0,far,0,8);
            Action<int,uint> far32=(at,value)=>{for(int n=0;n<4;n++)far[at+n]=(byte)(value>>(8*n));};
            far32(4,rootOffset);far[rootOffset]=1;far[rootOffset+2]=0x69;far[rootOffset+3]=0x87;far[rootOffset+4]=4;far32(rootOffset+6,1);far32(rootOffset+10,exifOffset);
            far[exifOffset]=1;far[exifOffset+2]=3;far[exifOffset+3]=0x90;far[exifOffset+4]=2;far32(exifOffset+6,20);far32(exifOffset+10,dateOffset);Array.Copy(Encoding.ASCII.GetBytes("2016:04:09 20:53:53\0"),0,far,dateOffset,20);
            string farPath=Path.Combine(external,"camera.IIQ");File.WriteAllBytes(farPath,far);File.SetLastWriteTime(farPath,new DateTime(2016,4,10));p=Plan(external,"SPRUCE",null,null,"DFAR");
            Check(p.Folder=="160409-SPRUCE"&&p.Reason.Contains("相机原档拍摄元数据")&&p.Reason.Contains("按IFD偏移定位"),"IIQ 45MB external IFD capture beats next-day modification");
            string weakRaw=Fresh("weak-raw-real-jpeg");File.WriteAllBytes(Path.Combine(weakRaw,"camera.iiq"),Tiff("2001:01:01 00:00:00",true,0x9004));Jpeg(Path.Combine(weakRaw,"photo.jpg"),"2016:04:09 20:53:53");p=Plan(weakRaw,"SPRUCE",null,null,"DWR");Check(p.Folder=="160409-SPRUCE"&&p.Reason.Contains("照片EXIF拍摄元数据"),"actual JPEG capture outranks RAW digitized date");
            string nested=Fresh("nested-exif");File.WriteAllBytes(Path.Combine(nested,"camera.nef"),NestedTiff());Check(Plan(nested,"SPRUCE",null,null,"DN").Folder=="100203-SPRUCE","realistic nested EXIF IFD pointer");
            string f=Fresh("jpeg");Jpeg(Path.Combine(f,"photo.jpg"),"2015:03:04 10:00:00");Jpeg(Path.Combine(f,"photo2.jpg"),"2015:03:05 10:00:00");p=Plan(f,null,null,null,"D06");Check(p.Folder.StartsWith("150304-")&&p.Reason.Contains("有分歧"),"JPEG EXIF and date disagreement transparency");
            string g=Fresh("software-edit");File.WriteAllBytes(Path.Combine(g,"photo.tif"),Tiff("2001:01:01 10:00:00",true,0x0132));File.SetLastWriteTime(Path.Combine(g,"photo.tif"),new DateTime(2022,4,5));p=Plan(g,null,null,null,"D07");Check(p.Folder.StartsWith("220405-")&&p.Reason.Contains("修改日期")&&!p.Reason.Contains("EXIF拍摄元数据"),"software DateTime never masquerades as capture");
            string h=Fresh("filename");File.WriteAllBytes(Path.Combine(h,"IMG_20180914.jpg"),new byte[]{1,2,3});p=Plan(h,null,null,null,"D08");Check(p.Folder.StartsWith("180914-")&&p.Reason.Contains("文件名日期"),"corrupt image falls back to filename date");
            Check(DiscPlan.Safe("CON")=="_CON"&&DiscPlan.Safe("A/B:C? ")=="A_B_C_"&&DiscPlan.Safe(new string('长',150)).Length<=110,"Windows safe reserved separators long names");
            string i=Fresh("limit");for(int n=0;n<300;n++)File.WriteAllText(Path.Combine(i,n+".txt"),"bounded");p=Plan(i,null,null,null,"D09");Check(p.Reason.Contains("有界采样"),"bounded file sampling");
            string majority=Fresh("majority");Jpeg(Path.Combine(majority,"a-old.jpg"),"2001:01:02 10:00:00");for(int n=0;n<7;n++)Jpeg(Path.Combine(majority,"b"+n+".jpg"),"2016:04:09 10:00:00");p=Plan(majority,"SPRUCE",null,null,"DM");Check(p.Folder=="160409-SPRUCE"&&p.Reason.Contains("多数日")&&p.Reason.Contains("有分歧"),"majority capture day defeats single old sample");
            string same=Fresh("same-customer");Directory.CreateDirectory(Path.Combine(same,"160409-云杉影像"));Directory.CreateDirectory(Path.Combine(same,"160410-云杉影像"));p=Plan(same,null,null,null,"DS");Check(p.TopFolders.Count==0&&!p.Folder.Contains("混装"),"same customer different dates is not mixed customers");
            string clash=Fresh("volume-clash");Directory.CreateDirectory(Path.Combine(clash,"160409-云杉影像"));p=Plan(clash,"170621-星河工作室",null,null,"DC");Check(p.TopFolders.Count==1&&p.TopFolders["160409-云杉影像"]=="160409-云杉影像"&&p.Folder.Contains("混装待归属"),"single other customer never merged into volume project");
            string typed=Fresh("date-material");File.WriteAllBytes(Path.Combine(typed,"camera.iiq"),new byte[]{1});p=Plan(typed,"170621",null,null,"DT");Check(p.Folder=="170621-RAW原档-DT","textual date still preserves material description and disc ID");
            Check(Plan(d,"星河工作室春季广告项目",null,null,"DL").Folder=="170621-星河工作室春季广告项目","long Chinese volume kept as readable project clue");
            foreach(string bad in new string[]{"新卷","无标题","DVD-R"})Check(Plan(a,bad,null,null,"DG").Folder=="240815-拍摄日未知-DG","generic volume rejected "+bad);
            string mod=Fresh("modified-majority");for(int n=0;n<4;n++){string path=Path.Combine(mod,n+".iiq");File.WriteAllBytes(path,new byte[]{1});File.SetLastWriteTime(path,n==0?new DateTime(2001,1,2):new DateTime(2016,4,9));}string cache=Path.Combine(mod,"CaptureOne","Cache");Directory.CreateDirectory(cache);Jpeg(Path.Combine(cache,"preview.jpg"),"2005:01:01 00:00:00");p=Plan(mod,"SPRUCE",null,null,"DMO");Check(p.Folder=="160409-SPRUCE"&&p.Reason.Contains("修改日期"),"RAW modification majority outranks old sample and excludes CaptureOne cache");
            string invalid=Fresh("default-time");string invalidFile=Path.Combine(invalid,"old.iiq");File.WriteAllBytes(invalidFile,new byte[]{1});File.SetLastWriteTime(invalidFile,new DateTime(1980,1,1));p=Plan(invalid,null,null,null,"DI");Check(p.Folder.Contains("拍摄日未知"),"invalid default filesystem time excluded");
            string consistent=Fresh("consistent");for(int n=0;n<20;n++)Jpeg(Path.Combine(consistent,n.ToString("00")+".jpg"),"2016:04:09 10:00:00");p=Plan(consistent,"SPRUCE",null,null,"DE");Check(p.Folder=="160409-SPRUCE"&&p.Reason.Contains("定位读取6个影像元数据")&&p.Reason.Contains("提前结束"),"six consistent samples reduce optical reads with disclosure");
            string spread=Fresh("spread");string oldDir=Path.Combine(spread,"a"),newDir=Path.Combine(spread,"z");Directory.CreateDirectory(oldDir);Directory.CreateDirectory(newDir);for(int n=0;n<20;n++)Jpeg(Path.Combine(oldDir,n.ToString("00")+".jpg"),"2001:01:02 10:00:00");for(int n=0;n<8;n++)Jpeg(Path.Combine(newDir,n.ToString("00")+".jpg"),"2016:04:09 10:00:00");p=Plan(spread,"SPRUCE",null,null,"DSP");Check(p.Reason.Contains("2001-01-02×6")&&p.Reason.Contains("2016-04-09×6"),"round robin sampling covers both directories before repeating");
            CancellationTokenSource cancel=new CancellationTokenSource();cancel.Cancel();bool stopped=false;try{DiscPlan.Decide(a,null,null,null,"D1",DateTime.Today,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancellation");
            Console.WriteLine("All synthetic plan tests passed.");return 0;
        } finally {Directory.Delete(work,true);}
    }
}
