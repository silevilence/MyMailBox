using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class ProtocolProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ProtocolProxy(bool socks) { _listener.Start(); _run = Run(socks); }
    private async Task Run(bool socks)
    {
        using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token); using var outgoing = new TcpClient();
        using var source = incoming.GetStream();
        if (socks)
        {
            var header = new byte[2]; await source.ReadExactlyAsync(header, _stop.Token);
            var methods = new byte[header[1]]; await source.ReadExactlyAsync(methods, _stop.Token); await source.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
            var request = new byte[4]; await source.ReadExactlyAsync(request, _stop.Token);
            if (request[3] == 3) { var length = new byte[1]; await source.ReadExactlyAsync(length, _stop.Token); await source.ReadExactlyAsync(new byte[length[0]], _stop.Token); }
            else await source.ReadExactlyAsync(new byte[request[3] == 1 ? 4 : 16], _stop.Token);
            var port = new byte[2]; await source.ReadExactlyAsync(port, _stop.Token);
            if ((port[0] << 8 | port[1]) != 3993) throw new Exception("Proxy test may connect only to GreenMail IMAPS");
            await outgoing.ConnectAsync(IPAddress.Loopback, 3993, _stop.Token);
            await source.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, _stop.Token);
        }
        else
        {
            var text = new StringBuilder(); var buffer = new byte[1];
            while (!text.ToString().EndsWith("\r\n\r\n")) { await source.ReadExactlyAsync(buffer, _stop.Token); text.Append((char)buffer[0]); }
            if (!text.ToString().StartsWith("CONNECT 127.0.0.1:3993 ")) throw new Exception("Unexpected proxy target");
            await outgoing.ConnectAsync(IPAddress.Loopback, 3993, _stop.Token);
            await source.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), _stop.Token);
        }
        using var target = outgoing.GetStream();
        var up = source.CopyToAsync(target, _stop.Token); var down = target.CopyToAsync(source, _stop.Token);
        await Task.WhenAny(up, down); _stop.Cancel();
        try { await Task.WhenAll(up, down); } catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } catch (IOException) { }
        _stop.Dispose();
    }
}
