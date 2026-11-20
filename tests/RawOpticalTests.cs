using System;
using System.IO;
using DiscArchive;

internal static class RawOpticalTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("FAIL: " + name);
    }
    public static int Main(string[] args)
    {
        try
        {
            byte[] source = new byte[2048 * 5];
            for (int i = 0; i < source.Length; i++) source[i] = (byte)(i % 251);
            int reads = 0, verifies = 0, closes = 0;
            bool changed = false;
            long bad = -1;
            AlignedOpticalStream stream = new AlignedOpticalStream(source.Length, 2048,
                delegate(long offset, byte[] target)
                {
                    Check(offset % 2048 == 0 && target.Length == 2048, "physical alignment");
                    reads++;
                    if (offset == bad) throw new IOException("simulated bad sector");
                    Buffer.BlockCopy(source, (int)offset, target, 0, target.Length);
                }, delegate { verifies++; if (changed) throw new IOException("media changed"); }, delegate { closes++; });
            byte[] buffer = new byte[4100];
            stream.Position = 2047;
            Check(stream.Read(buffer, 3, 2050) == 2050, "unaligned cross-sector read");
            for (int i = 0; i < 2050; i++)
                if (buffer[3 + i] != source[2047 + i]) throw new Exception("content mismatch");
            Check(reads == 3, "only intersected sectors read");
            stream.Position = 4097;
            Check(stream.Read(buffer, 0, 1) == 1 && reads == 3, "single-sector cache");
            stream.Seek(-3, SeekOrigin.End);
            Check(stream.Read(buffer, 0, 8) == 3 && buffer[2] == source[source.Length - 1], "short tail read");
            Check(stream.Read(buffer, 0, 1) == 0, "EOF");
            stream.Seek(10, SeekOrigin.End);
            Check(stream.Read(buffer, 0, 1) == 0, "seek past end yields EOF");
            Throws<IOException>(delegate { stream.Seek(-1, SeekOrigin.Begin); }, "negative seek rejected");
            Throws<IOException>(delegate { stream.Seek(Int64.MaxValue, SeekOrigin.End); }, "seek overflow rejected");
            Throws<ArgumentOutOfRangeException>(delegate { stream.Read(buffer, Int32.MaxValue, 1); }, "buffer bounds");
            Throws<NotSupportedException>(delegate { stream.Write(buffer, 0, 1); }, "writes rejected");
            bad = 6144;
            stream.Position = 0;
            Check(stream.Read(buffer, 0, 1) == 1, "unrelated bad sector does not affect good read");
            stream.Position = bad;
            Throws<IOException>(delegate { stream.Read(buffer, 0, 1); }, "bad sector reported");
            stream.Position = 0;
            Check(stream.Read(buffer, 0, 1) == 1, "good read recovers after bad sector");
            changed = true;
            Throws<IOException>(delegate { stream.Read(buffer, 0, 1); }, "cached sector still verifies continuity");
            Check(verifies > reads, "verification surrounds reads");
            stream.Dispose(); stream.Dispose();
            Check(closes == 1 && !stream.CanRead && !stream.CanSeek, "single release");
            Throws<ObjectDisposedException>(delegate { stream.Read(buffer, 0, 1); }, "disposed read rejected");
            byte[] bulkSource = new byte[1024 * 1024];
            for (int i = 0; i < bulkSource.Length; i++) bulkSource[i] = (byte)(i % 251);
            int singles = 0, batches = 0, boundaries = 0;
            bool bulkBad = false, bulkShort = false, bulkChanged = false, changeDuringRead = false;
            using (AlignedOpticalStream fast = new AlignedOpticalStream(bulkSource.Length, 2048,
                delegate(long at, byte[] target) { singles++; Buffer.BlockCopy(bulkSource, (int)at, target, 0, 2048); },
                delegate { boundaries++; if (bulkChanged) throw new IOException("media changed"); }, delegate { },
                delegate(long at, byte[] target, int targetOffset, int size)
                {
                    batches++;
                    Check(at % 2048 == 0 && size % 2048 == 0 && size > 0 && size <= 256 * 1024,
                        "bulk requests remain aligned and bounded");
                    if (bulkBad && at <= 4096 && at + size > 4096) throw new IOException("bulk bad sector");
                    if (bulkShort) throw new EndOfStreamException("short native read");
                    Buffer.BlockCopy(bulkSource, (int)at, target, targetOffset, size);
                    if (changeDuringRead) bulkChanged = true;
                }))
            {
                byte[] destination = new byte[600000];
                fast.Position = 1;
                int request = 512 * 1024 + 3000;
                Check(fast.Read(destination, 7, request) == request, "large unaligned read");
                for (int i = 0; i < request; i++) if (destination[7 + i] != bulkSource[1 + i]) throw new Exception("bulk content mismatch");
                Check(singles == 2 && batches == 2 && boundaries == 2, "two edge sectors two bulk requests two boundary checks");
                Check(destination[6] == 0 && destination[7 + request] == 0, "bulk output stays inside caller range");
                fast.Position = 1 + request - 1;
                int before = singles + batches;
                Check(fast.Read(destination, 0, 1) == 1 && singles + batches == before, "metadata cache performs no native read");
                Check(boundaries == 4, "metadata cached read still checks both boundaries");
                fast.Position = bulkSource.Length - 1;
                Check(fast.Read(destination, 0, 8) == 1 && destination[0] == bulkSource[bulkSource.Length - 1], "bulk-enabled tail does not overread");
                bulkBad = true;
                fast.Position = 0;
                Throws<IOException>(delegate { fast.Read(destination, 0, 8192); }, "bad sector inside requested bulk fails request");
                fast.Position = 0;
                Check(fast.Read(destination, 0, 1) == 1, "bad sector outside small retry is isolated");
                bulkBad = false; bulkShort = true; fast.Position = 2048;
                Throws<EndOfStreamException>(delegate { fast.Read(destination, 0, 2048); }, "short bulk read is never accepted");
                bulkShort = false; changeDuringRead = true; fast.Position = 2048;
                Throws<IOException>(delegate { fast.Read(destination, 0, 2048); }, "post-read verification rejects media transition");
                Console.WriteLine("BULK cadence: full reads verify twice; cached metadata reads verify twice with zero native reads.");
            }
            Check(!RawOptical.CanRead(null), "null is not readable media");
            Check(!RawOptical.CanRead(Path.GetPathRoot(Environment.SystemDirectory)), "fixed disk rejected");
            if (args.Length == 2 && args[0] == "--drive")
            {
                using (RawOptical medium = RawOptical.Open(args[1]))
                {
                    Check(medium.Data.Length > 0, "physical medium has length");
                    medium.Data.Position = 1023;
                    Check(medium.Data.Read(buffer, 0, 3) == 3, "physical unaligned three-byte read");
                    medium.Verify();
                    Console.WriteLine("READ-ONLY length: " + medium.Data.Length);
                }
                Console.WriteLine("Physical read handle and media lease disposed.");
            }
            Console.WriteLine("Checks passed: " + checks);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
