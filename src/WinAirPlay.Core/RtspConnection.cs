using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace WinAirPlay.Core;

public sealed record ProtocolMessage(string FirstLine, Dictionary<string, string> Headers, byte[] Body);

public sealed class RtspConnection : IDisposable
{
    private readonly TcpClient client = new(AddressFamily.InterNetwork) { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
    private readonly object gate = new();
    private Stream stream = Stream.Null;
    private int sequence;
    public string Session { get; private set; } = "";
    public string LocalAddress => ((System.Net.IPEndPoint)client.Client.LocalEndPoint!).Address.ToString();

    public void Connect(string host, int port, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        stream = client.GetStream();
    }

    public void Encrypt(byte[] secret) => stream = new HapStream(stream,
        SrpClient.Derive(secret, "Control-Salt", "Control-Write-Encryption-Key"),
        SrpClient.Derive(secret, "Control-Salt", "Control-Read-Encryption-Key"));

    public ProtocolMessage Request(string method, string path, byte[]? body = null,
        string contentType = "application/x-apple-binary-plist", Dictionary<string, string>? headers = null)
    {
        lock (gate)
        {
            body ??= [];
            var text = new StringBuilder($"{method} {path} RTSP/1.0\r\nCSeq: {++sequence}\r\nUser-Agent: AirPlay/550.10\r\n");
            if (Session.Length > 0) text.Append($"Session: {Session}\r\n");
            text.Append($"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\n");
            if (headers is not null)
                foreach (var (name, value) in headers) text.Append($"{name}: {value}\r\n");
            text.Append("\r\n");
            var prefix = Encoding.ASCII.GetBytes(text.ToString());
            stream.Write(prefix, 0, prefix.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            var response = ReadMessage(stream);
            var status = response.FirstLine.Split(' ');
            if (status.Length < 2 || !int.TryParse(status[1], out var code) || code is < 200 or >= 300)
                throw new IOException($"{method} {path}: {response.FirstLine}");
            if (response.Headers.TryGetValue("Session", out var session)) Session = session.Split(';')[0];
            return response;
        }
    }

    public static ProtocolMessage ReadMessage(Stream source)
    {
        var header = new List<byte>();
        while (header.Count < 32768)
        {
            var value = source.ReadByte();
            if (value < 0) throw new EndOfStreamException("AirPlay closed the control channel");
            header.Add((byte)value);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        if (header.Count >= 32768) throw new InvalidDataException("RTSP header too large");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var length = fields.TryGetValue("Content-Length", out var size) ? int.Parse(size, CultureInfo.InvariantCulture) : 0;
        if (length is < 0 or > 1048576) throw new InvalidDataException("RTSP body too large");
        var body = new byte[length];
        source.ReadExactly(body);
        return new(lines[0], fields, body);
    }

    public void Teardown(string path)
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            client.ReceiveTimeout = 500;
            client.SendTimeout = 500;
            Request("TEARDOWN", path);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        finally { Monitor.Exit(gate); }
    }

    public void Dispose() { client.Dispose(); stream.Dispose(); }
}
