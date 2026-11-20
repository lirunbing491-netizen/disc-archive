using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DiscArchive;

internal static class ArchiveTreeTests
{
    private static string work;
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static ArchiveItem Item(int index,string name,int parent,bool directory=false)
    {return new ArchiveItem {Index=index,Name=name,Path="handler-path-"+index,ParentIndex=parent,IsDirectory=directory,Size=directory?0:4,Modified=new DateTime(2010,6,7)};}
    private static string Root(){return Path.Combine(Path.GetTempPath(),"AbsentArchiveTree-"+Guid.NewGuid().ToString("N"));}
    private static ArchiveDiscSource Source(string root,IList<ArchiveItem> items,string label="Volume",string format="HFS+")
    {return new ArchiveDiscSource(root,items,delegate(int index){return new MemoryStream(new byte[]{(byte)index,2,3,4},false);},label,format);}
    private static List<string> Entries(DiscSource source,string path){return new List<string>(source.Entries(path));}
    private static List<string> Files(DiscSource source,string root)
    {
        List<string> files=new List<string>();foreach(string path in source.Entries(root))
            if((source.Attributes(path)&FileAttributes.Directory)!=0)files.AddRange(Files(source,path));else files.Add(path);
        return files;
    }
    private static RescueResult Copy(DiscSource source,string root,string name)
    {
        string target=Path.Combine(work,name);Directory.CreateDirectory(target);
        return RescueCopy.Run(root,target,"Project",null,"TREE",Path.Combine(work,name+"-records"),null,null,CancellationToken.None,null,false,source);
    }
    private static void Test(string name,Action action){action();Console.WriteLine("PASS "+name);}
    public static int Main()
    {
        work=Path.Combine(Path.GetTempPath(),"ArchiveTreeTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try
        {
            Test("HFS raw parent graph removes proven volume shell and preserves literal separators",delegate{
                string root=Root();ArchiveItem shell=Item(0,"Volume",-1,true),dir=Item(1,"Projects",0,true),file=Item(2,"literal\\slash:colon.txt",1);
                ArchiveDiscSource source=Source(root,new[]{file,shell,dir});List<string> top=Entries(source,root);
                Assert(top.Count==1 && Path.GetFileName(top[0])=="Projects","volume shell or hierarchy incorrect");
                List<string> files=Files(source,root);Assert(files.Count==1 && Path.GetDirectoryName(files[0])==top[0],"literal slash became hierarchy");
                Assert(Path.GetFileName(files[0]).Contains("~005C") && Path.GetFileName(files[0]).Contains("~003A"),"literal characters not escaped");
                Assert(source.VolumeLabel=="Volume" && source.OriginalRoot=="Volume","volume and removed shell not separately retained");
                Assert(Copy(source,root,"hfs").Files==1,"real copy workflow");
            });
            Test("ordinary first level never stripped without raw volume evidence",delegate{
                string root=Root();ArchiveDiscSource source=Source(root,new[]{Item(0,"Project",-1,true),Item(1,"file.txt",0)},"Volume");
                Assert(Path.GetFileName(Entries(source,root)[0])=="Project" && source.OriginalRoot==null,"ordinary first level stripped");
                string second=Root();ArchiveDiscSource noChildren=Source(second,new[]{Item(0,"Volume",-1,true)});
                Assert(Entries(noChildren,second).Count==1 && noChildren.OriginalRoot==null,"unproven lone empty folder stripped");
            });
            Test("ISO backslash paths rebuild real hierarchy even when children precede directories",delegate{
                string root=Root();ArchiveItem file=Item(0,"file.txt",-1);file.Path="Photos\\Day\\file.txt";
                ArchiveItem day=Item(1,"Day",-1,true);day.Path="Photos\\Day";ArchiveItem photo=Item(2,"Photos",-1,true);photo.Path="Photos";
                ArchiveDiscSource source=Source(root,new[]{file,day,photo},"Photos","ISO9660");
                Assert(Entries(source,root).Count==1 && Files(source,root).Count==1,"ISO implicit directories duplicated");
                string path=Files(source,root)[0];Assert(path==Path.Combine(root,"Photos","Day","file.txt"),"ISO hierarchy flattened");
                Assert(Copy(source,root,"iso").Files==1,"ISO copy workflow");
            });
            Test("case and NFC conflicts plus unsafe names preserve every independent item",delegate{
                string root=Root();string[] names={"A.txt","a.txt","café.txt","cafe\u0301.txt","CON","con.txt","..","../escape","x\u0001y","x:y","x\\y","tail.","tail ","~DiscArchive-preserved",new string('z',400)};
                List<ArchiveItem> items=new List<ArchiveItem>();for(int i=0;i<names.Length;i++)items.Add(Item(i,names[i],-1));
                ArchiveDiscSource source=Source(root,items,"","HFS+");List<string> files=Files(source,root);HashSet<string> canonical=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(string file in files){Assert(file.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal),"source escape");Assert(canonical.Add(file.Normalize(NormalizationForm.FormC)),"canonical collision");}
                Assert(files.Count==names.Length,"items silently dropped");RescueResult result=Copy(source,root,"unsafe");Assert(result.Files==names.Length && result.Failed==0,"safe exports lost");
                string reports=Path.Combine(work,"mapping");source.WriteDescription(reports);string[] descriptions=Directory.GetFiles(reports,"*.tsv");Assert(descriptions.Length==1,"mapping missing");
                string text=File.ReadAllText(descriptions[0]);Assert(text.Contains("~device-CON") && text.Contains("~item-1") && text.Contains("\\u0001"),"mapping lacks escapes/conflicts/original metadata");
                ArchiveDiscSource same=Source(root,items,"","HFS+");Assert(String.Join("|",files.ToArray())==String.Join("|",Files(same,root).ToArray()),"mapping unstable");
            });
            Test("only explicit stream and auxiliary flags create protected sidecars",delegate{
                string root=Root();ArchiveItem file=Item(0,"photo.jpg",-1),stream=Item(1,"rsrc",0),aux=Item(2,"FinderInfo",0),ordinary=Item(3,"._photo.jpg",-1),colon=Item(4,"photo.jpg:rsrc",-1);
                stream.IsAltStream=true;stream.ParentIsAltStream=true;aux.IsAux=true;
                ArchiveDiscSource source=Source(root,new[]{file,stream,aux,ordinary,colon},"","HFS+");List<string> files=Files(source,root);Assert(files.Count==5,"fork discarded");int sidecars=0;
                foreach(string path in files)if(path.Contains("~DiscArchive-preserved"+Path.DirectorySeparatorChar))sidecars++;
                Assert(sidecars==2,"filename heuristics misclassified ordinary files or forks");RescueResult result=Copy(source,root,"forks");Assert(result.Files==5 && result.Failed==0,"fork bytes not copied");
                Assert(File.ReadAllBytes(Directory.GetFiles(Path.Combine(work,"forks","Project"),"photo.jpg",SearchOption.AllDirectories)[0])[0]==0,"main fork changed");
                source.WriteDescription(Path.Combine(work,"fork-mapping"));string text=File.ReadAllText(Directory.GetFiles(Path.Combine(work,"fork-mapping"),"*.tsv")[0]);Assert(text.Contains("rsrc") && text.Contains("True"),"sidecar association missing");
            });
            Test("links, damaged directories, missing parents and cycles fail explicitly while healthy sibling copies",delegate{
                string root=Root();ArchiveItem good=Item(0,"good.bin",-1),sym=Item(1,"sym",-1),hard=Item(2,"hard",-1),badDir=Item(3,"bad-dir",-1,true),child=Item(4,"unread.bin",3),orphan=Item(5,"orphan",99),cycleA=Item(6,"cycle-a",7),cycleB=Item(7,"cycle-b",6);
                sym.IsLink=true;sym.LinkTarget="../../outside";hard.IsHardLink=true;badDir.Error="synthetic directory metadata failure";
                ArchiveDiscSource source=Source(root,new[]{good,sym,hard,badDir,child,orphan,cycleA,cycleB},"","HFS+");
                RescueResult result=Copy(source,root,"issues");Assert(result.Files==1 && result.Failed>=6,"issues became empty successes or stopped healthy sibling: "+result.Files+"/"+result.Failed);
                Assert(Directory.GetFiles(Path.Combine(work,"issues","Project"),"*",SearchOption.AllDirectories).Length==1,"link was published");
                source.WriteDescription(Path.Combine(work,"issue-mapping"));string text=File.ReadAllText(Directory.GetFiles(Path.Combine(work,"issue-mapping"),"*.tsv")[0]);Assert(text.Contains("../../outside") && text.Contains("成环") && text.Contains("synthetic directory metadata failure"),"failure metadata absent");
            });
            Test("deep ISO paths relocate safely and missing raw HFS names remain explicit failures",delegate{
                string root=Root();StringBuilder deep=new StringBuilder();for(int i=0;i<25;i++)deep.Append("directory-"+i+"\\");deep.Append("file.bin");
                ArchiveItem file=Item(0,"file.bin",-1);file.Path=deep.ToString();ArchiveDiscSource source=Source(root,new[]{file},"","UDF");
                List<string> files=Files(source,root);Assert(files.Count==1 && files[0].Length<=235,"deep fallback not bounded");Assert(Copy(source,root,"deep").Files==1,"deep file not exported");
                string hfs=Root();ArchiveItem missing=Item(0,"",-1);missing.Path="Volume\\ambiguous\\name";
                ArchiveDiscSource uncertain=Source(hfs,new[]{missing},"Volume","HFS+");RescueResult failure=Copy(uncertain,hfs,"missing-name");Assert(failure.Files==0 && failure.Failed==1,"ambiguous HFS path silently treated as hierarchy");
            });
            Test("native HFS file and directory placeholders are refused by exact raw component chains",delegate{
                string root=Root();ArchiveItem volume=Item(0,"Volume",-1,true),good=Item(1,"good.bin",0),link=Item(2,"literal\\slash:link",0),dirLink=Item(3,"directory-link",0,true),child=Item(4,"child.bin",3),fork=Item(5,"rsrc",2);
                link.Size=0;fork.IsAltStream=true;fork.ParentIsAltStream=true;fork.Size=0;int opened=0;
                HfsGuardResult guard=new HfsGuardResult {Audited=true,Complete=false,Links=2,Entries=new[]{
                    new HfsNativeLink {CNID=20,LinkReference=100,Components=new[]{"Volume","literal\\slash:link"}},
                    new HfsNativeLink {CNID=30,LinkReference=200,Directory=true,Components=new[]{"Volume","directory-link"}}}};
                ArchiveDiscSource source=new ArchiveDiscSource(root,new[]{volume,good,link,dirLink,child,fork},delegate(int index){Assert(index==1,"guarded placeholder reached item opener");opened++;return new MemoryStream(new byte[4],false);},"Volume","HFS+",guard);
                RescueResult result=Copy(source,root,"guarded");Assert(result.Files==1 && result.Failed==3 && opened==1,"native links became zero byte successes: "+result.Files+"/"+result.Failed);
                string reportDirectory=Path.GetDirectoryName(result.Report);string failureText=File.ReadAllText(Path.Combine(reportDirectory,"失败清单.tsv"));Assert(failureText.Contains("硬链接占位") && failureText.Contains("CNID=20") && failureText.Contains("目录"),"native guard missing failure receipt");
                source.WriteDescription(Path.Combine(work,"guard-mapping"));string mapping=File.ReadAllText(Directory.GetFiles(Path.Combine(work,"guard-mapping"),"*.tsv")[0]);Assert(mapping.Contains("CNID=30") && mapping.Contains("派生条目"),"guard missing directory or sidecar association");
            });
            Test("unmatched native links and incomplete audits produce partial results without blocking readable siblings",delegate{
                foreach(bool unmatched in new[]{true,false})
                {
                    string root=Root();ArchiveItem volume=Item(0,"Volume",-1,true),good=Item(1,"good.bin",0);
                    HfsGuardResult guard=new HfsGuardResult {Audited=unmatched,Complete=false,Links=unmatched?1:0,Note="synthetic audit limitation",Entries=unmatched?new[]{new HfsNativeLink {Components=new[]{"Volume\\good.bin"}}}:new HfsNativeLink[0]};
                    ArchiveDiscSource source=new ArchiveDiscSource(root,new[]{volume,good},delegate(int index){return new MemoryStream(new byte[4],false);},"Volume","HFS+",guard);
                    RescueResult result=Copy(source,root,unmatched?"guard-unmatched":"guard-incomplete");Assert(result.Files==1 && result.Failed==1,"unconfirmed guard must mark root partial and keep readable sibling");
                }
            });
            Test("complete zero-link HFS audits and non-HFS imports remain successful",delegate{
                foreach(string format in new[]{"HFS+","UDF","ISO9660"})
                {
                    string root=Root();ArchiveItem good=Item(0,"good.bin",-1);good.Path="good.bin";
                    HfsGuardResult guard=new HfsGuardResult {Audited=true,Complete=true,Links=0,Entries=new HfsNativeLink[0]};
                    ArchiveDiscSource source=new ArchiveDiscSource(root,new[]{good},delegate(int index){return new MemoryStream(new byte[4],false);},"",format,guard);
                    RescueResult result=Copy(source,root,"clean-"+format);Assert(result.Files==1 && result.Failed==0,"clean archive falsely partial");
                    string mappingDirectory=Path.Combine(work,"clean-map-"+format);source.WriteDescription(mappingDirectory);string mapping=File.ReadAllText(Directory.GetFiles(mappingDirectory,"*.tsv")[0]);
                    if(format!="HFS+")Assert(!mapping.Contains("HFS"),"non-HFS description mentions HFS support");
                }
            });
            Test("metadata-only construction and per-file opening retain original modified dates",delegate{
                string root=Root();int opened=0;ArchiveItem file=Item(0,"date.raw",-1);
                ArchiveDiscSource source=new ArchiveDiscSource(root,new[]{file},delegate(int index){opened++;return new MemoryStream(new byte[4],false);},"","HFS+");
                List<string> files=Files(source,root);Assert(opened==0 && source.Modified(files[0])==file.Modified,"construction extracted payload or lost dates");
                using(Stream input=source.OpenRead(files[0]))Assert(input.Length==4 && input.CanSeek && !input.CanWrite,"source stream contract");Assert(opened==1,"per-file opening");
            });
            Console.WriteLine("Generated fixtures: "+work);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Console.Error.WriteLine("Generated fixtures: "+work);return 1;}
    }
}
