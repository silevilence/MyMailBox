using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// <summary>Real loopback TLS conversations for failures GreenMail does not expose as test switches.</summary>
internal sealed class ProtocolFaultServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _runner;
    private readonly bool _smtp;
    private readonly bool _imap;
    public bool SpecialFolders { get; set; }
    public bool AdvertiseId { get; set; } = true;
    public string? Identification { get; private set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Pin => _certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public bool Uids { get; set; } = true;
    public bool DropSecondRetrieval { get; set; }
    public bool DropAfterData { get; set; }
    public List<string> RcptCommands { get; } = [];
    public int Submissions { get; private set; }
    public bool AcceptAll { get; set; }
    public bool RejectSender { get; set; }
    public int Deletes { get; private set; }
    public string? Received { get; private set; }
    private readonly List<(string Uid, byte[] Raw)> _mail = [];
    public ProtocolFaultServer(bool smtp = false,bool imap = false)
    {
        _smtp = smtp;_imap = imap;
        using var key = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddDays(1));
        _certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
        for (var i = 0; i < 2; i++) _mail.Add(("uid-" + i, Encoding.UTF8.GetBytes($"From: alice@localhost\r\nTo: bob@localhost\r\nSubject: fault-{i}\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n原文 {i}\r\n")));
        _listener.Start(); _runner = Run();
    }
    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var tls = new SslStream(tcp.GetStream()); await tls.AuthenticateAsServerAsync(_certificate);
                using var reader = new StreamReader(tls, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(tls, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\r\n" };
                if (_imap) await Imap(reader,writer); else if (_smtp) await Smtp(reader, writer); else await Pop(reader, writer);
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }
    }
    private async Task Imap(StreamReader reader, StreamWriter writer)
    {
        string capabilities = "IMAP4rev1" + (AdvertiseId ? " ID" : "") + (SpecialFolders ? " NAMESPACE SPECIAL-USE" : "");
        await writer.WriteLineAsync("* OK [CAPABILITY "+capabilities+"] loopback IMAP");
        while (await reader.ReadLineAsync(_stop.Token) is string line)
        {
            var parts = line.Split(' ',3);if (parts.Length < 2) return;
            string tag=parts[0];
            switch(parts[1].ToUpperInvariant())
            {
                case "CAPABILITY": await writer.WriteLineAsync("* CAPABILITY "+capabilities);break;
                case "ID": Identification=line;await writer.WriteLineAsync("* ID NIL");break;
                case "LOGIN" when SpecialFolders: break;
                case "NAMESPACE" when SpecialFolders: await writer.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL");break;
                case "LIST" when SpecialFolders:
                    if (line.EndsWith("\"\"", StringComparison.Ordinal)) await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
                    else
                    {
                        await writer.WriteLineAsync("* LIST () \"/\" INBOX");
                        if (!line.EndsWith("INBOX", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var (name, flag) in new[] { ("已发送", "Sent"), ("草稿箱", "Drafts"), ("垃圾箱", "Trash") })
                            {
                                var encoded = Convert.ToBase64String(Encoding.BigEndianUnicode.GetBytes(name)).TrimEnd('=').Replace('/', ',');
                                await writer.WriteLineAsync($"* LIST (\\{flag}) \"/\" \"&{encoded}-\"");
                            }
                        }
                    }
                    break;
                case "LOGIN": await writer.WriteLineAsync(tag+" NO Authentication rejected");return;
                case "LOGOUT": await writer.WriteLineAsync("* BYE");return;
                default: await writer.WriteLineAsync(tag+" BAD Unsupported command");continue;
            }
            await writer.WriteLineAsync(tag+" OK completed");
        }
    }
    private async Task Smtp(StreamReader reader, StreamWriter writer)
    {
        await writer.WriteLineAsync("220 localhost test");
        while (await reader.ReadLineAsync(_stop.Token) is { } command)
        {
            if (command.StartsWith("EHLO")) await writer.WriteLineAsync("250-localhost\r\n250-AUTH PLAIN\r\n250 SIZE 26214400");
            else if (command.StartsWith("AUTH")) await writer.WriteLineAsync("235 authenticated");
            else if (command.StartsWith("MAIL")) await writer.WriteLineAsync(RejectSender ? "550 sender refused" : "250 sender");
            else if (command.StartsWith("RCPT"))
            {
                RcptCommands.Add(command);
                await writer.WriteLineAsync(!AcceptAll && command.Contains("reject@") ? "550 no recipient" : "250 recipient");
            }
            else if (command == "DATA")
            {
                await writer.WriteLineAsync("354 continue"); var data = new StringBuilder();
                while (await reader.ReadLineAsync(_stop.Token) is { } line && line != ".") data.AppendLine(line);
                Received = data.ToString(); Submissions++; if (DropAfterData) return; await writer.WriteLineAsync("250 accepted");
            }
            else if (command == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
            else await writer.WriteLineAsync("250 okay");
        }
    }
    private async Task Pop(StreamReader reader, StreamWriter writer)
    {
        await writer.WriteLineAsync("+OK local test"); var pending = new HashSet<int>();
        while (await reader.ReadLineAsync(_stop.Token) is { } command)
        {
            var parts = command.Split(' '); var index = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n - 1 : -1;
            switch (parts[0])
            {
                case "CAPA": await writer.WriteLineAsync("+OK\r\nUSER\r\n" + (Uids ? "UIDL\r\n" : "") + "."); break;
                case "USER": case "PASS": await writer.WriteLineAsync("+OK"); break;
                case "STAT": await writer.WriteLineAsync($"+OK {_mail.Count} {_mail.Sum(m => m.Raw.Length)}"); break;
                case "UIDL":
                    if (!Uids) await writer.WriteLineAsync("-ERR unsupported");
                    else if (index >= 0) await writer.WriteLineAsync($"+OK {index + 1} {_mail[index].Uid}");
                    else { await writer.WriteLineAsync("+OK"); for (var i = 0; i < _mail.Count; i++) await writer.WriteLineAsync($"{i + 1} {_mail[i].Uid}"); await writer.WriteLineAsync("."); }
                    break;
                case "LIST":
                    if (index >= 0) await writer.WriteLineAsync($"+OK {index + 1} {_mail[index].Raw.Length}");
                    else { await writer.WriteLineAsync("+OK"); for (var i = 0; i < _mail.Count; i++) await writer.WriteLineAsync($"{i + 1} {_mail[i].Raw.Length}"); await writer.WriteLineAsync("."); }
                    break;
                case "RETR":
                    if (DropSecondRetrieval && index == 1) return;
                    await writer.WriteLineAsync("+OK"); await writer.WriteAsync(Encoding.UTF8.GetString(_mail[index].Raw)); await writer.WriteLineAsync("."); break;
                case "DELE": pending.Add(index); await writer.WriteLineAsync("+OK"); break;
                case "RSET": pending.Clear(); await writer.WriteLineAsync("+OK"); break;
                case "QUIT": foreach (var i in pending.OrderDescending()) { _mail.RemoveAt(i); Deletes++; } await writer.WriteLineAsync("+OK"); return;
                default: await writer.WriteLineAsync("-ERR unsupported"); break;
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _runner.WaitAsync(TimeSpan.FromSeconds(5)); _certificate.Dispose(); _stop.Dispose();
    }
}
