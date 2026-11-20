using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DiscArchive;

// Synthetic ownership and fault injection only. No physical drive is opened.
internal static class MountCleanupTests
{
    private static int checks;
    private static readonly List<string> order=new List<string>();
    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception("FAIL: "+message);checks++;Console.WriteLine("PASS: "+message);}
    private static FieldInfo Field(object value,string name)
    {return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);}
    private static void Set(object value,string name,object data){Field(value,name).SetValue(value,data);}
    private static object Get(object value,string name){return Field(value,name).GetValue(value);}
    private static IOException Failure(Action action)
    {try{action();}catch(IOException ex){return ex;}throw new Exception("Expected cleanup IOException.");}
    private sealed class Resource : IDisposable
    {
        private readonly string stage;private readonly bool fail;
        internal Resource(string stage,bool fail){this.stage=stage;this.fail=fail;}
        public void Dispose(){order.Add(stage);if(fail)throw new COMException("PRIVATE_PATH_AND_LABEL",unchecked((int)0x80004005));}
    }
    private sealed class FailingCache : FileStream
    {
        internal int Closes;
        internal FailingCache(string path):base(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,4096,FileOptions.DeleteOnClose){}
        protected override void Dispose(bool disposing)
        {base.Dispose(disposing);if(disposing){Closes++;throw new IOException("PRIVATE_CACHE_PATH");}}
    }
    private sealed class NativeStream : SevenZipInterop.ISequentialIn
    {public int Read(IntPtr data,uint size,IntPtr processed){Marshal.WriteInt32(processed,0);return 0;}}
    private sealed class Archive : SevenZipInterop.IArchive
    {
        internal int Closes;internal int Result;internal bool Throw;
        public int Close(){Closes++;order.Add("archive");if(Throw)throw new COMException("PRIVATE_ARCHIVE_PATH",unchecked((int)0x8001010E));return Result;}
        public int Open(SevenZipInterop.IIn stream,ref ulong maxStart,IntPtr callback){return 0;}
        public int GetNumberOfItems(out uint count){count=0;return 0;}
        public int GetProperty(uint index,uint property,ref SevenZipInterop.PropVariant value){return 0;}
        public int Extract(uint[] indices,uint count,int test,SevenZipInterop.IExtract callback){return 0;}
        public int GetArchiveProperty(uint property,ref SevenZipInterop.PropVariant value){return 0;}
        public int GetNumberOfProperties(out uint count){count=0;return 0;}
        public int GetPropertyInfo(uint index,out IntPtr name,out uint property,out ushort type){name=IntPtr.Zero;property=0;type=0;return 0;}
        public int GetNumberOfArchiveProperties(out uint count){count=0;return 0;}
        public int GetArchivePropertyInfo(uint index,out IntPtr name,out uint property,out ushort type){name=IntPtr.Zero;property=0;type=0;return 0;}
    }
    private static SevenZipReader Reader(Archive archive)
    {
        var reader=(SevenZipReader)Activator.CreateInstance(typeof(SevenZipReader),true);
        Set(reader,"archive",archive);Set(reader,"input",new SevenZipInterop.Input(new MemoryStream(new byte[16])));
        return reader;
    }
    private static Stream Item(SevenZipReader reader)
    {
        Type type=typeof(SevenZipReader).GetNestedType("ItemStream",BindingFlags.NonPublic);
        var stream=(Stream)Activator.CreateInstance(type,BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{reader,new NativeStream(),8L},null);
        ((IList)Get(reader,"streams")).Add(stream);return stream;
    }
    public static int Main()
    {
        string work=Path.Combine(Path.GetTempPath(),"DiscArchive-Cleanup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        var mount=(OpticalMount)Activator.CreateInstance(typeof(OpticalMount),BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{Path.Combine(work,"virtual")},null);
        var resources=(List<IDisposable>)Get(mount,"resources");
        resources.Add(new Resource("lease",true));resources.Add(new Resource("slice",false));resources.Add(new Resource("reader",true));
        IOException failure=Failure(mount.Dispose);
        Check(String.Join(",",order.ToArray())=="reader,slice,lease","mount closes in reverse ownership order despite failures");
        Check(failure.Message.Contains("HRESULT 0x80004005")&&!failure.ToString().Contains("PRIVATE_"),"cleanup diagnosis has HRESULT and excludes native private text");
        Check(((AggregateException)failure.InnerException).InnerExceptions.Count==2,"all close failures are retained");
        mount.Dispose();Check(order.Count==3,"mount repeated close does not release twice");

        var primary=new IOException("original read failure");Exception observed=null;
        try{OpticalMount.Open(Path.Combine(work,"virtual"),new MemoryStream(),delegate{throw primary;},new Resource("rollback",true),OpticalView.Auto);}catch(Exception ex){observed=ex;}
        Check(Object.ReferenceEquals(primary,observed),"open rollback does not mask original read error");
        Check(((string)primary.Data["DiscArchive.Cleanup"]).Contains("mount.Resource"),"rollback diagnostics are attached to original exception");
        ResourceCleanup.Preserve(primary,delegate{throw new COMException("PRIVATE_",unchecked((int)0x8001010E));});
        Check(((string)primary.Data["DiscArchive.Cleanup"]).Contains("mount.Resource")&&((string)primary.Data["DiscArchive.Cleanup"]).Contains("8001010E"),"nested rollback diagnostics accumulate");

        var archive=new Archive{Result=unchecked((int)0x80004005)};var reader=Reader(archive);
        Stream first=Item(reader),second=Item(reader);var cache=new FailingCache(Path.Combine(work,"cache.tmp"));Set(first,"cache",cache);
        string dll=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native",IntPtr.Size==8?"x64":"x86","7z.dll");
        var library=new SevenZipLibrary(dll);Set(reader,"library",library);
        failure=Failure(reader.Dispose);
        Check(cache.Closes==1&&!first.CanRead&&!second.CanRead,"one cache close failure does not leave other item streams alive");
        Check(Get(first,"native")==null&&Get(first,"cache")==null&&Get(first,"owner")==null,"failed item close detaches native aliases, cache and owner");
        Check(archive.Closes==1&&Get(reader,"archive")==null,"archive close still runs once after failed item close");
        Check((IntPtr)Get(library,"module")==IntPtr.Zero&&Get(reader,"input")==null,"library and input are released after item and archive close failures");
        Check(failure.Message.Contains("7zip.item.cache.close")&&failure.Message.Contains("7zip.archive.close")&&failure.Message.Contains("80004005"),"both failing stages and native Close HRESULT are reported");
        Check(!failure.ToString().Contains("PRIVATE_"),"reader aggregated diagnostics exclude fixture private paths");
        reader.Dispose();first.Dispose();second.Dispose();Check(cache.Closes==1&&archive.Closes==1,"failed close is idempotent throughout reader and items");

        archive=new Archive{Throw=true};reader=Reader(archive);failure=Failure(reader.Dispose);
        Check(failure.Message.Contains("8001010E")&&Get(reader,"archive")==null&&Get(reader,"input")==null,"thrown COM close error preserves HRESULT while completing cleanup");

        reader=Reader(new Archive());library=new SevenZipLibrary(dll);Set(reader,"library",library);Set(reader,"nativeReleaseFailed",true);
        failure=Failure(reader.Dispose);
        Check((IntPtr)Get(library,"module")!=IntPtr.Zero&&failure.Message.Contains("7zip.library.retained.after-release-failure"),"module stays loaded with explicit diagnostic when native ownership is unresolved");
        library.Dispose(); // This fixture has no real native object; safely release its test-only module reference.

        int unlock=0;var raw=new AlignedOpticalStream(2048,2048,delegate(long p,byte[] b){},delegate{},delegate {
            var cleanup=new ResourceCleanup();cleanup.Run("raw.reader.close",delegate{throw new IOException("PRIVATE_RAW_PATH");});cleanup.Run("raw.lease.unlock",delegate{unlock++;});cleanup.ThrowIfAny();
        });
        failure=Failure(raw.Dispose);raw.Dispose();Check(unlock==1&&!raw.CanRead&&failure.Message.Contains("raw.reader.close"),"raw ownership callback still unlocks and cannot close twice after error");
        Console.WriteLine("PASS "+checks+" cleanup checks. Generated fixtures: "+work);return 0;
    }
}
