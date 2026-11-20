using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace DiscArchive
{
    // The lock covers the complete choice of routes, copy and publication of the final receipt.
    internal sealed class DiscSession : IDisposable
    {
        internal readonly string Folder;
        private readonly FileStream handle;
        private readonly Entry run;
        internal readonly Entry Original;

        internal static List<DiscMatch> Discover(DiscIdentity identity,Entry e,CancellationToken token)
        {
            // Two simultaneous first insertions with the same signature must see one another.
            // The short per-signature lock is released before asking the operator or reading files.
            string area=Path.Combine(e.TargetRoot,".discarchive-identity-v1","intake");
            Catalog.CheckNoLinks(area); Directory.CreateDirectory(area);
            string gate=Path.Combine(area,identity.Key+".lock"); Catalog.CheckNoLinks(gate);
            FileStream acquired=null;
            while(acquired==null) {
                token.ThrowIfCancellationRequested();
                try { acquired=new FileStream(gate,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch(IOException ex) {
                    int code=ex.HResult&65535;
                    if(code!=32 && code!=33)throw;
                    token.WaitHandle.WaitOne(70);
                }
            }
            using(acquired) {
                var matches=identity.Find(e.TargetRoot,e.Id,e.DiscLabel,token);
                if(matches.Count==0) identity.Remember(e.TargetRoot,e.Id);
                return matches;
            }
        }

        internal DiscSession(Catalog book, Entry entry, string canonical)
        {
            run=entry;
            Folder=Catalog.DiscFolder(entry.TargetRoot,canonical);
            string lockFile=Path.Combine(Folder,".continuation-session.lock");
            Catalog.CheckNoLinks(lockFile);
            try { handle=new FileStream(lockFile,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException e) {
                if((e.HResult&65535)==32 || (e.HResult&65535)==33)
                    throw new IOException("另一台光驱或工位正在处理原盘号 "+canonical+"。请等该任务结束后再续导。",e);
                throw;
            }
            try {
                Original=book.ReadOrigin(entry,canonical);
                if(!String.IsNullOrEmpty(Original.CanonicalId) && Original.CanonicalId!=canonical)
                    throw new IOException("所选登记是一条插盘历史记录，请使用其中的原盘号。");
            } catch { handle.Dispose(); throw; }
        }

        internal DiscPlan ReadPlan()
        {
            string path=Path.Combine(Folder,"续导方案.xml");
            Catalog.CheckNoLinks(path);
            if(!File.Exists(path)) { if(!File.Exists(path+".bak")) return null; path+=".bak"; Catalog.CheckNoLinks(path); }
            if(new FileInfo(path).Length>1048576) throw new IOException("原归档方案过大，请检查该盘记录。");
            var settings=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null };
            XElement x;
            using(var r=XmlReader.Create(path,settings)) x=XElement.Load(r);
            if(x.Name!="plan" || (string)x.Attribute("id")!=Original.Id || (string)x.Attribute("target")!=run.TargetId)
                throw new IOException("存储的方案与当前原盘或备份硬盘不符。");
            var plan=new DiscPlan { Folder=(string)x.Attribute("folder"),Reason="沿用原盘号 "+Original.Id+" 已确认的项目目录。" };
            ProjectPhoto.DirectoryFor(run.TargetRoot,plan.Folder);
            foreach(var item in x.Elements("top")) {
                string source=(string)item.Attribute("name"),project=(string)item.Attribute("folder");
                if(String.IsNullOrEmpty(source) || Path.GetFileName(source)!=source || source=="." || source=="..") throw new IOException("存储的源目录无效。");
                ProjectPhoto.DirectoryFor(run.TargetRoot,project); plan.TopFolders.Add(source,project);
            }
            return plan;
        }
        internal void SavePlan(DiscPlan plan)
        {
            string file=Path.Combine(Folder,"续导方案.xml");
            var x=new XElement("plan",new XAttribute("id",Original.Id),new XAttribute("target",run.TargetId),new XAttribute("folder",plan.Folder));
            foreach(var p in plan.TopFolders) x.Add(new XElement("top",new XAttribute("name",p.Key),new XAttribute("folder",p.Value)));
            Write(file,x);
        }
        internal void Publish(Entry e)
        {
            Write(Path.Combine(Folder,"续导结果.xml"),new XElement("continuation",new XAttribute("canonical",Original.Id),
                new XAttribute("entry",e.Id),new XAttribute("status",e.Status),new XAttribute("files",e.Files),new XAttribute("reused",e.ReusedFiles)));
        }
        private static void Write(string path,XElement data)
        {
            Catalog.CheckNoLinks(path);
            Catalog.AtomicWrite(path,delegate(Stream f) { data.Save(f); });
        }
        public void Dispose(){ handle.Dispose(); }
    }
}
