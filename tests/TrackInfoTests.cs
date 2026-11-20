using System;
using DiscArchive;

internal static class TrackInfoTests
{
    private static int checks;
    private static void Check(bool condition, string text)
    { if (!condition) throw new Exception("FAIL " + text); checks++; Console.WriteLine("PASS " + text); }
    private static byte[] Response(int bytes, int first, int last)
    { byte[] b = new byte[bytes]; b[0] = (byte)((bytes - 2) >> 8); b[1] = (byte)(bytes - 2); b[2] = (byte)first; b[3] = (byte)last; return b; }
    private static void Track(byte[] b, int p, int number, bool data, int minute)
    { b[p + 1] = data ? (byte)0x14 : (byte)0x10; b[p + 2] = (byte)number; b[p + 5] = (byte)minute; }
    private static byte[] Session(int first, int last)
    { byte[] b = Response(12, first, last); Track(b, 4, 1, true, 0); return b; }
    private static byte[] Toc(bool mixed, bool allData)
    { byte[] b = Response(28, 1, 2); Track(b, 4, 1, allData, 0); Track(b, 12, 2, mixed || allData, 2); Track(b, 20, 0xAA, false, 4); return b; }
    private static void Unknown(byte[] session, byte[] toc, string text)
    { TrackInfo t = OpticalTracks.Parse(session, toc); Check(!t.HasUncoveredTracks && t.Note == "", text); }
    public static int Main()
    {
        try
        {
            Unknown(null, null, "missing driver responses remain unknown");
            Unknown(Session(1, 1), Toc(false, true), "one reported session and data TOC do not assert complete coverage");
            TrackInfo t = OpticalTracks.Parse(Session(1, 2), null);
            Check(t.HasUncoveredTracks && t.Note.Contains("历史区段尚未提取"), "two completed sessions warn about historical data");
            t = OpticalTracks.Parse(null, Toc(true, false));
            Check(t.HasUncoveredTracks && t.Note.Contains("混合音轨"), "mixed audio data tracks warn without reading payload");
            t = OpticalTracks.Parse(null, Toc(false, false));
            Check(t.HasUncoveredTracks && t.Note.Contains("均为音频"), "audio-only TOC warns tracks were not extracted");
            t = OpticalTracks.Parse(Session(1, 3), Toc(true, false));
            Check(t.Note.Contains("历史区段") && t.Note.Contains("混合音轨"), "independent verified coverage gaps both retained");
            Unknown(Session(3, 2), null, "reversed completed-session interval rejected");
            Unknown(Session(0, 2), null, "zero first completed session rejected");
            Unknown(Session(1, 100), null, "out of range session count rejected");
            byte[] b = Session(1, 2); b[1]--; Unknown(b, null, "truncated declared session length rejected");
            b = Toc(true, false); b[1] = 255; Unknown(null, b, "TOC oversized declared response rejected");
            b = Toc(true, false); b[14] = 1; Unknown(null, b, "duplicate track numbering rejected");
            b = Toc(true, false); b[22] = 3; Unknown(null, b, "missing leadout rejected");
            b = Toc(true, false); b[17] = 0; Unknown(null, b, "non-increasing track addresses rejected");
            b = Toc(true, false); b[10] = 60; Unknown(null, b, "invalid MSF second rejected");
            b = Toc(true, false); b[11] = 75; Unknown(null, b, "invalid MSF frame rejected");
            b = Toc(true, false); b[5] = 0; Unknown(null, b, "unknown ADR is not assumed audio");
            Unknown(null, new byte[10000], "oversized TOC bounded without parsing");
            Unknown(new byte[1], new byte[1], "short metadata cannot index beyond response");
            t = OpticalTracks.Parse(Session(1, 2), new byte[1]);
            Check(t.HasUncoveredTracks && t.Note.Contains("历史区段"), "damaged TOC does not erase reliable session warning");
            Console.WriteLine("PASS " + checks + " synthetic checks; no device opened."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
