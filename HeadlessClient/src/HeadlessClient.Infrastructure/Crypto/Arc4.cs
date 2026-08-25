namespace HeadlessClient.Infrastructure.Crypto;

public sealed class Arc4
{
    private readonly byte[] _s = new byte[256];
    private int _i;
    private int _j;
    private bool _initialized;

    public void Init(ReadOnlySpan<byte> key)
    {
        if (key.Length == 0)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }

        for (var i = 0; i < 256; i++)
        {
            _s[i] = (byte)i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + _s[i] + key[i % key.Length]) & 0xFF;
            (_s[i], _s[j]) = (_s[j], _s[i]);
        }

        _i = 0;
        _j = 0;
        _initialized = true;
    }

    public void Process(Span<byte> buffer)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Arc4 has not been initialized.");
        }

        for (var n = 0; n < buffer.Length; n++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            var k = _s[(_s[_i] + _s[_j]) & 0xFF];
            buffer[n] ^= k;
        }
    }

    public void Process(byte[] buffer) => Process(buffer.AsSpan());
}
