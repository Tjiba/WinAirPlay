using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WinAirPlay.Core;

public sealed class SrpClient
{
    // RFC 5054, 3072-bit group; public protocol constants, not credentials.
    public static readonly BigInteger Modulus = new(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF"), true, true);
    private readonly BigInteger privateKey = new(RandomNumberGenerator.GetBytes(32), true, true);
    public byte[] PublicKey { get; }
    public byte[] SessionKey { get; private set; } = [];
    public byte[] Proof { get; private set; } = [];

    public SrpClient() => PublicKey = Pad(BigInteger.ModPow(5, privateKey, Modulus));

    public void Challenge(byte[] salt, byte[] serverKey, string pin)
    {
        if (salt.Length is < 1 or > 64 || serverKey.Length is < 1 or > 384)
            throw new InvalidDataException("Invalid SRP challenge");
        var b = new BigInteger(serverKey, true, true);
        if (b % Modulus == 0) throw new CryptographicException("Invalid SRP public key");
        var k = Number(Hash(Pad(Modulus), Pad(5)));
        var x = Number(Hash(salt, Hash(Encoding.UTF8.GetBytes("Pair-Setup:" + pin))));
        var u = Number(Hash(PublicKey, Pad(b)));
        if (u.IsZero) throw new CryptographicException("Invalid SRP scrambling parameter");
        var basis = ((b - k * BigInteger.ModPow(5, x, Modulus)) % Modulus + Modulus) % Modulus;
        var shared = BigInteger.ModPow(basis, privateKey + u * x, Modulus);
        SessionKey = Hash(shared.ToByteArray(true, true));
        var xor = Hash(Pad(Modulus));
        var generatorHash = Hash([5]);
        for (var i = 0; i < xor.Length; i++) xor[i] ^= generatorHash[i];
        Proof = Hash(xor, Hash(Encoding.UTF8.GetBytes("Pair-Setup")), salt, PublicKey, serverKey, SessionKey);
    }

    public void Verify(byte[] proof)
    {
        if (!CryptographicOperations.FixedTimeEquals(Hash(PublicKey, Proof, SessionKey), proof))
            throw new CryptographicException("Receiver SRP proof is invalid");
    }

    public static byte[] Pad(BigInteger value)
    {
        var bytes = value.ToByteArray(true, true);
        var result = new byte[384];
        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }

    public static byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts) hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private static BigInteger Number(byte[] data) => new(data, true, true);

    public static byte[] Derive(byte[] secret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32,
            Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(info));
}
