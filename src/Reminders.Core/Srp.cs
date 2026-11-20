using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Reminders.Core;

internal static class Srp
{
    // RFC 5054's 2048-bit group; generator 2. Integers on the wire are unsigned
    // big-endian, not .NET BigInteger's default signed little-endian format.
    private const string Modulus = "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73";
    private static readonly BigInteger N = Integer(Convert.FromHexString(Modulus));
    internal static BigInteger Integer(byte[] value) => new(value, true, true);
    private static byte[] Bytes(BigInteger n) => n.ToByteArray(true, true);
    private static byte[] Pad(BigInteger n) { var b = Bytes(n); var result = new byte[256]; b.CopyTo(result, result.Length - b.Length); return result; }
    private static byte[] Hash(params byte[][] parts) { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach (var p in parts) h.AppendData(p); return h.GetHashAndReset(); }
    public static byte[] Public(BigInteger a) => Bytes(BigInteger.ModPow(2, a, N));
    internal static byte[] Derive(byte[] password, byte[] salt, int iterations, string protocol)
    {
        var digest = SHA256.HashData(password); var filtered = protocol == "s2k_fo" ? Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant()) : digest.ToArray();
        try { return Rfc2898DeriveBytes.Pbkdf2(filtered, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(filtered); CryptographicOperations.ZeroMemory(digest); }
    }
    public static (byte[] M1, byte[] M2) Proof(string user, byte[] password, BigInteger a, JsonNode challenge)
    {
        var protocol = challenge.Text("protocol") ?? ""; var iterations = challenge.Number("iteration");
        if (protocol is not ("s2k" or "s2k_fo") || iterations is < 1 or > 1000000) throw new CoreException("AUTH_REQUIRED", "iCloud returned invalid sign-in parameters").WithPresentation("ErrorInvalidICloudSignInParameters");
        var bBytes = Convert.FromBase64String(challenge.Text("b") ?? ""); var salt = Convert.FromBase64String(challenge.Text("salt") ?? ""); var b = Integer(bBytes);
        if (bBytes.Length is 0 or > 256 || b % N == 0 || salt.Length is 0 or > 64) throw new CoreException("AUTH_REQUIRED", "iCloud returned an invalid sign-in challenge").WithPresentation("ErrorInvalidICloudSignInChallenge");
        var derived = Derive(password, salt, (int)iterations, protocol); byte[]? key = null, identity = null;
        try
        {
            identity = Hash(Encoding.ASCII.GetBytes(":"), derived); var x = Integer(Hash(salt, identity)); var pub = BigInteger.ModPow(2, a, N);
            var k = Integer(Hash(Pad(N), Pad(2))); var u = Integer(Hash(Pad(pub), Pad(b)));
            if (u == 0) throw new CoreException("AUTH_REQUIRED", "iCloud returned an invalid sign-in challenge").WithPresentation("ErrorInvalidICloudSignInChallenge");
            var basis = (b + N - k * BigInteger.ModPow(2, x, N) % N) % N;
            key = Hash(Bytes(BigInteger.ModPow(basis, a + u * x, N)));
            var hn = Hash(Pad(N)); var hg = Hash(Pad(2)); var xor = hn.Zip(hg, (l, r) => (byte)(l ^ r)).ToArray();
            var m1 = Hash(xor, Hash(Encoding.UTF8.GetBytes(user)), salt, Bytes(pub), Bytes(b), key);
            return (m1, Hash(Bytes(pub), m1, key));
        }
        finally { CryptographicOperations.ZeroMemory(derived); if (key is not null) CryptographicOperations.ZeroMemory(key); if (identity is not null) CryptographicOperations.ZeroMemory(identity); }
    }
}
