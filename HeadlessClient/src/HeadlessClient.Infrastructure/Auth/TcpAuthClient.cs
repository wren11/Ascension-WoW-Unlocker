using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Auth;
using HeadlessClient.Infrastructure.Crypto;

namespace HeadlessClient.Infrastructure.Auth;

public sealed class TcpAuthClient : IAuthClient
{
    private readonly IHeadlessOptions _options;

    public TcpAuthClient(IHeadlessOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AuthLoginResult> LoginAndListRealmsAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(_options.AuthHost))
        {
            throw new InvalidOperationException("AuthHost is not configured.");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(_options.AuthHost, _options.AuthPort, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var challengePacket = BuildLogonChallengePacket(credentials.Account, client);
        await stream.WriteAsync(challengePacket, cancellationToken).ConfigureAwait(false);

        var challengeRaw = await ReadAtLeastAsync(stream, 3, cancellationToken).ConfigureAwait(false);
        if (challengeRaw.Length == 0)
        {
            throw new InvalidOperationException(
                "Auth server closed the connection after logon challenge. " +
                "Ascension requires a sealed 638-byte challenge (set AscensionChallengePath).");
        }

        if (challengeRaw.Length >= 3 && challengeRaw[2] == 0)
        {
            challengeRaw = await ReadExactGrowAsync(stream, challengeRaw, 3 + 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1, cancellationToken)
                .ConfigureAwait(false);
        }

        var challenge = AuthPacketCodec.DecodeLogonChallenge(challengeRaw);
        if (challenge.Error != 0)
        {
            throw new InvalidOperationException($"Auth logon challenge failed with error 0x{challenge.Error:X2}.");
        }

        // Live Ascension under Extensions.dll REPLACES Challenge @ RVA 0x5A83E0 with
        // Ext RVA 0xE5DF0: copies 40-byte K from auth singleton +0x140/+0x150/+0x160.
        // Stock SRP Sha1Interleave never runs. Wire A/M1 are theater (often zeros).
        // World K must come from the seal-bound key (override until seal→K is ported).
        var a = Srp6.GeneratePrivateEphemeral();
        var A = Srp6.ComputeClientPublicEphemeral(a);
        var stockSessionKey = Srp6.ComputeSessionKey(
            credentials.Account,
            credentials.Password,
            challenge.Salt,
            challenge.B,
            A,
            a);
        var M1 = Srp6.ComputeClientProof(credentials.Account, challenge.Salt, A, challenge.B, stockSessionKey);

        byte[] sessionKey = stockSessionKey;
        var overridePath = _options.SessionKeyOverridePath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            var dumped = await File.ReadAllBytesAsync(overridePath, cancellationToken).ConfigureAwait(false);
            if (dumped.Length != 40)
            {
                throw new InvalidOperationException(
                    $"SessionKeyOverridePath must be 40 bytes, got {dumped.Length}: {overridePath}");
            }

            sessionKey = dumped;
            Console.WriteLine($"auth: using SessionKeyOverridePath ({overridePath}) — Extensions seal K, not stock SRP.");
        }
        else
        {
            Console.WriteLine(
                "auth: WARNING — no SessionKeyOverridePath; stock SRP K is rejected by Ascension world " +
                "(Extensions Challenge hook supplies seal K).");
        }

        // Match live AuthWire: zero A/M1 on the wire. CRC is still sent (live uses SHA1(A||seed)).
        var wireA = new byte[32];
        var wireM1 = new byte[20];
        var crcHash = new byte[20];
        RandomNumberGenerator.Fill(crcHash);

        var proofPacket = AuthPacketCodec.EncodeLogonProof(wireA, wireM1, crcHash);
        await stream.WriteAsync(proofPacket, cancellationToken).ConfigureAwait(false);

        var proofRaw = await ReadAtLeastAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        if (proofRaw.Length >= 2 && proofRaw[1] == 0)
        {
            // Ascension proof response is 44 bytes (M2 + extended tail), not stock 32.
            proofRaw = await ReadExactGrowAsync(stream, proofRaw, 44, cancellationToken).ConfigureAwait(false);
        }

        var proof = AuthPacketCodec.DecodeLogonProof(proofRaw);
        if (proof.Error != 0)
        {
            throw new InvalidOperationException($"Auth logon proof failed with error 0x{proof.Error:X2}.");
        }

        // Ascension auth is Extensions theater: M2 is not SHA1(wireA||wireM1||K).
        // Keep stock M2 check only as a diagnostic against the unused SRP transcript.
        var expectedM2 = Srp6.ComputeServerProof(A, M1, stockSessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expectedM2, proof.M2))
        {
            Console.WriteLine(
                "auth: server M2 != SHA1(SRP A||M1||stockK) (expected under Extensions theater).");
        }

        var realmRequest = AuthPacketCodec.EncodeRealmListRequest();
        await stream.WriteAsync(realmRequest, cancellationToken).ConfigureAwait(false);

        var realmHeader = await ReadExactAsync(stream, 3, cancellationToken).ConfigureAwait(false);
        var realmSize = BitConverter.ToUInt16(realmHeader, 1);
        var realmBody = await ReadExactAsync(stream, realmSize, cancellationToken).ConfigureAwait(false);
        var realmPacket = new byte[3 + realmBody.Length];
        Buffer.BlockCopy(realmHeader, 0, realmPacket, 0, 3);
        Buffer.BlockCopy(realmBody, 0, realmPacket, 3, realmBody.Length);
        var realms = AuthPacketCodec.DecodeRealmList(realmPacket);
        return new AuthLoginResult(realms, sessionKey, proof.Tail);
    }

    private byte[] BuildLogonChallengePacket(string account, TcpClient client)
    {
        try
        {
            return AscensionChallengeLocator.Resolve(
                _options.AscensionChallengePath,
                GetRuntimeInstDir());
        }
        catch (FileNotFoundException)
        {
            // Fall through to stock WotLK challenge (Ascension auth rejects it).
        }

        var localIp = ((IPEndPoint?)client.Client.LocalEndPoint)?.Address ?? IPAddress.Loopback;
        var ipBytes = localIp.MapToIPv4().GetAddressBytes();
        var ip = BitConverter.ToUInt32(ipBytes, 0);
        return AuthPacketCodec.EncodeLogonChallenge(account, _options.ClientBuild, ip);
    }

    private string? GetRuntimeInstDir() => _options.RuntimeInstDir;

    private static async Task<byte[]> ReadAtLeastAsync(NetworkStream stream, int minimum, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Max(minimum, 256)];
        var read = 0;
        while (read < minimum)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return Array.Empty<byte>();
                }

                throw new EndOfStreamException("Auth server closed the connection.");
            }

            read += n;
        }

        if (read == buffer.Length)
        {
            return buffer;
        }

        var exact = new byte[read];
        Buffer.BlockCopy(buffer, 0, exact, 0, read);
        return exact;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Auth server closed the connection.");
            }

            read += n;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadExactGrowAsync(
        NetworkStream stream,
        byte[] existing,
        int totalNeeded,
        CancellationToken cancellationToken)
    {
        if (existing.Length >= totalNeeded)
        {
            return existing;
        }

        var buffer = new byte[totalNeeded];
        Buffer.BlockCopy(existing, 0, buffer, 0, existing.Length);
        var read = existing.Length;
        while (read < totalNeeded)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, totalNeeded - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Auth server closed the connection.");
            }

            read += n;
        }

        return buffer;
    }
}
