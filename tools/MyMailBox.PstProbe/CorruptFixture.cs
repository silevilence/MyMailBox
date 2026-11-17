using System.Security.Cryptography;
using XstReader;

// Creates one deliberately damaged copy of our small synthetic ANSI fixture.
// The existing source and any existing destination are never opened for writing.
static class CorruptFixture
{
    public static void Create(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        var bytes = File.ReadAllBytes(source);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != "BCFCEB0311C78D7C3E5A05E223C0C780D355A0CF1D663244B86FED1E6F6D92E5")
            throw new InvalidDataException("Only the existing synthetic ANSI fixture is accepted.");
        var file = new XstFile(source, 936);
        Message Find(Folder folder)
        {
            foreach (var message in file.ReadMessages(folder))
                if (message.Subject == "MMB-rtf") { file.ReadMessageDetails(message); return message; }
            foreach (var child in folder.Folders) { var message = Find(child); if (message != null) return message; }
            return null;
        }
        var compressed = Find(file.ReadFolderTree())?.RtfCompressed ?? throw new InvalidDataException("Fixture message missing.");
        var decode = Enumerable.Range(0, 256).Select(n => (byte)n).ToArray();
        Crypto.Decrypt(ref decode, EbCryptMethod.NDB_CRYPT_PERMUTE, 0, 0, decode.Length);
        var encode = new byte[256];
        for (var i = 0; i < 256; i++) encode[decode[i]] = (byte)i;
        var pattern = compressed.Select(b => encode[b]).ToArray();
        var offset = bytes.AsSpan().IndexOf(pattern);
        if (offset < 0 || bytes.AsSpan(offset + pattern.Length).IndexOf(pattern) >= 0)
            throw new InvalidDataException("Expected exactly one complete RTF payload in the synthetic fixture.");
        bytes[offset + 12] = encode[(byte)(compressed[12] ^ 1)]; // RTF checksum only
        if (File.Exists(destination))
        {
            if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes)) throw new IOException("Existing damaged fixture differs.");
        }
        else
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            output.Write(bytes);
        }
        Console.WriteLine($"Prepared/reused one-message RTF corruption at offset {offset + 12}; source retained.");
    }
}
