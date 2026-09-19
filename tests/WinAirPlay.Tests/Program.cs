using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WinAirPlay.Core;

if (args.Length > 0 && args[0] is "--probe" or "--stream")
{
    try
    {
        var host = args[1];
        var port = args.Length > 2 ? int.Parse(args[2]) : 7000;
        using var session = new AirPlaySession(new(host, "Diagnostic", host, port), null, 100, 50, Console.WriteLine);
        string? failure = null;
        session.Failed += error => failure = error;
        session.Connect(startAudio: args[0] == "--stream");
        if (args[0] == "--stream") Thread.Sleep(15000);
        if (failure is not null) throw new IOException(failure);
        Console.WriteLine(args[0] == "--probe" ? "PASS: appairage, contrôle chiffré et SETUP réel, sans audio." : "PASS: émission 15 secondes (écoute non vérifiée).");
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Length > 0 && args[0] == "--discover")
{
    using var discovery = new Discovery();
    discovery.Changed += receivers => Console.WriteLine(string.Join(" | ", receivers));
    discovery.Error += Console.Error.WriteLine;
    discovery.Start(); Thread.Sleep(10000); return 0;
}
if (args.Length > 0 && args[0] == "--capture")
{
    foreach (var endpoint in NativeAudio.Devices()) Console.WriteLine(endpoint);
    using var capture = new NativeAudio(null);
    var pcm = new byte[AudioPacket.PcmBytes];
    var timer = NativeAudio.wa_timer_create();
    var priority = NativeAudio.wa_priority_begin();
    try
    {
        capture.Prime(882 + AudioPacket.Frames, CancellationToken.None);
        Check(capture.Stats.Queued >= 882 + AudioPacket.Frames, "capture prebuffer filled");
        var start = NativeAudio.wa_clock();
        for (var i = 0; i < 1250; i++)
        {
            NativeAudio.wa_timer_wait(timer, start + i * 352.0 / 44100);
            Check(capture.Read(pcm, 352) == 352, "capture complète");
        }
        var stats = capture.Stats;
        Console.WriteLine($"WASAPI : frames={stats.Delivered}, réserve={stats.Queued}, débordements={stats.Overflows}, discontinuités={stats.Discontinuities}, erreur={stats.Error}");
        Check(stats.Delivered == 440000 && stats.Overflows == 0 && stats.Error == 0, "capture 10 s");
    }
    finally { NativeAudio.wa_timer_destroy(timer); NativeAudio.wa_priority_end(priority); }
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("TLV fragments et troncature", () =>
    {
        var value = RandomNumberGenerator.GetBytes(700);
        var fields = Tlv.Decode(Tlv.Encode((3, value), (6, [2])));
        Check(fields[3].SequenceEqual(value) && fields[6][0] == 2, "TLV fragmentation");
        Throws<InvalidDataException>(() => Tlv.Decode([3, 4, 0]));
    }),
    ("SRP client contre calcul serveur indépendant", () =>
    {
        byte[] Hash(params byte[][] parts) => SHA512.HashData(parts.SelectMany(part => part).ToArray());
        BigInteger Num(byte[] bytes) => new(bytes, true, true);
        byte[] Pad(BigInteger n) { var bytes = n.ToByteArray(true, true); return new byte[384 - bytes.Length].Concat(bytes).ToArray(); }
        var n = SrpClient.Modulus;
        var salt = Convert.FromHexString("BEB25379D1A8581EB5A727673A2441EE");
        var b = Num(RandomNumberGenerator.GetBytes(32));
        var x = Num(Hash(salt, Hash(Encoding.UTF8.GetBytes("Pair-Setup:3939"))));
        var verifier = BigInteger.ModPow(5, x, n);
        var multiplier = Num(Hash(Pad(n), Pad(5)));
        var serverPublic = Pad((multiplier * verifier + BigInteger.ModPow(5, b, n)) % n);
        var client = new SrpClient();
        client.Challenge(salt, serverPublic, "3939");
        var u = Num(Hash(client.PublicKey, serverPublic));
        var secret = BigInteger.ModPow(Num(client.PublicKey) * BigInteger.ModPow(verifier, u, n) % n, b, n);
        var key = Hash(secret.ToByteArray(true, true));
        Check(key.SequenceEqual(client.SessionKey), "SRP shared secret");
        var xor = Hash(Pad(n)).Zip(Hash([5]), (a, c) => (byte)(a ^ c)).ToArray();
        var proof = Hash(xor, Hash(Encoding.UTF8.GetBytes("Pair-Setup")), salt, client.PublicKey, serverPublic, key);
        Check(proof.SequenceEqual(client.Proof), "SRP client proof");
        client.Verify(Hash(client.PublicKey, proof, key));
        Throws<CryptographicException>(() => client.Verify(new byte[64]));
        Throws<CryptographicException>(() => client.Challenge(salt, new byte[384], "3939"));
    }),
    ("HAP fragmentation, compteurs et authentification", () =>
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var plain = RandomNumberGenerator.GetBytes(4097);
        using var wire = new MemoryStream();
        using var writer = new HapStream(wire, key, key);
        writer.Write(plain, 0, plain.Length);
        var encoded = wire.ToArray();
        using var reader = new HapStream(new MemoryStream(encoded), key, key);
        var output = new byte[plain.Length]; reader.ReadExactly(output);
        Check(output.SequenceEqual(plain), "HAP payload");
        encoded[10] ^= 1;
        using var tampered = new HapStream(new MemoryStream(encoded), key, key);
        Throws<AuthenticationTagMismatchException>(() => tampered.ReadExactly(output));
    }),
    ("Plist imbriqué, entiers 64 bits et Unicode", () =>
    {
        var input = new Dictionary<string, object> { ["streams"] = new object[] { new Dictionary<string, object> { ["type"] = 96, ["key"] = new byte[32] } }, ["name"] = "Séjour 🎵", ["enabled"] = true, ["large"] = ulong.MaxValue };
        var result = (Dictionary<string, object>)Plist.Decode(Plist.Encode(input));
        Check((string)result["name"] == "Séjour 🎵" && (bool)result["enabled"], "Unicode / bool");
        Check(Convert.ToUInt64(result["large"]) == ulong.MaxValue, "uint64");
        var nested = (Dictionary<string, object>)((object[])result["streams"])[0];
        Check(Convert.ToInt32(nested["type"]) == 96 && ((byte[])nested["key"]).Length == 32, "stream");
        Throws<InvalidDataException>(() => Plist.Decode(new byte[50]));
    }),
    ("RTP PCM big-endian, nonce et horodatages", () =>
    {
        var pcm = new byte[AudioPacket.PcmBytes]; pcm[0] = 0x34; pcm[1] = 0x12; pcm[2] = 0xfe; pcm[3] = 0xff;
        var packet = new byte[AudioPacket.PacketBytes];
        using var cipher = new ChaCha20Poly1305(new byte[32]);
        AudioPacket.Encode(packet, pcm, 65535, 0xffffffff, 0x12345678, 0x1020304050607080, true, cipher);
        Check(packet[0] == 0x80 && packet[1] == 0xe0 && packet[2] == 255 && packet[3] == 255, "RTP header");
        Check(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) == uint.MaxValue, "RTP timestamp");
        var nonce = new byte[12]; packet.AsSpan(packet.Length - 8).CopyTo(nonce.AsSpan(4));
        Check(BinaryPrimitives.ReadUInt64LittleEndian(nonce.AsSpan(4)) == 0x1020304050607080, "nonce");
        var decoded = new byte[pcm.Length];
        cipher.Decrypt(nonce, packet.AsSpan(12, pcm.Length), packet.AsSpan(12 + pcm.Length, 16), decoded, packet.AsSpan(4, 8));
        Check(decoded[0] == 0x12 && decoded[1] == 0x34 && decoded[2] == 255 && decoded[3] == 254, "PCM endian");
        var sync = AudioPacket.Sync(100, 4510, 0x123456789abcdef0, true);
        Check(sync.AsSpan(0, 4).SequenceEqual(new byte[] { 0x90, 0xd4, 0, 4 }), "AirPlay sync without legacy latency hint");
        Check(AudioPacket.Sync(100, 4510, 0x123456789abcdef0, false)[0] == 0x80, "sync start marker only on first packet");
        Check(BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(4)) == 100 && BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(16)) == 4510, "100 ms sync offset");
    }),
    ("RTSP corps fragmenté et limites", () =>
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\ncseq: 1\r\ncontent-length: 4\r\n\r\ntest"));
        var response = RtspConnection.ReadMessage(stream);
        Check(response.Headers["CSeq"] == "1" && Encoding.ASCII.GetString(response.Body) == "test", "RTSP fields");
        using var invalid = new MemoryStream(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nContent-Length: 1048577\r\n\r\n"));
        Throws<InvalidDataException>(() => RtspConnection.ReadMessage(invalid));
    }),
    ("DNS pointeur cyclique rejeté", () =>
    {
        Check(DnsPacket.Parse(DnsPacket.Query()).Count == 0, "DNS question");
        var cycle = new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 12, 0, 12, 0, 1 };
        Throws<InvalidDataException>(() => DnsPacket.Parse(cycle));
    }),
    ("Horloge NTP durée monotone", () =>
    {
        var clock = new NetworkClock();
        var delta = clock.At(1) - clock.At(0);
        Check(delta == 1UL << 32, "NTP one second");
        Check((clock.Now >> 32) > 3900000000, "NTP epoch");
    })
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests réussis");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
