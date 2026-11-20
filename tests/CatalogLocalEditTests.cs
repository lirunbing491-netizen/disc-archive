using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using DiscArchive;

public static class CatalogLocalEditTests
{
    private static void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); }
    private static Entry Row(Catalog c,string id) { return c.List().Single(x=>x.Id==id); }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"DiscArchive-CatalogLocalEdit-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Console.WriteLine("Artifacts: "+root);
        try {
            string state=Path.Combine(root,"state"),target=Path.Combine(root,"target"),photo=Path.Combine(root,"photo.jpg");
            Directory.CreateDirectory(target);
            using(Bitmap b=new Bitmap(32,24)) { using(Graphics g=Graphics.FromImage(b)) g.Clear(Color.Azure); b.Save(photo,ImageFormat.Jpeg); }
            Catalog book=new Catalog(state); Entry disc=book.Create(photo,target,"X:\\","实盘");
            string side=Path.Combine(state,"catalog-local.xml");
            Check(!File.Exists(side),"physical registration does not create a local maintenance side file");
            book.Save(disc);
            Check(!File.Exists(side),"ordinary physical Save does not create a local maintenance side file");
            string main=Path.Combine(state,"catalog.xml"), before=File.ReadAllText(main);
            bool rejected=false; try {book.AddManual(" ",null);} catch(ArgumentException) {rejected=true;}
            Check(rejected,"blank manual entry rejected");
            Entry manual=book.AddManual("手工标题",null),project=book.AddManual(null,"仅项目");
            Check(manual.ManualOnly && manual.Status=="手工补录" && manual.Files==0 && manual.Bytes==0 &&
                String.IsNullOrEmpty(manual.Drive) && String.IsNullOrEmpty(manual.TargetRoot) && String.IsNullOrEmpty(manual.JobDir) && String.IsNullOrEmpty(manual.Photo),"manual rows have no optical task or successful copy claims");
            Check(project.Title=="" && project.Project=="仅项目" && manual.Id!=project.Id && manual.Id!=disc.Id,"title-only and project-only accepted with unique allocated IDs");
            Check(File.ReadAllText(main)==before,"manual creation leaves legacy catalog unchanged");
            Entry staleManual=Row(book,manual.Id);
            book.SetHidden(manual.Id,true); book.Edit(staleManual,"修改补录","新项目");
            Check(Row(book,manual.Id).HiddenFromCatalog && !Row(book,manual.Id).PendingLabelSync && Row(book,manual.Id).Title=="修改补录","stale manual edit preserves removal without label sync");
            book.SetHidden(manual.Id,false);
            Check(File.ReadAllText(main)==before,"manual edit and restore leave legacy catalog unchanged");
            rejected=false; try {book.SetHidden(disc.Id,true);} catch(InvalidOperationException) {rejected=true;}
            Check(rejected && !Row(book,disc.Id).HiddenFromCatalog,"waiting physical task cannot be removed");
            foreach(string status in new [] {"正在归类","抢救中","等待选择","复制校验中"}) {
                disc.Status=status; book.Save(disc); rejected=false;
                try {book.SetHidden(disc.Id,true);} catch(InvalidOperationException) {rejected=true;}
                Check(rejected,"active status guarded: "+status);
            }
            disc.Status="已拷出"; disc.Files=1; disc.Bytes=7; book.Save(disc);
            Check(Row(book,disc.Id).ImportedLocally && Catalog.LocalImportedCount(book.List())==1,"successful physical import records local history and excludes manual rows");
            disc.Status="正在归类"; disc.Files=disc.Bytes=0; book.Save(disc);
            Check(Row(book,disc.Id).ImportedLocally && Catalog.LocalImportedCount(new Catalog(state).List())==1,"retry clearing current counts retains local history across reopen");
            disc.Status="已拷出"; disc.Files=1; disc.Bytes=7; book.Save(disc);
            string payload=Path.Combine(disc.JobDir,"preserved.dat"); File.WriteAllText(payload,"payload");
            string replicaPath=Path.Combine(disc.JobDir,"entry.xml"),replica=File.ReadAllText(replicaPath),mainBeforeHide=File.ReadAllText(main);
            Check(!replica.Contains("ImportedLocally"),"archive receipt does not transfer workstation import history");
            Entry stale=Row(book,disc.Id); book.SetHidden(disc.Id,true);
            Check(File.ReadAllText(replicaPath)==replica && File.ReadAllText(main)==mainBeforeHide && File.Exists(payload) && File.Exists(disc.Photo) && File.Exists(disc.OriginalPhoto),"physical removal preserves original record, receipt, photos and files");
            string csv=Path.Combine(root,"local.csv"),shared=Path.Combine(root,"shared.csv"); book.Export(csv);
            Check(!File.ReadAllText(csv).Contains(disc.Id) && File.ReadAllText(csv).Contains(manual.Id),"local export contains visible rows only");
            Check(book.ExportTarget(target,shared)==1 && File.ReadAllText(shared).Contains(disc.Id) && book.LookupOriginal(target,disc.Id).Id==disc.Id,"hidden physical disc remains in shared export and original lookup");
            string sideBeforeSave=File.ReadAllText(side); DateTime sideTime=File.GetLastWriteTimeUtc(side);
            stale.Note="worker outcome";
            using(FileStream sideReadOnly=new FileStream(side,FileMode.Open,FileAccess.Read,FileShare.Read)) book.Save(stale);
            Check(File.ReadAllText(side)==sideBeforeSave && File.GetLastWriteTimeUtc(side)==sideTime,"ordinary physical Save leaves side file unchanged even while side writes are locked");
            Check(Row(book,disc.Id).HiddenFromCatalog && !File.ReadAllText(replicaPath).Contains("HiddenFromCatalog"),"stale worker Save preserves local removal without leaking it into receipt");
            book.Edit(stale,"标题修改","项目修改");
            Check(Row(book,disc.Id).HiddenFromCatalog && Row(book,disc.Id).Files==1,"stale physical label edit preserves removal and copy outcome");
            // Simulate an older executable rewriting only its known physical catalog.
            XmlDocument legacy=new XmlDocument(); legacy.Load(main);
            foreach(XmlNode n in legacy.SelectNodes("//ManualOnly|//HiddenFromCatalog")) n.ParentNode.RemoveChild(n);
            legacy.Save(main);
            Catalog reopened=new Catalog(state);
            Check(Row(reopened,disc.Id).HiddenFromCatalog && Row(reopened,manual.Id).ManualOnly,"old-version catalog rewrite preserves side-file state");
            Entry staleHidden=Row(reopened,disc.Id); reopened.SetHidden(disc.Id,false); reopened.Save(staleHidden);
            Check(!Row(reopened,disc.Id).HiddenFromCatalog,"stale hidden snapshot cannot undo restoration");
            reopened.Export(csv); Check(File.ReadAllText(csv).Contains(disc.Id),"restored disc returns to local CSV");
            Entry worker=Row(book,disc.Id);
            Task save=Task.Run(delegate { for(int i=0;i<12;i++) {worker.Note="progress "+i; book.Save(worker);} });
            Task visibility=Task.Run(delegate { for(int i=0;i<12;i++) new Catalog(state).SetHidden(disc.Id,i%2==0); book.SetHidden(disc.Id,true); });
            Task.WaitAll(save,visibility);
            Check(Row(book,disc.Id).HiddenFromCatalog,"concurrent Save cannot overwrite last locked removal");
            Check(Catalog.LocalImportedCount(book.List())==1,"hidden records remain part of cumulative local import history");
            Check(!File.ReadAllText(main).Contains(manual.Id) && !File.ReadAllText(main).Contains("HiddenFromCatalog") && !File.ReadAllText(main).Contains("ManualOnly"),"legacy catalog contains physical records only and no local flags");
            Workstation.RecoverInterrupted(book);
            Check(Row(book,manual.Id).Status=="手工补录" && Row(book,manual.Id).Files==0,"workstation recovery leaves manual rows alone");
            Catalog other=new Catalog(Path.Combine(root,"second-workstation"));
            Entry continuation=other.Create(photo,target,"X:\\","跨电脑续导");
            continuation.CanonicalId=disc.Id; continuation.LatestRunId=continuation.Id;
            continuation.Status="已拷出"; continuation.Files=continuation.ReusedFiles=1; other.Save(continuation);
            Check(Catalog.LocalImportedCount(other.List())==0 && !Row(other,continuation.Id).ImportedLocally,"second workstation checking only reused files does not claim an import");
            continuation.Status="部分拷出"; continuation.Files=2; other.Save(continuation);
            Check(Catalog.LocalImportedCount(new Catalog(Path.Combine(root,"second-workstation")).List())==1,"second workstation newly recovering a file counts original disc once");
            Entry empty=other.Create(photo,target,"X:\\","真实完成空盘"); empty.Status="已拷出"; other.Save(empty);
            Check(Row(other,empty.Id).ImportedLocally && Catalog.LocalImportedCount(other.List())==2,"completed empty disc persists its own local history");
            Console.WriteLine("PASS: CatalogLocalEditTests"); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
