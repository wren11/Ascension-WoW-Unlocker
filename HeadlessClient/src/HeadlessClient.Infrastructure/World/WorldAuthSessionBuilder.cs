using System.Security.Cryptography;
using System.Text;
using HeadlessClient.Domain.Auth;
using HeadlessClient.Domain.Protocol;

namespace HeadlessClient.Infrastructure.World;

/// <summary>
/// Builds Ascension CMSG_AUTH_SESSION (0x01ED).
/// Wire layout verified from live ExtProxy capture ExtProxy64.auth.session.*.bin
/// (Ascension.exe SendAuthSession RVA 0x64830 + real addon zlib block).
/// </summary>
public static class WorldAuthSessionBuilder
{
    /// <summary>Hardcoded in Ascension.exe SendAuthSession (only PUSH 0x3038 in .text).</summary>
    public const uint AscensionWireBuild = 12344;

    /// <summary>
    /// Live Ascension sends dosResponse=1 (matches SMSG_AUTH_CHALLENGE unk).
    /// </summary>
    public const ulong AscensionDosResponse = 1;

    private static byte[]? _cachedAddonInfo;
    private static readonly object AddonLock = new();

    public static Packet Build(
        RealmInfo realm,
        int clientBuild,
        string account,
        uint serverSeed,
        uint clientSeed,
        byte[] sessionKey,
        ReadOnlySpan<byte> authProofTail = default,
        uint loginServerId = 0,
        uint loginServerType = 0,
        uint regionId = 0,
        uint battlegroupId = 0,
        ulong dosResponse = AscensionDosResponse)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("Session key must be 40 bytes.", nameof(sessionKey));
        }

        // Live wire uses the account string exactly as typed (email lower-case) — NOT strupr.
        var acct = account;
        var build = clientBuild > 0 ? (uint)clientBuild : AscensionWireBuild;
        var digest = BuildAuthDigest(acct, clientSeed, serverSeed, sessionKey);
        var addonInfo = BuildAddonInfo(authProofTail);

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(build);
            bw.Write(loginServerId);
            WriteCString(bw, acct);
            bw.Write(loginServerType);
            bw.Write(clientSeed);
            bw.Write(regionId);
            bw.Write(battlegroupId);
            bw.Write((uint)realm.Id);
            bw.Write(dosResponse);
            bw.Write(digest);
            bw.Write(addonInfo);
        }

        return new Packet(Opcodes.CmsgAuthSession, ms.ToArray());
    }

    /// <summary>
    /// SHA1(account || uint32(0) || clientSeed || serverSeed || sessionKey[40]).
    /// Account casing matches the wire account string (live capture: lower-case email).
    /// </summary>
    private static byte[] BuildAuthDigest(string account, uint clientSeed, uint serverSeed, byte[] sessionKey)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(account));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes(clientSeed));
        ms.Write(BitConverter.GetBytes(serverSeed));
        ms.Write(sessionKey);
        return SHA1.HashData(ms.ToArray());
    }

    private static byte[] BuildAddonInfo(ReadOnlySpan<byte> authProofTail)
    {
        _ = authProofTail;
        lock (AddonLock)
        {
            if (_cachedAddonInfo is not null)
            {
                return _cachedAddonInfo;
            }

            foreach (var path in AddonCandidatePaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 8)
                {
                    _cachedAddonInfo = bytes;
                    return bytes;
                }
            }

            // Fallback: empty addon list (rejected by Ascension world — prefer captured blob).
            _cachedAddonInfo = Array.Empty<byte>();
            return _cachedAddonInfo;
        }
    }

    private static IEnumerable<string> AddonCandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "data", "ascension-addon-info.bin");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "ascension-addon-info.bin");
        yield return @"c:\Users\Dean\gamet\HeadlessClient\data\ascension-addon-info.bin";
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }
}
