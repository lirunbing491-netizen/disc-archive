using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscArchive;

internal static class IdentityTests
{
    private static int checks;
    private sealed class UnsupportedReplace : IOException {internal UnsupportedReplace(int code) {HResult=unchecked((int)0x80070000)|code;}}
    private static void Check(bool condition,string message) {if(!condition)throw new Exception("FAIL "+message);checks++;Console.WriteLine("PASS "+message);}
    private static string Dir(string root,string name) {string p=Path.Combine(root,name);Directory.CreateDirectory(p);return p;}
    private static void Record(string root,string id,string status,string canonical)
    {
        string job=Dir(Dir(root,"DiscArchive"),id);
        File.WriteAllText(Path.Combine(job,"entry.xml"),"<ArrayOfEntry><Entry><Id>"+id+"</Id><Status>"+status+"</Status><Project>合成项目</Project><CanonicalId>"+canonical+"</CanonicalId><DiscLabel>同名卷</DiscLabel><JobDir>C:\\obsolete\\external</JobDir></Entry></ArrayOfEntry>");
    }
    public static int Main()
    {
        string work=Dir(Path.GetTempPath(),"DiscIdentity-"+Guid.NewGuid().ToString("N"));
        try
        {
            string a=Dir(work,"media-a"), b=Dir(work,"media-b"), target=Dir(work,"archive");
            byte[] data=new byte[80000];for(int i=0;i<data.Length;i++)data[i]=(byte)(i%251);
            File.WriteAllBytes(Path.Combine(a,"1-1.dat"),data);File.WriteAllBytes(Path.Combine(b,"1-1.dat"),data);
            DiscIdentity first=DiscIdentity.Inspect(a,null,CancellationToken.None), same=DiscIdentity.Inspect(b,null,CancellationToken.None);
            Check(first.Complete && first.Key==same.Key,"mount path does not affect visible identity");
            Record(target,"A1","进行中","");first.Remember(target,"A1");
            List<DiscMatch> matches=same.Find(target,"NEW","同名卷",CancellationToken.None);
            Check(matches.Count==1 && matches[0].Strong && matches[0].Status=="进行中","interrupted indexed job matches with sampling evidence");
            Check(matches[0].JobDir==Path.Combine(target,"DiscArchive","A1"),"published path rebased to current root");
            File.WriteAllText(Path.Combine(matches[0].JobDir,"续导结果.xml"),"<continuation canonical='A1' entry='A2' status='部分完成'/>");
            Check(same.Find(target,"NEW","",CancellationToken.None)[0].Status=="部分完成","canonical continuation result overrides stale status");
            data[40000]^=0x7f;File.WriteAllBytes(Path.Combine(b,"1-1.dat"),data);
            DiscIdentity changed=DiscIdentity.Inspect(b,null,CancellationToken.None);
            Check(first.Key==changed.Key,"same path and length retain structural key");
            Check(changed.Find(target,"NEW","同名卷",CancellationToken.None).Count==0,"middle content difference rejects same-name same-length disc");
            Check(changed.ConflictsWith(target,"A1"),"strong content conflict blocks wrong original selection");
            File.WriteAllText(Path.Combine(target,"DiscArchive","A1","校验清单.tsv"),"F\t1-1.dat\t80000\t0\tunknown\n");
            Check(changed.Find(target,"NEW","同名卷",CancellationToken.None).Count==0,"strong sample conflict is never downgraded to an old-list candidate");
            File.Move(Path.Combine(b,"1-1.dat"),Path.Combine(b,"1-2.dat"));
            DiscIdentity second=DiscIdentity.Inspect(b,null,CancellationToken.None);
            Check(second.Key!=first.Key && second.ConflictsWith(target,"A1"),"1-1 and 1-2 structures differ");
            Record(target,"A2","观察记录","A1");same.Remember(target,"A2");
            Check(same.Find(target,"NEW","",CancellationToken.None).Count==1,"observation records do not duplicate physical candidate");
            CancellationTokenSource stop=new CancellationTokenSource();stop.Cancel();bool canceled=false;
            try{DiscIdentity.Inspect(a,null,stop.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"inspection observes cancellation");
            bool verifyFailed=false;try{DiscIdentity.Inspect(a,delegate{throw new IOException("media changed");},CancellationToken.None);}catch(InvalidOperationException ex){verifyFailed=ex.InnerException is IOException;}
            Check(verifyFailed,"media verify exception is never swallowed as unreadable sector");
            DiscIdentity missing=DiscIdentity.Inspect(Path.Combine(work,"unreadable-missing"),null,CancellationToken.None);
            Check(!missing.Complete && missing.Evidence.Contains("不妨碍"),"enumeration failure records incomplete evidence without blocking rescue");
            same.Complete=false;same.Remember(target,"A1");same.Complete=true;
            Check(changed.ConflictsWith(target,"A1"),"incomplete later inspection does not erase previous strong identity");
            string oldTarget=Dir(work,"legacy");Record(oldTarget,"OLD1","部分完成","");Record(oldTarget,"OLD2","完成","");Record(oldTarget,"LABELONLY","完成","");
            File.WriteAllText(Path.Combine(oldTarget,"DiscArchive","OLD1","校验清单.tsv"),"F\t1-1.dat\t80000\t0\tunknown\n");
            File.WriteAllText(Path.Combine(oldTarget,"DiscArchive","OLD2","校验清单.tsv"),"F\t1-2.dat\t80000\t0\tunknown\n");
            matches=same.Find(oldTarget,"NEW","同名卷",CancellationToken.None);
            Check(matches.Count==1 && matches[0].Id=="OLD1" && !matches[0].Strong,"legacy readable overlap distinguishes same volume labels and numbered discs");
            string rescueTarget=Dir(work,"old-rescue");Record(rescueTarget,"R1","部分完成","");
            string reports=Dir(Path.Combine(rescueTarget,"DiscArchive","R1"),"抢救记录-test");
            File.WriteAllText(Path.Combine(reports,"路径清单.tsv"),"文件\tE:\\\\1-1.dat\tZ:\\\\old.dat\t完整 80000 字节\n");
            Check(same.Find(rescueTarget,"NEW","",CancellationToken.None).Count==1,"old rescue path list compares source names without opening old absolute locations");
            Record(target,"C1","完成","");Record(target,"C2","部分完成","");
            Parallel.Invoke(delegate{first.Remember(target,"C1");},delegate{first.Remember(target,"C2");});
            Check(same.Find(target,"NEW","",CancellationToken.None).Count==3,"concurrent workstation writes preserve independent candidates");
            Check(!first.ConflictsWith(oldTarget,"OLD1"),"legacy records without strong signatures remain unknown");
            string noise=Dir(work,"noise-target");Record(noise,"NOISE","完成","");
            string tiny=Dir(work,"noise-source");File.WriteAllBytes(Path.Combine(tiny,"desktop.ini"),new byte[80000]);File.WriteAllBytes(Path.Combine(tiny,"empty.mov"),new byte[0]);
            File.WriteAllText(Path.Combine(noise,"DiscArchive","NOISE","校验清单.tsv"),"F\tdesktop.ini\t80000\t0\tunknown\nF\tempty.mov\t0\t0\tunknown\n");
            Check(DiscIdentity.Inspect(tiny,null,CancellationToken.None).Find(noise,"NEW","同名卷",CancellationToken.None).Count==0,"cache files and zero-byte placeholders never create weak candidates");
            string huge=Dir(work,"large-old-library");
            for(int i=0;i<600;i++) {string id="L"+i.ToString("D4");Record(huge,id,"完成","");File.WriteAllText(Path.Combine(huge,"DiscArchive",id,"校验清单.tsv"),"F\t"+(i==599?"1-1.dat":"other-"+i+".mov")+"\t80000\t0\tunknown\n");}
            Check(same.Find(huge,"NEW","同名卷",CancellationToken.None).Count==0,"first legacy batch remains bounded instead of scanning all 600 reports");
            for(int i=0;i<9;i++)same.Find(huge,"NEW","同名卷",CancellationToken.None);
            Check(same.Find(huge,"NEW","同名卷",CancellationToken.None).Exists(delegate(DiscMatch m){return m.Id=="L0599";}),"persistent legacy cursor reaches candidates beyond first 512 entries");
            File.Delete(Path.Combine(huge,"DiscArchive","L0599","校验清单.tsv"));
            Check(same.Find(huge,"NEW","同名卷",CancellationToken.None).Exists(delegate(DiscMatch m){return m.Id=="L0599";}),"cached path buckets find old candidate without rereading old material or report");
            Record(huge,"INSERTION","已完成","L0599");
            File.WriteAllText(Path.Combine(huge,"DiscArchive","INSERTION","校验清单.tsv"),"F\t1-1.dat\t80000\t0\tunknown\n");
            for(int i=0;i<10;i++)matches=same.Find(huge,"NEW","同名卷",CancellationToken.None);
            Check(!matches.Exists(delegate(DiscMatch m){return m.Id=="INSERTION";}),"canonical insertion alias never becomes independent legacy candidate");
            Action<string,string> replace=DiscIdentity.ReplaceFile;
            try {
                DiscIdentity.ReplaceFile=delegate(string source,string destination){throw new UnsupportedReplace(50);};
                first.Evidence+=" synthetic exFAT update";first.Remember(target,"A1");
                Check(File.Exists(Path.Combine(target,".discarchive-identity-v1","records","A1.txt.recover")),"unsupported atomic replace retains previous complete index record");
                Check(same.Find(target,"NEW","",CancellationToken.None).Exists(delegate(DiscMatch m){return m.Id=="A1";}),"unsupported replace fallback still publishes usable index");
                DiscIdentity.ReplaceFile=delegate(string source,string destination){throw new UnsupportedReplace(1);};
                first.Evidence+=" second unsupported code";first.Remember(target,"A1");
                Check(same.Find(target,"NEW","",CancellationToken.None).Exists(delegate(DiscMatch m){return m.Id=="A1";}),"invalid-function atomic replace also uses recoverable fallback");
            }finally {DiscIdentity.ReplaceFile=replace;}
            Console.WriteLine("PASS "+checks+" synthetic checks; fixtures retained: "+work);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Console.Error.WriteLine(work);return 1;}
    }
}
