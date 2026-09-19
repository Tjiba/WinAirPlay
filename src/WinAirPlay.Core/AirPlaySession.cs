using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinAirPlay.Core;

public sealed class AirPlaySession(Receiver receiver, string? endpointId, int targetMs, int initialVolume,
    Action<string> log) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly RtspConnection rtsp = new();
    private readonly NetworkClock clock = new();
    private readonly object cacheLock = new();
    private readonly (ushort Sequence, byte[] Packet, bool Valid)[] cache = Enumerable.Range(0, 512)
        .Select(_ => ((ushort)0, new byte[AudioPacket.PacketBytes], false)).ToArray();
    private readonly uint streamId = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private readonly uint firstTimestamp = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private readonly ushort firstSequence = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
    private UdpClient? timing;
    private UdpClient? control;
    private UdpClient? data;
    private TcpClient? events;
    private Thread? sender;
    private byte[] audioKey = [];
    private int controlPort;
    private int volume = initialVolume;
    private uint latencyFrames;
    private long retransmits;
    private long retransmitRequests;
    private long retransmitMisses;
    private long timingReplies;
    private int disposed;
    private string? sessionUrl;
    private bool ready;
    public event Action<string>? Failed;
    public event Action? Streaming;
    public int Volume { set => Volatile.Write(ref volume, Math.Clamp(value, 0, 100)); }
    private string Url => $"rtsp://{rtsp.LocalAddress}/{streamId}";

    public void Connect(string? password = null, bool startAudio = true)
    {
        try
        {
            log($"Connecting directly to {receiver.Name} ({receiver.Host}:{receiver.Port})");
            rtsp.Connect(receiver.Host, receiver.Port, stop.Token);
            sessionUrl = Url;
            stop.Token.ThrowIfCancellationRequested();
            using var cancelConnect = stop.Token.Register(rtsp.Dispose);
            rtsp.Request("GET", "/info");
            var srp = new SrpClient();
            var headers = new Dictionary<string, string> { ["X-Apple-HKP"] = "4" };
            var challenge = Tlv.Decode(rtsp.Request("POST", "/pair-setup",
                Tlv.Encode((6, [1]), (0, [0]), (19, [16])), "application/octet-stream", headers).Body);
            CheckPairing(challenge, 2);
            // 3939 is the protocol's public transient pairing code, never a saved user credential.
            srp.Challenge(challenge[2], challenge[3], string.IsNullOrEmpty(password) ? "3939" : password);
            var proof = Tlv.Decode(rtsp.Request("POST", "/pair-setup",
                Tlv.Encode((6, [3]), (3, srp.PublicKey), (4, srp.Proof)), "application/octet-stream", headers).Body);
            CheckPairing(proof, 4);
            srp.Verify(proof[4]);
            rtsp.Encrypt(srp.SessionKey);
            audioKey = srp.SessionKey[..32];
            rtsp.Request("GET", "/info");
            log("Transient pairing and encrypted control verified");

            var local = IPAddress.Parse(rtsp.LocalAddress);
            timing = new UdpClient(new IPEndPoint(local, 0));
            control = new UdpClient(new IPEndPoint(local, 0));
            data = new UdpClient(new IPEndPoint(local, 0));
            _ = Task.Run(TimingLoop);
            var identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            var mac = string.Join(':', Enumerable.Range(0, 6).Select(i => identity.Substring(i * 2, 2)));
            log($"NTP synchronization on {local}:{Port(timing)}");
            var session = Setup(new()
            {
                ["deviceID"] = mac, ["macAddress"] = mac, ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["timingProtocol"] = "NTP", ["timingPort"] = Port(timing), ["name"] = "WinAirPlay",
                ["model"] = "Windows", ["sourceVersion"] = "690.7.1", ["isMultiSelectAirPlay"] = true,
                ["groupContainsGroupLeader"] = false, ["osName"] = "Windows", ["osVersion"] = "10.0"
            });
            if (session.TryGetValue("eventPort", out var eventPort)) OpenEvents(Convert.ToInt32(eventPort), srp.SessionKey);
            rtsp.Request("RECORD", Url, headers: new() { ["Range"] = "npt=0-" });
            latencyFrames = (uint)Math.Round(Math.Max(20, targetMs - 20) * 44.1);
            var audio = Setup(new()
            {
                ["streams"] = new object[] { new Dictionary<string, object>
                {
                    ["type"] = 96, ["audioFormat"] = 0x800, ["ct"] = 1, ["sr"] = 44100,
                    ["spf"] = AudioPacket.Frames, ["audioMode"] = "default", ["isMedia"] = true,
                    ["shk"] = audioKey, ["controlPort"] = Port(control), ["dataPort"] = Port(data),
                    ["latencyMin"] = latencyFrames, ["latencyMax"] = 88200,
                    ["supportsDynamicStreamID"] = false, ["streamConnectionID"] = streamId
                } }
            });
            var stream = (Dictionary<string, object>)((object[])audio["streams"])[0];
            controlPort = Convert.ToInt32(stream["controlPort"]);
            data.Connect(receiver.Host, Convert.ToInt32(stream["dataPort"]));
            if (stream.TryGetValue("latencyMin", out var minimum)) latencyFrames = Math.Max(latencyFrames, Convert.ToUInt32(minimum));
            log($"Session ready; requested receiver latency: {latencyFrames / 44.1:F1} ms, total target: {targetMs} ms (not measured)");
            rtsp.Request("FLUSH", Url, headers: new()
            {
                ["RTP-Info"] = $"seq={firstSequence};rtptime={firstTimestamp}", ["Range"] = "npt=0-"
            });
            ready = true;
            if (!startAudio) return;
            SendVolume();
            _ = Task.Run(ControlLoop);
            _ = Task.Run(FeedbackLoop);
            sender = new Thread(SendLoop) { IsBackground = true, Name = "AirPlay audio", Priority = ThreadPriority.Highest };
            sender.Start();
        }
        catch { Dispose(); throw; }
    }

    private Dictionary<string, object> Setup(Dictionary<string, object> body) =>
        (Dictionary<string, object>)Plist.Decode(rtsp.Request("SETUP", Url, Plist.Encode(body)).Body);

    private static void CheckPairing(Dictionary<byte, byte[]> response, byte expected)
    {
        if (response.TryGetValue(7, out var error))
            throw new IOException($"Pairing rejected (code {Convert.ToHexString(error)}). Check the speaker's AirPlay permissions.");
        if (!response.TryGetValue(6, out var state) || state.Length != 1 || state[0] != expected)
            throw new InvalidDataException("Unexpected pairing state");
    }

    private void OpenEvents(int port, byte[] secret)
    {
        events = new TcpClient { NoDelay = true };
        events.ConnectAsync(receiver.Host, port, stop.Token).AsTask().GetAwaiter().GetResult();
        var channel = new HapStream(events.GetStream(),
            SrpClient.Derive(secret, "Events-Salt", "Events-Read-Encryption-Key"),
            SrpClient.Derive(secret, "Events-Salt", "Events-Write-Encryption-Key"));
        _ = Task.Run(() =>
        {
            try
            {
                using (channel)
                    while (!stop.IsCancellationRequested)
                    {
                        var request = RtspConnection.ReadMessage(channel);
                        var cseq = request.Headers.GetValueOrDefault("CSeq", "0");
                        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nCSeq: {cseq}\r\nContent-Length: 0\r\n\r\n");
                        channel.Write(response, 0, response.Length);
                    }
            }
            catch (Exception error) { Fail("Event channel", error); }
        });
    }

    private async Task TimingLoop()
    {
        var first = true;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var request = await timing!.ReceiveAsync(stop.Token);
                var received = clock.Now;
                if (request.RemoteEndPoint.Address.ToString() != receiver.Host || request.Buffer.Length != 32) continue;
                if (first) { log("Received NTP request from speaker"); first = false; }
                var reply = new byte[32];
                reply[0] = 0x80; reply[1] = 0xd3; reply[3] = 7;
                request.Buffer.AsSpan(24, 8).CopyTo(reply.AsSpan(8));
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), received);
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), clock.Now);
                await timing.SendAsync(reply, request.RemoteEndPoint, stop.Token);
                Interlocked.Increment(ref timingReplies);
            }
        }
        catch (Exception error) { Fail("NTP clock", error); }
    }

    private async Task ControlLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var request = await control!.ReceiveAsync(stop.Token);
                var bytes = request.Buffer;
                if (request.RemoteEndPoint.Address.ToString() != receiver.Host || bytes.Length < 8 || (bytes[1] & 127) != 85) continue;
                var sequence = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
                var count = Math.Min(512, (int)BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6)));
                Interlocked.Add(ref retransmitRequests, count);
                for (var i = 0; i < count; i++)
                {
                    var wanted = unchecked((ushort)(sequence + i));
                    byte[]? original;
                    lock (cacheLock)
                    {
                        var entry = cache[wanted % cache.Length];
                        original = entry.Valid && entry.Sequence == wanted ? (byte[])entry.Packet.Clone() : null;
                    }
                    if (original is null)
                    {
                        Interlocked.Increment(ref retransmitMisses);
                        continue;
                    }
                    var reply = new byte[original.Length + 4];
                    reply[0] = 0x80; reply[1] = 0xd6;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), wanted);
                    original.CopyTo(reply, 4);
                    await control.SendAsync(reply, request.RemoteEndPoint, stop.Token);
                    Interlocked.Increment(ref retransmits);
                }
            }
        }
        catch (Exception error) { Fail("Retransmissions", error); }
    }

    private void SendLoop()
    {
        var priority = NativeAudio.wa_priority_begin();
        var timer = NativeAudio.wa_timer_create();
        try
        {
            using var capture = new NativeAudio(string.IsNullOrEmpty(endpointId) ? null : endpointId);
            using var cipher = new ChaCha20Poly1305(audioKey);
            var pcm = new byte[AudioPacket.PcmBytes];
            var packet = new byte[AudioPacket.PacketBytes];
            // Keep 20 ms in reserve after consuming the first packet.
            capture.Prime(882 + AudioPacket.Frames, stop.Token);
            log($"Capture primed: {capture.Stats.Queued / 44.1:F1} ms; sinc drift correction");
            var anchor = NativeAudio.wa_clock();
            var networkAnchor = clock.Elapsed;
            var sequence = firstSequence;
            ulong counter = 0;
            long frames = 0;
            double maxGap = 0, maxReadWait = 0, maxLate = 0, lastSend = anchor, lastReport = anchor;
            uint minQueue = uint.MaxValue;
            uint maxQueue = 0;
            long latePackets = 0;
            double correction = 0;
            double queueAverage = 882;
            while (!stop.IsCancellationRequested)
            {
                NativeAudio.wa_timer_wait(timer, anchor + frames / 44100.0);
                var readStart = NativeAudio.wa_clock();
                if (capture.Read(pcm, AudioPacket.Frames) != AudioPacket.Frames)
                    throw new IOException($"Capture interrupted (0x{capture.Stats.Error:X8})");
                maxReadWait = Math.Max(maxReadWait, NativeAudio.wa_clock() - readStart);
                var queued = capture.Stats.Queued;
                minQueue = Math.Min(minQueue, queued);
                maxQueue = Math.Max(maxQueue, queued);
                var timestamp = unchecked(firstTimestamp + (uint)frames);
                if (counter % 125 == 0)
                {
                    var sync = AudioPacket.Sync(unchecked(timestamp - latencyFrames), timestamp,
                        clock.At(networkAnchor + frames / 44100.0), counter == 0);
                    control!.Send(sync, new IPEndPoint(IPAddress.Parse(receiver.Host), controlPort));
                }
                AudioPacket.Encode(packet, pcm, sequence, timestamp, streamId, counter, counter == 0, cipher);
                data!.Send(packet);
                lock (cacheLock)
                {
                    ref var entry = ref cache[sequence % cache.Length];
                    packet.CopyTo(entry.Packet, 0);
                    entry.Sequence = sequence;
                    entry.Valid = true;
                }
                var now = NativeAudio.wa_clock();
                maxGap = Math.Max(maxGap, now - lastSend);
                maxLate = Math.Max(maxLate, now - (anchor + frames / 44100.0));
                if (now > anchor + (frames + latencyFrames) / 44100.0) latePackets++;
                lastSend = now;
                if (counter == 0) { log("First audio packet sent"); Streaming?.Invoke(); }
                if (counter % 125 == 0)
                {
                    queueAverage += 0.05 * (capture.Stats.Queued - queueAverage);
                    correction = Math.Clamp((queueAverage - 882) * 0.0000002, -0.002, 0.002);
                    capture.SetDrift(correction);
                }
                if (now - lastReport >= 5)
                {
                    var stats = capture.Stats;
                    log($"Audio : max interval={maxGap * 1000:F2} ms, buffer={stats.Queued / 44.1:F1} ms, " +
                        $"min buffer={minQueue / 44.1:F1} ms, max PCM wait={maxReadWait * 1000:F2} ms, " +
                        $"max delay={maxLate * 1000:F2} ms, discontinuities={stats.Discontinuities}, " +
                        $"overflows={stats.Overflows}, retx={Interlocked.Read(ref retransmits)}, NTP={Interlocked.Read(ref timingReplies)}, " +
                        $"max buffer={maxQueue / 44.1:F1} ms, drift={correction * 1000000:F1} ppm, " +
                        $"past playback deadline={latePackets}, requested packets={Interlocked.Read(ref retransmitRequests)}, " +
                        $"cache misses={Interlocked.Read(ref retransmitMisses)}");
                    maxGap = 0; maxReadWait = 0; maxLate = 0; minQueue = uint.MaxValue; maxQueue = 0; lastReport = now;
                }
                if (now - (anchor + frames / 44100.0) > 0.5) throw new IOException("Sender is more than 500 ms late");
                frames += AudioPacket.Frames;
                sequence = unchecked((ushort)(sequence + 1));
                counter++;
            }
        }
        catch (Exception error) { Fail("Audio", error); }
        finally { NativeAudio.wa_timer_destroy(timer); NativeAudio.wa_priority_end(priority); }
    }

    private void SendVolume()
    {
        var level = Volatile.Read(ref volume);
        var db = level == 0 ? -144 : -30 + level * 0.3;
        rtsp.Request("SET_PARAMETER", Url, Encoding.ASCII.GetBytes("volume: " + db.ToString("F2", CultureInfo.InvariantCulture) + "\r\n"), "text/parameters");
    }
    private async Task FeedbackLoop()
    {
        var previous = Volatile.Read(ref volume);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop.Token);
                if (previous != Volatile.Read(ref volume)) { SendVolume(); previous = Volatile.Read(ref volume); }
                rtsp.Request("POST", "/feedback");
            }
        }
        catch (Exception error) { Fail("Control", error); }
    }
    private void Fail(string stage, Exception error)
    {
        if (stop.IsCancellationRequested) return;
        log($"{stage} : {error.Message}");
        stop.Cancel();
        Failed?.Invoke($"{stage} : {error.Message}");
    }
    private static int Port(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    public void Cancel()
    {
        stop.Cancel();
        rtsp.Dispose();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        if (sender != Thread.CurrentThread) sender?.Join(1000);
        if (ready && sessionUrl is not null) rtsp.Teardown(sessionUrl);
        events?.Dispose(); timing?.Dispose(); control?.Dispose(); data?.Dispose();
        rtsp.Dispose();
        CryptographicOperations.ZeroMemory(audioKey);
    }
}
