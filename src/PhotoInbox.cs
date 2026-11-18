using System;
using System.IO;
using System.Windows.Forms;

namespace DiscArchive
{
    internal sealed partial class MainForm
    {
        private void ToggleWatch()
        {
            if (watcher != null) { EndWatch(); return; }
            if (!Directory.Exists(target.Text) || String.IsNullOrEmpty(drives.Text))
                throw new IOException("请先选择归档硬盘和光驱。");
            string path = Folder("选择新盘面照片会出现在这台电脑上的文件夹。已有照片不会被自动接收。", "");
            if (path != null) BeginWatch(path);
        }
        private void BeginWatch(string folder)
        {
            EndWatch();
            watcher = new FileSystemWatcher(folder) {
                IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName, SynchronizingObject = this
            };
            watcher.Created += delegate(object sender, FileSystemEventArgs e) { if (sender == watcher) NewImage(e.FullPath); };
            watcher.Renamed += delegate(object sender, RenamedEventArgs e) { if (sender == watcher) NewImage(e.FullPath); };
            watcher.Error += delegate(object sender, ErrorEventArgs e) { if (sender == watcher) PauseInbox("照片文件夹失去连接或一次收到太多文件。"); };
            watcher.EnableRaisingEvents = true;
            watchButton.Text = "停止接收新照片";
            watchLabel.Text = "正在接收新照片：" + folder;
        }
        private void EndWatch()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            nextPhoto = null;
            watchButton.Text = "自动接收照片…";
            watchLabel.Text = "照片来源：手动选择或拖入";
        }
        private void PauseInbox(string why)
        {
            EndWatch(); station.BlockAutomatic();
            watchLabel.Text = "自动接收已暂停。 " + why + " 请手动核对照片。";
            if (Active && !station.Busy) info.Text = "请核对屏幕上的照片，或停止这张后重新选图。";
        }
        private void NewImage(string path)
        {
            if (watcher == null || !IsPhoto(path)) return;
            if (Active) { PauseInbox("上一张盘尚未结束时收到了新照片。"); return; }
            if (nextPhoto != null && !String.Equals(nextPhoto,path,StringComparison.OrdinalIgnoreCase))
            { PauseInbox("同时收到了多张照片。"); return; }
            nextPhoto = path;
            settleUntil = DateTime.UtcNow.AddSeconds(1.2);
            photoDeadline = DateTime.UtcNow.AddSeconds(45);
        }
        private void TakeNewPhoto()
        {
            if (nextPhoto == null || DateTime.UtcNow < settleUntil) return;
            string path = nextPhoto;
            try
            {
                using (FileStream stream = File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None))
                    if (stream.Length == 0) throw new IOException("照片暂时为空。");
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= photoDeadline) PauseInbox("照片一直没有传输完成。");
                return;
            }
            catch (UnauthorizedAccessException ex) { PauseInbox(ex.Message); return; }
            nextPhoto = null;
            try { Register(path); }
            catch (Exception ex) { PauseInbox(ex.Message); }
        }
    }
}
