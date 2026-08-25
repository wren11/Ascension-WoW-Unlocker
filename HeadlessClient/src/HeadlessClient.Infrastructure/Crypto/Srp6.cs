using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HeadlessClient.Infrastructure.Crypto;

public static class Srp6
{
    public static readonly BigInteger N = BigInteger.Parse(
        "0894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7",
        System.Globalization.NumberStyles.HexNumber);

    public static readonly BigInteger G = 7;

    public static readonly BigInteger K = 3;

    public static byte[] ComputeClientPublicEphemeral(byte[] a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var aInt = FromLittleEndian(a);
        var A = BigInteger.ModPow(G, aInt, N);
        return ToFixedLittleEndian(A, 32);
    }

    public static byte[] ComputeSessionKey(
        string account,
        string password,
        byte[] salt,
        byte[] B,
        byte[] A,
        byte[] a)
        => ComputeSessionKey(account, password, salt, B, A, a, upperPassword: true);

    /// <param name="upperPassword">
    /// Ascension IdentitySet ASCII-uppercases both account and password before I=SHA1(user:pass).
    /// Pass false only for casing experiments.
    /// </param>
    public static byte[] ComputeSessionKey(
        string account,
        string password,
        byte[] salt,
        byte[] B,
        byte[] A,
        byte[] a,
        bool upperPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(B);
        ArgumentNullException.ThrowIfNull(A);
        ArgumentNullException.ThrowIfNull(a);

        // Ascension strips '#' for identity/M1 username hash only (RVA 0x4CD86D).
        var hashAccount = account.Split('#', 2)[0];
        var user = AsciiUpper(hashAccount);
        var pass = upperPassword ? AsciiUpper(password) : password;
        var x = CalculateX(user, pass, salt);
        var u = CalculateU(A, B);
        var aInt = FromLittleEndian(a);
        var BInt = FromLittleEndian(B);
        var xInt = FromLittleEndian(x);
        var uInt = FromLittleEndian(u);

        if (BInt % N == 0)
        {
            throw new InvalidOperationException("Server public ephemeral B is not valid modulo N.");
        }

        if (uInt == 0)
        {
            throw new InvalidOperationException("Scrambling parameter u must not be zero.");
        }

        var gX = BigInteger.ModPow(G, xInt, N);
        var kgx = (K * gX) % N;
        var baseValue = Mod(BInt - kgx, N);
        var S = BigInteger.ModPow(baseValue, aInt + uInt * xInt, N);
        return Sha1Interleave(ToFixedLittleEndian(S, 32));
    }

    /// <summary>ASCII a-z→A-Z only — mirrors Ascension strupr @ RVA 0x76F6C0 / 0x88E558.</summary>
    public static string AsciiUpper(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z')
            {
                chars[i] = (char)(chars[i] - 32);
            }
        }

        return new string(chars);
    }

    public static byte[] ComputeClientProof(string account, byte[] salt, byte[] A, byte[] B, byte[] sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(A);
        ArgumentNullException.ThrowIfNull(B);
        ArgumentNullException.ThrowIfNull(sessionKey);

        var user = AsciiUpper(account.Split('#', 2)[0]);
        var nHash = SHA1.HashData(ToFixedLittleEndian(N, 32));
        var gHash = SHA1.HashData(ToFixedLittleEndian(G, 1));
        var xorHash = new byte[20];
        for (var i = 0; i < 20; i++)
        {
            xorHash[i] = (byte)(nHash[i] ^ gHash[i]);
        }

        var userHash = SHA1.HashData(Encoding.ASCII.GetBytes(user));
        using var ms = new MemoryStream(20 + 20 + salt.Length + A.Length + B.Length + sessionKey.Length);
        ms.Write(xorHash);
        ms.Write(userHash);
        ms.Write(salt);
        ms.Write(A);
        ms.Write(B);
        ms.Write(sessionKey);
        return SHA1.HashData(ms.ToArray());
    }

    /// <summary>Server proof M2 = SHA1(A || M1 || K). Matches Ascension auth verify path.</summary>
    public static byte[] ComputeServerProof(byte[] A, byte[] M1, byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(A);
        ArgumentNullException.ThrowIfNull(M1);
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (A.Length != 32 || M1.Length != 20 || sessionKey.Length != 40)
        {
            throw new ArgumentException("M2 inputs must be A=32, M1=20, K=40.");
        }

        var buf = new byte[32 + 20 + 40];
        Buffer.BlockCopy(A, 0, buf, 0, 32);
        Buffer.BlockCopy(M1, 0, buf, 32, 20);
        Buffer.BlockCopy(sessionKey, 0, buf, 52, 40);
        return SHA1.HashData(buf);
    }

    public static byte[] CalculateX(string usernameUpper, string passwordUpper, byte[] salt)
    {
        var interim = SHA1.HashData(Encoding.ASCII.GetBytes(usernameUpper + ":" + passwordUpper));
        var combined = new byte[salt.Length + interim.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(interim, 0, combined, salt.Length, interim.Length);
        return SHA1.HashData(combined);
    }

    public static byte[] CalculateU(byte[] A, byte[] B)
    {
        var combined = new byte[A.Length + B.Length];
        Buffer.BlockCopy(A, 0, combined, 0, A.Length);
        Buffer.BlockCopy(B, 0, combined, A.Length, B.Length);
        return SHA1.HashData(combined);
    }

    public static byte[] Sha1Interleave(byte[] S)
    {
        if (S.Length == 0)
        {
            throw new ArgumentException("S key must not be empty.", nameof(S));
        }

        var offset = 0;
        while (offset < S.Length && S[offset] == 0)
        {
            offset++;
        }

        var length = S.Length - offset;
        if ((length & 1) != 0)
        {
            offset++;
            length--;
        }

        if (length <= 0)
        {
            throw new InvalidOperationException("S key has no usable bytes for interleave.");
        }

        var half = length / 2;
        var even = new byte[half];
        var odd = new byte[half];
        for (var i = 0; i < half; i++)
        {
            even[i] = S[offset + i * 2];
            odd[i] = S[offset + i * 2 + 1];
        }

        var evenHash = SHA1.HashData(even);
        var oddHash = SHA1.HashData(odd);
        var sessionKey = new byte[40];
        for (var i = 0; i < 20; i++)
        {
            sessionKey[i * 2] = evenHash[i];
            sessionKey[i * 2 + 1] = oddHash[i];
        }

        return sessionKey;
    }

    public static byte[] GeneratePrivateEphemeral(int byteLength = 32)
    {
        if (byteLength < 19)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        var a = new byte[byteLength];
        RandomNumberGenerator.Fill(a);
        return a;
    }

    public static BigInteger FromLittleEndian(ReadOnlySpan<byte> data)
    {
        var tmp = new byte[data.Length + 1];
        data.CopyTo(tmp);
        return new BigInteger(tmp);
    }

    public static byte[] ToFixedLittleEndian(BigInteger value, int length)
    {
        if (value.Sign < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");
        }

        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = new byte[length];
        Buffer.BlockCopy(raw, 0, result, 0, Math.Min(raw.Length, length));
        return result;
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var result = value % modulus;
        if (result < 0)
        {
            result += modulus;
        }

        return result;
    }
}
