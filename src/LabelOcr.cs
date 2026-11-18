using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DiscArchive
{
    /// <summary>Optional, offline Windows OCR. Call from a worker thread.</summary>
    public static class LabelOcr
    {
        public static string Method { get { return "Windows 离线 OCR（盘面定位/浅色文档、双处理一致性筛选；不识别时留白）"; } }
        public static string Read(string photoPath)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath)) return String.Empty;
                string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabelOcr.ps1");
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"System32\WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(script) || !File.Exists(powershell)) return String.Empty;
                string fullPath = Path.GetFullPath(photoPath);
                ProcessStartInfo start = new ProcessStartInfo(powershell);
                start.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    Quote(script) + " -PhotoPath " + Quote(fullPath);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = new UTF8Encoding(false);
                start.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process process = Process.Start(start))
                {
                    // Drain both pipes concurrently, including errors on unavailable Windows versions.
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch { }
                        return String.Empty;
                    }
                    if (!Task.WaitAll(new Task[] { output, errors }, 1000) || process.ExitCode != 0)
                        return String.Empty;
                    return output.Result.Trim().Trim('\uFEFF');
                }
            }
            catch { return String.Empty; }
        }

        private static string Quote(string value)
        {
            // Windows filenames cannot contain quotes; escape trailing backslashes for argv.
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}
