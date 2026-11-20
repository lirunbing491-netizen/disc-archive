using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Microsoft.Win32.SafeHandles;
using DiscArchive;

internal static class OpticalEjectTests
{
    private static int checks;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    private sealed class Device : IOpticalEjectDevice
    {
        internal List<string> Events=new List<string>();
        internal int Verifies,Ejects,Closes;
        internal bool Changed,Disconnected,Refused,ChangeAfterRelease;
        public void Verify(uint baseline)
        {
            Events.Add("verify");Verifies++;
            if(Changed || Disconnected || (ChangeAfterRelease && Verifies>1))
                throw new IOException(Disconnected?"设备断开":"检测到换盘");
            if(baseline!=12)throw new Exception("wrong baseline");
        }
        public bool Eject(out int error){Events.Add("eject");Ejects++;error=Refused?5:0;return !Refused;}
        public void Dispose(){Events.Add("close");Closes++;}
    }
    private static OpticalEjectTicket Ticket(Device d,Action verify,Action release)
    {return new OpticalEjectTicket(d,12,verify,release);}
    private static void Set(object target,string name,object value)
    {target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(target,value);}
    private static void LeaseOwnership()
    {
        // Fake handles are deliberately invalid; locked=false prevents any
        // native control. Exercise the real lease's ownership transitions.
        foreach(bool retained in new[]{false,true})
        {
            var lease=(OpticalLease)FormatterServices.GetUninitializedObject(typeof(OpticalLease));
            var handle=new SafeFileHandle(new IntPtr(-1),false);
            var counter=new SafeFileHandle(new IntPtr(-1),false);
            Set(lease,"sync",new object());Set(lease,"handle",handle);Set(lease,"counterHandle",counter);Set(lease,"ejectRetained",retained);
            lease.Dispose();lease.Dispose();
            Check(handle.IsClosed==!retained && counter.IsClosed==!retained,"ordinary disposal vs retained ticket ownership "+retained);
            bool rejected=false;try{lease.Verify();}catch(ObjectDisposedException){rejected=true;}
            Check(rejected,"closed read lifecycle never permits further Verify "+retained);
            if(retained)
            {
                typeof(OpticalLease).GetMethod("ReleaseEjectRetention",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(lease,null);
                Check(handle.IsClosed && counter.IsClosed,"ticket releases both original handles after source closes");
            }
        }
        var live=(OpticalLease)FormatterServices.GetUninitializedObject(typeof(OpticalLease));
        var active=new SafeFileHandle(new IntPtr(-1),false);
        Set(live,"sync",new object());Set(live,"handle",active);Set(live,"ejectRetained",true);
        typeof(OpticalLease).GetMethod("ReleaseEjectRetention",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(live,null);
        Check(!active.IsClosed,"cancelling ticket preserves active reading handles");
        live.Dispose();Check(active.IsClosed,"active lease still closes normally after ticket cancellation");
    }
    public static int Main()
    {
        try
        {
            LeaseOwnership();
            string reason;int released=0;
            Device d=new Device();
            var ticket=Ticket(d,delegate{d.Events.Add("source");},delegate{released++;d.Events.Add("release");});
            Check(ticket.TryEject(out reason),"successful bound device accepts eject");
            Check(String.Join(",",d.Events.ToArray())=="source,verify,release,verify,eject","original lock released before final identity/count check and eject");
            Check(!ticket.TryEject(out reason) && d.Ejects==1,"only one physical command");
            ticket.Dispose();ticket.Dispose();Check(released==1 && d.Closes==1,"release and close exactly once");
            foreach(string mode in new[]{"changed","disconnected","after-release","source-failed","unlock-failed","refused"})
            {
                d=new Device{Changed=mode=="changed",Disconnected=mode=="disconnected",ChangeAfterRelease=mode=="after-release",Refused=mode=="refused"};
                int closeSource=0;
                var current=d;
                ticket=Ticket(d,delegate{if(mode=="source-failed")throw new IOException("原盘保护失效");},delegate{closeSource++;if(mode=="unlock-failed")throw new IOException("无法解锁");});
                Check(!ticket.TryEject(out reason) && !String.IsNullOrWhiteSpace(reason),mode+" preserves disc and gives reason");
                Check(d.Ejects==(mode=="refused"?1:0),mode+" never sends wrong eject");
                Check(!ticket.TryEject(out reason),mode+" never retries");
                try{ticket.Dispose();}catch(IOException){Check(mode=="unlock-failed","expected cleanup failure only");}
                Check(current.Closes==1 && closeSource==1,mode+" closes all retained resources");
            }
            d=new Device();released=0;ticket=Ticket(d,delegate{},delegate{released++;});ticket.Dispose();
            Check(!ticket.TryEject(out reason) && d.Ejects==0 && released==1,"cancelled ticket only releases never ejects");
            ticket=OpticalEjectTicket.Unavailable("计数不可用");Check(!ticket.TryEject(out reason) && reason=="计数不可用","unsupported counter gives concrete retention reason");ticket.Dispose();
            Check(OpticalEjectDevice.CdromEjectMedia==((2u<<16)|(1u<<14)|(0x202u<<2)),"CDROM FILE_READ_ACCESS CTL_CODE");
            // Execute the actual P/Invoke with an invalid handle only. This
            // validates native linkage/layout without targeting any device.
            MethodInfo native=typeof(OpticalEjectDevice).GetMethod("DeviceIoControl",BindingFlags.NonPublic|BindingFlags.Static);
            using(var invalid=new SafeFileHandle(new IntPtr(-1),false))
            {
                object[] arguments={invalid,OpticalEjectDevice.CdromEjectMedia,null,0u,null,0u,0u,IntPtr.Zero};
                Check(!(bool)native.Invoke(null,arguments),"actual native eject declaration rejects invalid handle");
                Check(Marshal.GetLastWin32Error()==6,"actual native declaration reports ERROR_INVALID_HANDLE");
            }
            Console.WriteLine("PASS "+checks+" safe-eject checks; no physical device command issued.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
