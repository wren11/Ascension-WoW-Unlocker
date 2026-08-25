using System.Security.Cryptography;

namespace HeadlessClient.Infrastructure.Crypto;

public sealed class WowCrypt
{
    private static readonly byte[] ServerEncryptionKey =
    [
        0x08, 0xF1, 0x95, 0x9F, 0x47, 0xE5, 0xD2, 0xDB,
        0xA1, 0x3D, 0x77, 0x8F, 0x3F, 0x3E, 0xE7, 0x00
    ];

    private static readonly byte[] ServerDecryptionKey =
    [
        0x40, 0xAA, 0xD3, 0x92, 0x26, 0x71, 0x43, 0x47,
        0x3A, 0x31, 0x08, 0xA6, 0xE7, 0xDC, 0x98, 0x2A
    ];

    private readonly Arc4 _send = new();
    private readonly Arc4 _recv = new();
    private bool _initialized;

    public bool IsInitialized => _initialized;

    public void Reset() => _initialized = false;

    public void Init(ReadOnlySpan<byte> sessionKey)
    {
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("WotLK session key must be 40 bytes.", nameof(sessionKey));
        }

        var sendKey = HmacSha1(ServerDecryptionKey, sessionKey);
        var recvKey = HmacSha1(ServerEncryptionKey, sessionKey);

        _send.Init(sendKey);
        _recv.Init(recvKey);

        Span<byte> drop = stackalloc byte[1024];
        drop.Clear();
        _send.Process(drop);
        drop.Clear();
        _recv.Process(drop);

        _initialized = true;
    }

    public void EncryptSendHeader(Span<byte> header)
    {
        EnsureInitialized();
        if (header.IsEmpty || header.Length > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "Send header must be 1-6 bytes.");
        }

        _send.Process(header);
    }

    public void DecryptRecvHeader(Span<byte> header)
    {
        EnsureInitialized();
        if (header.IsEmpty || header.Length > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "Recv header must be 1-6 bytes.");
        }

        _recv.Process(header);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("WowCrypt has not been initialized.");
        }
    }

    private static byte[] HmacSha1(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA1(key);
        return hmac.ComputeHash(data.ToArray());
    }
}
