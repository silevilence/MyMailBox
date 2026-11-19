using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Chaos.NaCl;
using MyMailBox.Contracts;
using MyMailBox.Updates;

internal static class UpdateLoopbackChecks
{
    // Opt in once with an existing NSIS installer. Subsequent checks reopen the verified local download.
    public static async Task Run(string installer)
    {
        var bytes = await File.ReadAllBytesAsync(installer);
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("需要已有 Windows 安装包。");
        var filename = Path.GetFileName(installer);
        var version = filename["MyMailBox-V".Length..^"-win-x64-setup.exe".Length];
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/");
        Uri Url(string name) => new(address, name);
        var seed = RandomNumberGenerator.GetBytes(32);
        Ed25519.KeyPairFromSeed(out var publicKey, out var privateKey, seed);
        CryptographicOperations.ZeroMemory(seed);
        var cast = SignedAppCast.Create(version, Url(filename), bytes.Length, Convert.ToBase64String(Ed25519.Sign(bytes, privateKey)), "回环验收");
        var signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(Ed25519.Sign(cast, privateKey)));
        CryptographicOperations.ZeroMemory(privateKey);
        var payloads = new Dictionary<string, byte[]> { [filename] = bytes, ["appcast.xml"] = cast, ["appcast.xml.signature"] = signature };
        var downloads = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var serving = Serve();
        var cache = Path.Combine(Environment.GetEnvironmentVariable("MYMAILBOX_HOME")!, "update-loopback");
        var key = Convert.ToBase64String(publicKey);
        var release = new UpdateRelease(version, "fixture", address, new(filename,Url(filename),bytes.Length),
            new("appcast.xml",Url("appcast.xml"),cast.Length), new("appcast.xml.signature",Url("appcast.xml.signature"),signature.Length));
        try
        {
            using var packages = new UpdatePackages(cache,key,"0.0.0",_=>throw new Exception("Unexpected install"));
            var result = await packages.Download(release,cancellation:stop.Token);
            MailChecks.Check(result.State==UpdateInstallState.Ready && downloads==1, "Valid installer did not arrive exactly once: "+result.Message);
        }
        finally { stop.Cancel(); await serving; }
        PreparedUpdate? prepared = null;
        using var reopened = new UpdatePackages(cache,key,"0.0.0",p=>prepared=p);
        await reopened.RequestInstall();
        MailChecks.Check(prepared != null && (await File.ReadAllBytesAsync(prepared.File)).SequenceEqual(bytes), "Offline reopen did not reuse verified bytes");
        Console.WriteLine($"PASS HTTP update: signed manifest, one installer GET ({bytes.Length} bytes), server stopped, received file reopened and verified for handoff");

        async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await server.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
                    var parts = (await reader.ReadLineAsync(stop.Token))?.Split(' ');
                    if (parts == null) continue;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    var path = new Uri(address,parts[1]).AbsolutePath.TrimStart('/');
                    bool found = payloads.TryGetValue(path,out var body);
                    body ??= [];
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),stop.Token);
                    if (parts[0] == "GET")
                    {
                        if (path==filename) Interlocked.Increment(ref downloads);
                        await stream.WriteAsync(body,stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
