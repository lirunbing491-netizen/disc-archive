using System;
using System.IO;
using System.Runtime.InteropServices;
using DiscArchive;

// Read-only host checks. No media load/eject/write is issued. Acquire is tested
// only for non-CDROM paths and the known empty optical drive supplied as arg.
internal static class OpticalTests
{
    [DllImport("kernel32.dll")] private static extern uint GetThreadErrorMode();
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Rejected(string root)
    {
        try { using (Optical.Acquire(root)) { } }
        catch (ArgumentException) { checks++; return; }
        catch (IOException) { checks++; return; }
        throw new Exception("Unexpected lease: " + root);
    }
    public static int Main(string[] args)
    {
        try
        {
            uint mode = GetThreadErrorMode();
            Check(!Optical.IsReady(null), "null not ready");
            Check(!Optical.IsEmpty(null), "null not empty");
            foreach (string invalid in new string[] { "", "C:\\folder", "\\\\server\\share", "H:\\..\\", "1:\\", "H:\\x" })
            {
                Check(!Optical.IsReady(invalid), "invalid root rejected: " + invalid);
                Check(!Optical.IsEmpty(invalid), "invalid root not empty: " + invalid);
                Rejected(invalid);
            }
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            Check(!Optical.IsReady(systemRoot), "fixed disk not accepted");
            Check(!Optical.IsEmpty(systemRoot), "fixed disk not empty CDROM");
            Check(OpticalNative.EjectionControl == 0x002D0940, "EJECTION_CONTROL constant");
            Check(OpticalNative.CheckVerify2 == 0x002D0800, "CHECK_VERIFY2 constant");
            Check(OpticalNative.ExplicitNoMedia(false, 1112), "explicit no media accepted");
            Check(!OpticalNative.ExplicitNoMedia(true, 1112), "successful media request not empty");
            foreach (int error in new int[] { 0, 1, 5, 21, 23, 31, 50, 87, 1110, 1117, 1167, 1785 })
                Check(!OpticalNative.ExplicitNoMedia(false, error), "ambiguous/device/media error not empty: " + error);
            byte[] media = new byte[] { 0, 6, 4, 0xDE, 0, 1, 0, 0 };
            Check(OpticalNative.MediaStatusNoMedia(media), "current MMC open empty tray");
            media[5] = 0;
            Check(OpticalNative.MediaStatusNoMedia(media), "current MMC closed empty tray");
            media[5] = 2;
            Check(!OpticalNative.MediaStatusNoMedia(media), "present media (including audio/bad/spinning) not empty");
            media[5] = 0; media[2] = 0x84;
            Check(!OpticalNative.MediaStatusNoMedia(media), "no event available not empty proof");
            media[2] = 3;
            Check(!OpticalNative.MediaStatusNoMedia(media), "wrong notification class rejected");
            media[2] = 4; media[4] = 2;
            Check(!OpticalNative.MediaStatusNoMedia(media), "contradictory new-media event rejected");
            media[4] = 0; media[3] = 0;
            Check(!OpticalNative.MediaStatusNoMedia(media), "unsupported media class rejected");
            Check(!OpticalNative.MediaStatusNoMedia(new byte[7]), "truncated media status rejected");
            Check(!OpticalNative.MediaStatusNoMedia(null), "missing media status rejected");
            Rejected(systemRoot);
            // Optional enumeration is read-only: no Acquire, tray lock, media load or eject.
            if (Array.IndexOf(args, "--enumerate-read-only") >= 0)
            {
                string[] drives = Optical.Drives();
                Console.WriteLine("READ-ONLY CDROM roots: " + String.Join(", ", drives));
                foreach (string drive in drives)
                    Check(drive.Length == 3 && drive[1] == ':' && drive[2] == '\\', "normalized root: " + drive);
            }
            Check(mode == GetThreadErrorMode(), "thread error mode restored");
            Console.WriteLine("Checks passed: " + checks);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
