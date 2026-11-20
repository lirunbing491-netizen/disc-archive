using System;
using System.Diagnostics;
using System.IO;

namespace DiscArchive
{
    // The helper is a bundled native executable; never resolve it through PATH or a shell.
    internal static class PortableSupport
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitializeRuntime()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        internal static Func<Stream, byte[]> ThumbnailForTests;
        internal static byte[] MakeThumbnail(Stream source)
        {
            if (ThumbnailForTests != null) return ThumbnailForTests(source);
            string helper = Environment.GetEnvironmentVariable("DISCARC_MEDIA_HELPER");
            if (String.IsNullOrEmpty(helper) || !Path.IsPathFullyQualified(helper) || !File.Exists(helper))
                throw new IOException("未找到本机照片处理组件，登记尚未开始。");
            string temporary = Path.Combine(Path.GetTempPath(), "discarchive-photo-" + Guid.NewGuid().ToString("N"));
            if(OperatingSystem.IsWindows()) Directory.CreateDirectory(temporary);
            else Directory.CreateDirectory(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            string input = Path.Combine(temporary,"source"), output = Path.Combine(temporary,"thumbnail.jpg");
            try
            {
                using (FileStream f = new FileStream(input,FileMode.CreateNew,FileAccess.Write,FileShare.None)) source.CopyTo(f);
                var start = new ProcessStartInfo(helper) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
                start.ArgumentList.Add("thumbnail"); start.ArgumentList.Add(input); start.ArgumentList.Add(output);
                using (Process process=Process.Start(start))
                {
                    var stdout=process.StandardOutput.ReadToEndAsync();
                    var stderr=process.StandardError.ReadToEndAsync();
                    if(!process.WaitForExit(30000)) {process.Kill(true); throw new IOException("本机照片处理超时，未登记。");}
                    if(process.ExitCode!=0)throw new IOException("照片无法解码或生成缩略图，未登记。");
                }
                if(!File.Exists(output) || new FileInfo(output).Length>16*1024*1024)throw new IOException("照片缩略图无效，未登记。");
                byte[] image=File.ReadAllBytes(output);
                if(image.Length<4 || image[0]!=0xff || image[1]!=0xd8 || image[image.Length-2]!=0xff || image[image.Length-1]!=0xd9)
                    throw new IOException("照片处理组件未返回 JPEG，未登记。");
                return image;
            }
            finally
            {
                if(File.Exists(input))File.Delete(input);
                if(File.Exists(output))File.Delete(output);
                if(Directory.Exists(temporary) && Directory.GetFileSystemEntries(temporary).Length==0)Directory.Delete(temporary);
            }
        }
    }
}
