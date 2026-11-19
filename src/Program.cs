using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DiscArchive
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (Mutex instance = new Mutex(true, @"Local\DiscArchive.Workstation.v1", out first))
            {
                if (!first) { MessageBox.Show("已有一个导盘助手窗口在运行。多光驱版可在同一个窗口选择多台光驱；如当前用的是旧版，请等任务结束并退出后再打开测试版。", "导盘助手"); return; }
                try
                {
                    string previous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscArchive", "工作站");
                    string state = PrepareTrial(previous);
                    Application.Run(new MainForm(state, new SystemReader()));
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "导盘助手无法继续", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
        internal static string PrepareTrial(string previous)
        {
            string trial=Path.Combine(previous,"多光驱版");
            if(Directory.Exists(trial)) return trial;
            Directory.CreateDirectory(previous);
            string v1=Path.Combine(previous,"抢救版");
            string source=Directory.Exists(v1) ? v1 : previous;
            string stage=Path.Combine(previous,"多光驱版-初始化-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            // v1 cannot round-trip the new XML fields. Isolate the catalog while keeping
            // existing records and their thumbnail paths accessible in the parent directory.
            foreach(string name in new[]{"catalog.xml","catalog.xml.bak","sequence.txt","设置.xml"})
            {
                string src=Path.Combine(source,name);
                if(File.Exists(src)) File.Copy(src,Path.Combine(stage,name),false);
            }
            Directory.Move(stage,trial);
            return trial;
        }
    }
}
