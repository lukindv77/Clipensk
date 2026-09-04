using System.Security.Cryptography;
using System.Threading;

namespace Clipensk.Core.Security;

public sealed class MasterKeyLease : IDisposable
{
    private byte[]? _key;

    public MasterKeyLease(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("MasterKey не может быть пустым.", nameof(key));
        }

        // Объект принимает владение массивом и гарантированно очищает его при Dispose.
        _key = key;
    }

    public int Length => GetKey().Length;

    public ReadOnlyMemory<byte> DangerousGetMemory() => GetKey();

    public void Dispose()
    {
        byte[]? key = Interlocked.Exchange(ref _key, null);
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetKey()
    {
        return Volatile.Read(ref _key)
            ?? throw new ObjectDisposedException(nameof(MasterKeyLease));
    }
}
