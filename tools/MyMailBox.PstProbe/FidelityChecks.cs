using System.Text;
using XstReader;

static class FidelityChecks
{
    public static void Run()
    {
        static void Equal(string expected, string actual)
        {
            if (expected != actual) throw new InvalidDataException($"Expected '{expected}', got '{actual}'.");
        }
        Equal("中文", LTP.DecodeString8([.. Encoding.GetEncoding(936).GetBytes("中文"), 0, 0xea, 0x34], 936, false));
        Equal("café", LTP.DecodeString8([0x63, 0x61, 0x66, 0xe9, 0], 1252, false));
        Equal("中文", LTP.DecodeString8([1, 1, .. Encoding.GetEncoding(936).GetBytes("中文"), 0], 936, true));
        Equal("", LTP.DecodeString8([], null, true));
        Equal("中文", LTP.DecodeString16([1, 0, 1, 0, .. Encoding.Unicode.GetBytes("中文")], true));
        Equal("", LTP.DecodeString16([], true));
        Equal("abc", LTP.DecodeString8([97, 98, 99], null, false));
        try { LTP.DecodeString8([0xd6, 0xd0], null, false); throw new Exception("Missing code page was silently guessed."); }
        catch (XstException) { }
        try { LTP.DecodeString8([0xff], 65001, false); throw new Exception("Invalid bytes were silently replaced."); }
        catch (DecoderFallbackException) { }
        Equal("text  ", PstBodyText.FromRtf(@"{\rtf1\ansi text  \par}"));
        Equal("text\r\n", PstBodyText.FromRtf(@"{\rtf1\ansi text\par\par}"));
        Equal("中文", PstBodyText.FromRtf(@"{\rtf1\ansi\ansicpg936 \'d6\'d0\'ce\'c4\par}"));
        Console.WriteLine("PASS PST String8: code pages, null boundary, prefix, empty, missing metadata and invalid bytes");
        Console.WriteLine("PASS PST RTF: Chinese text, trailing spaces and intentional trailing paragraphs");
    }
}
