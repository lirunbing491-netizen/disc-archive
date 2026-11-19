using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DiscArchive;

public static class CatalogTests
{
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static byte[] SampleJpegWithOrientation()
    {
        byte[] jpeg;
        using (Bitmap bitmap = new Bitmap(2000, 1000))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (MemoryStream stream = new MemoryStream())
        {
            graphics.Clear(Color.DarkBlue);
            graphics.FillRectangle(Brushes.Red, 0, 0, 500, 500);
            bitmap.Save(stream, ImageFormat.Jpeg);
            jpeg = stream.ToArray();
        }
        // A real EXIF APP1/TIFF record: orientation=6, rotate 90 degrees clockwise.
        byte[] exif = { 255,225,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0 };
        byte[] result = new byte[jpeg.Length + exif.Length];
        Array.Copy(jpeg, 0, result, 0, 2);
        Array.Copy(exif, 0, result, 2, exif.Length);
        Array.Copy(jpeg, 2, result, 2 + exif.Length, jpeg.Length - 2);
        return result;
    }

    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    public static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "DiscArchive-CatalogTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string target = Path.Combine(temp, "target");
            Directory.CreateDirectory(target);
            string photo = Path.Combine(temp, "source.jpg");
            byte[] sourceBytes = SampleJpegWithOrientation();
            File.WriteAllBytes(photo, sourceBytes);
            string state = Path.Combine(temp, "state");
            string sequence = Path.Combine(temp, "sequence");
            Catalog catalog = new Catalog(state, sequence);
            Entry first = catalog.Create(photo, target, "E:", "项目甲");
            Assert(first.Status == "等待放盘" && catalog.List().Count == 1, "create and persist");
            Assert(first.TitleState == "待确认", "new title awaits confirmation");
            Assert(File.Exists(first.Photo) && first.Photo != photo && first.Photo.EndsWith("thumbnail.jpg"), "independent local JPEG thumbnail");
            using (Image thumb = Image.FromFile(first.Photo))
            {
                Assert(thumb.Width == 600 && thumb.Height == 1200 && thumb.RawFormat.Guid == ImageFormat.Jpeg.Guid, "thumbnail size and EXIF orientation");
                using (Bitmap oriented = new Bitmap(thumb)) Assert(oriented.GetPixel(500, 100).R > 150, "orientation rotates visible red corner");
            }
            Assert(SameBytes(sourceBytes, File.ReadAllBytes(first.OriginalPhoto)), "archived original is byte-for-byte identical");
            string legacyState = Path.Combine(temp, "legacy-state");
            Directory.CreateDirectory(legacyState);
            System.Xml.XmlDocument legacy = new System.Xml.XmlDocument();
            legacy.Load(Path.Combine(state, "catalog.xml"));
            System.Xml.XmlNode oldOriginal = legacy.SelectSingleNode("/ArrayOfEntry/Entry/OriginalPhoto");
            oldOriginal.ParentNode.RemoveChild(oldOriginal);
            legacy.SelectSingleNode("/ArrayOfEntry/Entry/Photo").InnerText = photo;
            legacy.Save(Path.Combine(legacyState, "catalog.xml"));
            Entry legacyEntry = new Catalog(legacyState, sequence).List()[0];
            using (Image oldImage = Image.FromFile(legacyEntry.Photo)) Assert(legacyEntry.OriginalPhoto == null && oldImage.Width == 2000, "legacy photo remains readable without original path field");
            Assert(File.Exists(Path.Combine(first.JobDir, "photo.jpg")) && File.Exists(Path.Combine(first.JobDir, "登记信息.txt")), "target photo and readable entry");
            string originalMarker = File.ReadAllText(Path.Combine(target, ".discarchive-target.id"));
            catalog.Edit(first, "标题\"甲,乙\n下一行", "新项目");
            Assert(first.TitleState == "已确认", "manual edit confirms title");
            Assert(new Catalog(state, sequence).List()[0].Project == "新项目", "edit survives reload");
            Assert(File.ReadAllText(Path.Combine(first.JobDir, "登记信息.txt")).Contains("新项目"), "online replica updated");
            Assert(File.ReadAllText(Path.Combine(first.JobDir, "登记信息.txt")).Contains(first.OriginalPhoto), "readable record exposes original path");
            string badPhoto = Path.Combine(temp, "damaged.jpg");
            File.WriteAllBytes(badPhoto, new byte[] { 1, 2, 3 });
            int archiveCount = Directory.GetDirectories(Path.Combine(target, "DiscArchive")).Length;
            bool damagedRejected = false;
            try { catalog.Create(badPhoto, target, "E:", "损坏图片"); } catch (ArgumentException) { damagedRejected = true; }
            Assert(damagedRejected && catalog.List().Count == 1 && Directory.GetDirectories(Path.Combine(target, "DiscArchive")).Length == archiveCount, "damaged image creates no record or archive");
            bool writeRejected = false;
            using (FileStream locked = new FileStream(Path.Combine(state, "catalog.xml"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { catalog.Create(photo, target, "E:", "模拟登记失败"); } catch (IOException) { writeRejected = true; }
            }
            Assert(writeRejected && catalog.List().Count == 1 && Directory.GetDirectories(Path.Combine(target, "DiscArchive")).Length == archiveCount && Directory.GetDirectories(Path.Combine(state, "entries")).Length == 1, "failed catalog commit cleans only its new archive and thumbnail");
            string replica = File.ReadAllText(Path.Combine(first.JobDir, "entry.xml"));
            File.WriteAllText(Path.Combine(target, ".discarchive-target.id"), Guid.NewGuid().ToString("N"));
            first.Note = "同盘符其他盘测试";
            catalog.Save(first);
            Assert(!catalog.IsOriginalTarget(first) && File.ReadAllText(Path.Combine(first.JobDir, "entry.xml")) == replica, "wrong marker never updates replica");
            File.WriteAllText(Path.Combine(target, ".discarchive-target.id"), originalMarker);
            string offline = target + "-offline";
            Directory.Move(target, offline);
            first.Note = "离线修改";
            catalog.Save(first);
            Assert(catalog.List()[0].Note == "离线修改" && File.Exists(first.Photo), "offline local editing and photo access");
            using (Image offlineImage = Image.FromFile(first.Photo)) Assert(offlineImage.Width == 600 && offlineImage.Height == 1200, "offline thumbnail decodes independently of target disk");
            Directory.Move(offline, target);
            first.Note = " =HYPERLINK(\"x\")";
            catalog.Save(first);
            string csv = Path.Combine(temp, "export.csv");
            catalog.Export(csv);
            byte[] raw = File.ReadAllBytes(csv);
            string text = File.ReadAllText(csv);
            Assert(raw[0] == 239 && raw[1] == 187 && raw[2] == 191, "Excel UTF-8 BOM");
            Assert(text.Contains("\"' =HYPERLINK(\"\"x\"\")\"") && text.Contains("标题\"\"甲,乙\n下一行"), "CSV escaping and formula protection");
            Assert(text.Contains(first.OriginalPhoto), "CSV exposes original photo path");
            Catalog other = new Catalog(Path.Combine(temp, "other-state"), sequence);
            Entry second = other.Create(photo, target, "E:", "项目乙");
            Assert(second.Id != first.Id && second.TargetId == first.TargetId && second.JobDir != first.JobDir, "common sequence and independent archives");
            Task[] tasks = new Task[6];
            for (int i = 0; i < tasks.Length; i++) tasks[i] = Task.Factory.StartNew(delegate { catalog.Create(photo, target, "E:", "并行"); });
            Task.WaitAll(tasks);
            Assert(catalog.List().Count == 7, "concurrent creates preserve every record");
            Catalog independent = new Catalog(Path.Combine(temp, "independent-state"));
            Entry independentEntry = independent.Create(photo, target, "F:", "独立工位");
            Assert(independentEntry.TargetId == first.TargetId && independent.List().Count == 1 && catalog.List().Count == 7,
                "independent workstation keeps its own local catalog");
            string clonedIdentity = File.ReadAllText(Path.Combine(sequence, "registry.id"));
            Catalog[] clones = new Catalog[2];
            Entry[] cloneEntries = new Entry[2];
            Task[] cloneTasks = new Task[2];
            for (int i = 0; i < clones.Length; i++)
            {
                string cloneState = Path.Combine(temp, "clone-" + i);
                Directory.CreateDirectory(cloneState);
                File.WriteAllText(Path.Combine(cloneState, "registry.id"), clonedIdentity);
                File.WriteAllText(Path.Combine(cloneState, "sequence.txt"), "0");
                clones[i] = new Catalog(cloneState);
                int slot = i;
                cloneTasks[i] = Task.Factory.StartNew(delegate { cloneEntries[slot] = clones[slot].Create(photo, target, "G:", "克隆序列"); });
            }
            Task.WaitAll(cloneTasks);
            Assert(cloneEntries[0].Id != cloneEntries[1].Id && cloneEntries[0].Id != first.Id &&
                SameBytes(sourceBytes, File.ReadAllBytes(first.OriginalPhoto)), "cloned sequence collisions advance without replacing another station archive");
            Entry driveChange = catalog.List()[0];
            driveChange.Drive = "Z:";
            catalog.Save(driveChange);
            System.Xml.XmlDocument movedDriveReplica = new System.Xml.XmlDocument();
            movedDriveReplica.Load(Path.Combine(driveChange.JobDir, "entry.xml"));
            Assert(catalog.List()[0].Drive == "Z:" && movedDriveReplica.SelectSingleNode("/ArrayOfEntry/Entry/Drive").InnerText == "Z:",
                "same registered disc can move to another optical drive and update its own replica");
            Entry wrongItem = catalog.List()[0];
            wrongItem.Id = catalog.List()[1].Id;
            bool wrongItemRejected = false;
            try { catalog.Save(wrongItem); } catch (InvalidOperationException) { wrongItemRejected = true; }
            Assert(wrongItemRejected, "changing drive does not permit saving into another registered disc");
            string targetCsv = Path.Combine(temp, "target.csv");
            string publishedReplica = Path.Combine(independentEntry.JobDir, "entry.xml");
            System.Xml.XmlDocument shared = new System.Xml.XmlDocument();
            shared.Load(publishedReplica);
            shared.SelectSingleNode("/ArrayOfEntry/Entry/Photo").InnerText = "C:\\another-machine\\private.jpg";
            shared.SelectSingleNode("/ArrayOfEntry/Entry/OriginalPhoto").InnerText = "C:\\another-machine\\private.jpg";
            shared.SelectSingleNode("/ArrayOfEntry/Entry/TargetRoot").InnerText = "Q:\\other-machine";
            shared.SelectSingleNode("/ArrayOfEntry/Entry/JobDir").InnerText = "Q:\\other-machine\\DiscArchive\\" + independentEntry.Id;
            System.Xml.XmlElement reportNode = shared.CreateElement("Report");
            reportNode.InnerText = "Q:\\other-machine\\DiscArchive\\" + independentEntry.Id + "\\抢救记录\\子目录";
            shared.DocumentElement.FirstChild.AppendChild(reportNode);
            System.Xml.XmlElement folders = shared.CreateElement("ArchiveFolders");
            System.Xml.XmlElement folderNode = shared.CreateElement("string");
            folderNode.InnerText = "项目共享"; folders.AppendChild(folderNode);
            shared.DocumentElement.FirstChild.AppendChild(folders);
            shared.SelectSingleNode("/ArrayOfEntry/Entry/RegistrationId").ParentNode.RemoveChild(shared.SelectSingleNode("/ArrayOfEntry/Entry/RegistrationId"));
            shared.Save(publishedReplica);
            string pendingDir = Path.Combine(target, "DiscArchive", "DA-unpublished.pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pendingDir);
            File.WriteAllText(Path.Combine(pendingDir, "entry.xml"), "incomplete");
            string relocated = target + "-current-access";
            Directory.Move(target, relocated);
            int targetCount = independent.ExportTarget(relocated, targetCsv);
            string targetText = File.ReadAllText(targetCsv);
            byte[] targetBytes = File.ReadAllBytes(targetCsv);
            Assert(targetCount == 11 && targetBytes[0] == 239 && targetBytes[1] == 187 && targetBytes[2] == 191,
                "target export includes all stations and legacy replica with UTF-8 BOM, excludes pending");
            Assert(targetText.Contains(Path.Combine(relocated, "项目共享")) && targetText.Contains(Path.Combine(relocated, "DiscArchive", independentEntry.Id, "photo.jpg")) &&
                targetText.Contains(Path.Combine(relocated, "DiscArchive", independentEntry.Id, "抢救记录", "子目录")) &&
                !targetText.Contains("another-machine") && !targetText.Contains("other-machine") && targetText.Contains("\"F:\"") && targetText.Contains("' =HYPERLINK"),
                "target export rebases actual directories and photos and protects shared CSV formula text");
            shared.SelectSingleNode("/ArrayOfEntry/Entry/TargetRoot").InnerText = "\\\\old-workstation\\old-share";
            shared.SelectSingleNode("/ArrayOfEntry/Entry/JobDir").InnerText = "\\\\old-workstation\\old-share\\DiscArchive\\" + independentEntry.Id;
            shared.SelectSingleNode("/ArrayOfEntry/Entry/Report").InnerText = "\\\\old-workstation\\old-share\\DiscArchive\\" + independentEntry.Id + "\\抢救记录\\子目录";
            shared.Save(Path.Combine(relocated, "DiscArchive", independentEntry.Id, "entry.xml"));
            Assert(independent.ExportTarget(relocated, targetCsv) == targetCount && File.ReadAllText(targetCsv) == targetText,
                "stored UNC source paths rebase identically without consulting the old network share");
            string brokenReplica = Path.Combine(relocated, "DiscArchive", cloneEntries[0].Id, "entry.xml");
            string intactReplica = File.ReadAllText(brokenReplica);
            File.WriteAllText(brokenReplica, "broken shared XML");
            bool sharedRejected = false;
            try { independent.ExportTarget(relocated, targetCsv); }
            catch (IOException error) { sharedRejected = error.Message.Contains(cloneEntries[0].Id); }
            Assert(sharedRejected && File.ReadAllText(targetCsv) == targetText, "corrupt published record fails visibly without replacing previous export");
            File.WriteAllText(brokenReplica, intactReplica);
            shared.SelectSingleNode("/ArrayOfEntry/Entry/ArchiveFolders/string").InnerText = "..\\outside";
            shared.Save(Path.Combine(relocated, "DiscArchive", independentEntry.Id, "entry.xml"));
            sharedRejected = false;
            try { independent.ExportTarget(relocated, targetCsv); } catch (IOException) { sharedRejected = true; }
            Assert(sharedRejected && File.ReadAllText(targetCsv) == targetText, "shared project paths cannot escape the selected target");
            Directory.Move(relocated, target);
            string legacyReplicaPath = Path.Combine(cloneEntries[0].JobDir, "entry.xml");
            string legacyCatalogPath = Path.Combine(temp, "clone-0", "catalog.xml");
            foreach (string legacyPath in new string[] { legacyReplicaPath, legacyCatalogPath })
            {
                System.Xml.XmlDocument oldRecord = new System.Xml.XmlDocument(); oldRecord.Load(legacyPath);
                System.Xml.XmlNode registration = oldRecord.SelectSingleNode("/ArrayOfEntry/Entry/RegistrationId");
                registration.ParentNode.RemoveChild(registration); oldRecord.Save(legacyPath);
            }
            Entry legacyDriveChange = clones[0].List()[0]; legacyDriveChange.Drive = "H:";
            clones[0].Save(legacyDriveChange);
            System.Xml.XmlDocument oldReplicaReadback = new System.Xml.XmlDocument(); oldReplicaReadback.Load(legacyReplicaPath);
            Assert(clones[0].List()[0].Drive == "H:" && oldReplicaReadback.SelectSingleNode("/ArrayOfEntry/Entry/Drive").InnerText == "H:",
                "legacy registered disc without RegistrationId can retry using another optical drive");
            string otherReplica = Path.Combine(cloneEntries[1].JobDir, "entry.xml");
            System.Xml.XmlDocument foreign = new System.Xml.XmlDocument(); foreign.Load(otherReplica);
            foreign.SelectSingleNode("/ArrayOfEntry/Entry/RegistrationId").InnerText = Guid.NewGuid().ToString("N");
            foreign.Save(otherReplica);
            string foreignText = File.ReadAllText(otherReplica);
            bool ownershipRejected = false;
            try { clones[1].Save(cloneEntries[1]); } catch (IOException) { ownershipRejected = true; }
            Assert(ownershipRejected && File.ReadAllText(otherReplica) == foreignText, "saving cannot overwrite a foreign registration replica");
            Entry invalid = catalog.List()[0];
            invalid.JobDir = temp;
            bool rejected = false;
            try { catalog.Save(invalid); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "reject changed destination");
            string xml = Path.Combine(state, "catalog.xml");
            Assert(File.Exists(xml + ".bak"), "atomic replacement backup");
            File.WriteAllText(xml, "broken XML");
            rejected = false;
            try { catalog.List(); } catch (IOException) { rejected = true; }
            Assert(rejected, "damaged primary fails closed without losing evidence");
            File.Move(Path.Combine(sequence, "sequence.txt"), Path.Combine(sequence, "sequence-hidden.txt"));
            rejected = false;
            try { other.Create(photo, target, "E:", "不应登记"); } catch (IOException) { rejected = true; }
            Assert(rejected, "missing sequence never resets old disc numbers");
            Console.WriteLine("All catalog tests passed. Test directory: " + temp);
            return 0;
        }
        finally
        {
            // Delete only this test-created directory, never production paths.
            if (Path.GetFileName(temp).StartsWith("DiscArchive-CatalogTests-", StringComparison.Ordinal)) Directory.Delete(temp, true);
        }
    }
}
