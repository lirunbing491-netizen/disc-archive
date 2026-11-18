using System;
using System.IO;
using System.Security.Cryptography;

namespace DiscArchive
{
    internal static class ProjectPhoto
    {
        internal static string DirectoryFor(string target,string folder)
        {
            if(String.IsNullOrWhiteSpace(folder) || folder=="." || folder==".." || folder!=Path.GetFileName(folder)
                || folder.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || folder.EndsWith(".") || folder.EndsWith(" "))
                throw new IOException("项目目录名无效。");
            string path=Path.Combine(Path.GetFullPath(target),folder);
            CheckParents(path);
            return path;
        }
        private static void CheckParents(string path)
        {
            for(string p=path;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
            {
                try { if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new IOException("归档位置含目录链接："+p); }
                catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
            }
        }
        private static string Hash(string path)
        {
            using(var f=File.OpenRead(path)) using(var sha=SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(f));
        }
        internal static void Store(Entry e,string folder,Action verify)
        {
            verify();
            string photoDir=Path.Combine(DirectoryFor(e.TargetRoot,folder),"盘面照片");
            CheckParents(photoDir);
            Directory.CreateDirectory(photoDir);
            string source=e.OriginalPhoto;
            if(String.IsNullOrEmpty(source)) source=e.Photo;
            string dest=Path.Combine(photoDir,e.Id+Path.GetExtension(source));
            CheckParents(dest);
            string digest=Hash(source);
            if(File.Exists(dest))
            {
                if(Hash(dest)==digest) return;
                dest=Path.Combine(photoDir,e.Id+"-"+Guid.NewGuid().ToString("N")+Path.GetExtension(source));
            }
            string stage=Path.Combine(e.JobDir,"photo-"+Guid.NewGuid().ToString("N")+".tmp");
            File.Copy(source,stage,false);
            if(Hash(stage)!=digest) throw new IOException("项目盘面照片读回不一致。");
            verify(); CheckParents(dest); File.Move(stage,dest);
        }
    }
}
