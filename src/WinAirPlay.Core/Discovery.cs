using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WinAirPlay.Core;

public enum SpeakerKind { Unknown, HomePod, HomePodMini }

public sealed record Receiver(string Id, string Name, string Host, int Port)
{
    public string Model { get; init; } = "";
    public SpeakerKind Kind => Model switch
    {
        "AudioAccessory5,1" or "AudioAccessorySingle5,1" => SpeakerKind.HomePodMini,
        "AudioAccessory1,1" or "AudioAccessory1,2" or "AudioAccessory6,1" => SpeakerKind.HomePod,
        _ => SpeakerKind.Unknown
    };
    public override string ToString() => $"{Name} — {Host}" + (Model.Length == 0 ? "" : $" ({Model})");
}

public sealed record DnsRecord(string Name, ushort Type, uint Ttl, string? Target, int Port, string? Address)
{
    public string Model { get; init; } = "";
}

public static class DnsPacket
{
    public static byte[] Query(string service = "_raop._tcp.local", ushort type = 12)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in service.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }
        output.Write(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
        return output.ToArray();
    }

    private static string Name(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        for (var hops = 0; hops < 128; hops++)
        {
            if (cursor >= packet.Length) throw new InvalidDataException("Truncated DNS name");
            var size = packet[cursor++];
            if (size == 0)
            {
                if (!jumped) offset = cursor;
                return string.Join('.', labels);
            }
            if ((size & 0xc0) == 0xc0)
            {
                if (cursor >= packet.Length) throw new InvalidDataException("Truncated DNS pointer");
                if (!jumped) offset = cursor + 1;
                cursor = ((size & 63) << 8) | packet[cursor];
                jumped = true;
                continue;
            }
            if (size > 63 || cursor > packet.Length - size) throw new InvalidDataException("Invalid DNS label");
            labels.Add(Encoding.UTF8.GetString(packet, cursor, size));
            cursor += size;
        }
        throw new InvalidDataException("DNS compression cycle");
    }

    public static IReadOnlyList<DnsRecord> Parse(byte[] packet)
    {
        if (packet.Length < 12) throw new InvalidDataException("Truncated DNS packet");
        int Word(int at) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at, 2));
        var offset = 12;
        for (var i = 0; i < Word(4); i++) { Name(packet, ref offset); offset += 4; }
        var records = new List<DnsRecord>();
        var count = Word(6) + Word(8) + Word(10);
        for (var i = 0; i < count; i++)
        {
            var name = Name(packet, ref offset);
            if (offset > packet.Length - 10) throw new InvalidDataException("Truncated DNS record");
            var type = (ushort)Word(offset);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset + 4));
            var length = Word(offset + 8);
            offset += 10;
            if (offset > packet.Length - length) throw new InvalidDataException("Truncated DNS data");
            var next = offset + length;
            if (type == 1 && length == 4)
                records.Add(new(name, type, ttl, null, 0, new IPAddress(packet.AsSpan(offset, 4)).ToString()));
            if (type == 33 && length >= 7)
            {
                var port = Word(offset + 4);
                offset += 6;
                records.Add(new(name, type, ttl, Name(packet, ref offset), port, null));
            }
            if (type == 16)
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (offset < next)
                {
                    var size = packet[offset++];
                    if (size > next - offset) throw new InvalidDataException("Truncated DNS TXT field");
                    var field = Encoding.UTF8.GetString(packet, offset, size);
                    offset += size;
                    var equals = field.IndexOf('=');
                    if (equals > 0) fields.TryAdd(field[..equals], field[(equals + 1)..]);
                }
                var model = fields.GetValueOrDefault("am", "");
                if (model.Length == 0) model = fields.GetValueOrDefault("model", "");
                records.Add(new(name, type, ttl, null, 0, null) { Model = model });
            }
            offset = next;
        }
        return records;
    }
}

public sealed class Discovery : IDisposable
{
    private readonly UdpClient socket = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (DnsRecord Record, DateTime Expires)> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim refresh = new(0, 1);
    public event Action<IReadOnlyList<Receiver>>? Changed;
    public event Action<string>? Error;

    public void Refresh()
    {
        try { refresh.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void Start()
    {
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        _ = Task.Run(QueryLoop);
        _ = Task.Run(ReceiveLoop);
    }

    private async Task QueryLoop()
    {
        var multicast = IPAddress.Parse("224.0.0.251");
        var joined = new HashSet<IPAddress>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.SupportsMulticast)
                        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                        .Select(address => address.Address).Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToHashSet();
                    foreach (var address in joined.Except(interfaces).ToArray())
                    {
                        try { socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(multicast, address)); }
                        catch (SocketException) { }
                        joined.Remove(address);
                    }
                    foreach (var address in interfaces)
                    {
                        try
                        {
                            if (!joined.Contains(address))
                            {
                                socket.JoinMulticastGroup(multicast, address);
                                joined.Add(address);
                            }
                            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                            await socket.SendAsync(DnsPacket.Query(), new IPEndPoint(multicast, 5353), stop.Token);
                            await socket.SendAsync(DnsPacket.Query("_airplay._tcp.local"), new IPEndPoint(multicast, 5353), stop.Token);
                            string[] missing;
                            lock (gate) missing = records.Values.Where(value => value.Record.Type == 33
                                && !records.ContainsKey(value.Record.Name + "/16")
                                && (value.Record.Name.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase)
                                    || value.Record.Name.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase)))
                                .Select(value => value.Record.Name).Take(64).ToArray();
                            foreach (var name in missing)
                                await socket.SendAsync(DnsPacket.Query(name, 16), new IPEndPoint(multicast, 5353), stop.Token);
                        }
                        catch (SocketException error) { Error?.Invoke(error.Message); }
                    }
                }
                catch (NetworkInformationException error) { Error?.Invoke(error.Message); }
                Publish();
                await refresh.WaitAsync(3000, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Error?.Invoke(error.Message); }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult response;
                try { response = await socket.ReceiveAsync(stop.Token); }
                catch (SocketException error)
                {
                    Error?.Invoke(error.Message);
                    await Task.Delay(1000, stop.Token);
                    continue;
                }
                try
                {
                    var parsed = DnsPacket.Parse(response.Buffer);
                    lock (gate)
                        foreach (var record in parsed)
                            records[record.Name + "/" + record.Type] = (record, DateTime.UtcNow.AddSeconds(Math.Min(record.Ttl, 120)));
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException) { continue; }
                Publish();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Error?.Invoke(error.Message); }
    }

    private void Publish()
    {
        lock (gate)
        {
            foreach (var key in records.Where(pair => pair.Value.Expires <= DateTime.UtcNow).Select(pair => pair.Key).ToArray()) records.Remove(key);
            var receivers = new List<Receiver>();
            foreach (var (record, _) in records.Values.Where(value => value.Record.Type == 33))
            {
                if (!record.Name.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!records.TryGetValue(record.Target + "/1", out var address)) continue;
                var name = record.Name[..record.Name.IndexOf("._raop", StringComparison.OrdinalIgnoreCase)];
                var model = records.TryGetValue(record.Name + "/16", out var txt) ? txt.Record.Model : "";
                if (model.Length == 0)
                {
                    var airplay = records.Values.FirstOrDefault(value => value.Record.Type == 33
                        && value.Record.Name.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(value.Record.Target, record.Target, StringComparison.OrdinalIgnoreCase)
                        && value.Record.Port == record.Port).Record;
                    if (airplay is not null && records.TryGetValue(airplay.Name + "/16", out txt)) model = txt.Record.Model;
                }
                receivers.Add(new(record.Name, name.Contains('@') ? name[(name.IndexOf('@') + 1)..] : name,
                    address.Record.Address!, record.Port) { Model = model });
            }
            Changed?.Invoke(receivers.OrderBy(receiver => receiver.Name).ToArray());
        }
    }
    public void Dispose() { stop.Cancel(); socket.Dispose(); }
}
