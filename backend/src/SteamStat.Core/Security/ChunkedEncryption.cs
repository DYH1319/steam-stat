using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SteamStat.Core.Security;

/// <summary>加密密钥来源：口令（PBKDF2 派生）或已有的 256 位原始密钥。</summary>
public abstract record EncryptionKeySource
{
    private EncryptionKeySource()
    {
    }

    public sealed record Passphrase(string Value, int Iterations = ChunkedEncryption.DefaultIterations) : EncryptionKeySource;

    public sealed record RawKey(ReadOnlyMemory<byte> Key) : EncryptionKeySource;
}

/// <summary>
/// 流式认证加密（AES-256-GCM，按块加密），供手动备份与多设备同步使用。
/// <para>
/// 文件头 38 字节：「SSEC」| 版本 1 | KDF（0 = 原始密钥，1 = PBKDF2-HMAC-SHA256）| 迭代次数（uint32 BE）|
/// 盐（16）| nonce 前缀（8，随机）| 块大小（uint32 BE）。
/// 每块：4 字节头（最高位 = 最后一块，其余位 = 明文长度）+ 密文 + 16 字节标签；
/// nonce = 前缀 ‖ 块序号（uint32 BE），AAD = 文件头 ‖ 块序号 ‖ 块头。
/// 块被截断、重排、替换或缺少最后一块时解密失败。
/// </para>
/// </summary>
public static class ChunkedEncryption
{
    public const int DefaultIterations = 600_000;
    public const int KeySize = 32;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;
    private const int DefaultChunkSize = 1024 * 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 8;
    private const int TagSize = 16;
    private const int HeaderSize = 38;
    private const byte Version = 1;
    private const byte KdfRaw = 0;
    private const byte KdfPbkdf2 = 1;
    private const uint FinalFlag = 0x8000_0000;

    private static ReadOnlySpan<byte> Magic => "SSEC"u8;

    /// <summary>流的前 4 个字节是否为本格式的魔数。</summary>
    public static bool HasMagic(ReadOnlySpan<byte> prefix) => prefix.Length >= 4 && prefix[..4].SequenceEqual(Magic);

    public static async Task EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        EncryptionKeySource keySource,
        CancellationToken cancellationToken = default,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxChunkSize);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        var salt = header.AsSpan(10, SaltSize);
        byte[] key;
        switch (keySource)
        {
            case EncryptionKeySource.Passphrase passphrase:
                if (passphrase.Iterations is < MinIterations or > MaxIterations)
                    throw new ArgumentOutOfRangeException(nameof(keySource), "Unsupported iteration count.");
                header[5] = KdfPbkdf2;
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), (uint)passphrase.Iterations);
                RandomNumberGenerator.Fill(salt);
                key = DeriveKey(passphrase.Value, salt, passphrase.Iterations);
                break;
            case EncryptionKeySource.RawKey raw:
                if (raw.Key.Length != KeySize) throw new ArgumentException("A 256-bit key is required.", nameof(keySource));
                header[5] = KdfRaw;
                key = raw.Key.ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(keySource));
        }
        RandomNumberGenerator.Fill(header.AsSpan(26, NoncePrefixSize));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(34, 4), (uint)chunkSize);

        try
        {
            await ciphertext.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            using var aes = new AesGcm(key, TagSize);
            var current = new byte[chunkSize];
            var next = new byte[chunkSize];
            var currentLength = await FillAsync(plaintext, current, cancellationToken).ConfigureAwait(false);
            var output = new byte[4 + chunkSize + TagSize];
            var nonce = new byte[12];
            var aad = new byte[HeaderSize + 8];
            header.CopyTo(aad, 0);
            for (uint counter = 0; ; counter++)
            {
                // 预读下一块，判断当前块是否为最后一块
                var nextLength = currentLength == chunkSize ? await FillAsync(plaintext, next, cancellationToken).ConfigureAwait(false) : 0;
                var final = nextLength == 0;
                var word = (uint)currentLength | (final ? FinalFlag : 0);

                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), word);
                header.AsSpan(26, NoncePrefixSize).CopyTo(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), counter);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(HeaderSize, 4), counter);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(HeaderSize + 4, 4), word);
                aes.Encrypt(
                    nonce,
                    current.AsSpan(0, currentLength),
                    output.AsSpan(4, currentLength),
                    output.AsSpan(4 + currentLength, TagSize),
                    aad);
                await ciphertext.WriteAsync(output.AsMemory(0, 4 + currentLength + TagSize), cancellationToken).ConfigureAwait(false);

                if (final) break;
                if (counter == uint.MaxValue) throw new InvalidOperationException("Input is too large.");
                (current, next) = (next, current);
                currentLength = nextLength;
            }
            await ciphertext.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// 解密。<paramref name="passphrase"/> 用于口令加密的文件，<paramref name="rawKey"/> 用于原始密钥加密的文件。
    /// 口令错误、密钥不符或内容被篡改时抛出 <see cref="CryptographicException"/>；格式不对时抛出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        string? passphrase,
        ReadOnlyMemory<byte> rawKey = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);

        var header = new byte[HeaderSize];
        if (await FillAsync(ciphertext, header, cancellationToken).ConfigureAwait(false) != HeaderSize || !HasMagic(header))
            throw new InvalidDataException("Not an encrypted Steam Stat file.");
        if (header[4] != Version) throw new InvalidDataException("Unsupported encryption format version.");
        var chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(34, 4));
        if (chunkSize is < 1 or > MaxChunkSize) throw new InvalidDataException("Invalid chunk size.");

        byte[] key;
        switch (header[5])
        {
            case KdfPbkdf2:
                var iterations = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6, 4));
                if (iterations is < MinIterations or > MaxIterations) throw new InvalidDataException("Invalid key derivation parameters.");
                if (passphrase == null) throw new CryptographicException("A passphrase is required.");
                key = DeriveKey(passphrase, header.AsSpan(10, SaltSize), (int)iterations);
                break;
            case KdfRaw:
                if (rawKey.Length != KeySize) throw new CryptographicException("A 256-bit key is required.");
                key = rawKey.ToArray();
                break;
            default:
                throw new InvalidDataException("Unsupported key derivation.");
        }

        try
        {
            using var aes = new AesGcm(key, TagSize);
            var wordBuffer = new byte[4];
            var buffer = new byte[chunkSize + TagSize];
            var output = new byte[chunkSize];
            var nonce = new byte[12];
            var aad = new byte[HeaderSize + 8];
            header.CopyTo(aad, 0);
            for (uint counter = 0; ; counter++)
            {
                if (await FillAsync(ciphertext, wordBuffer, cancellationToken).ConfigureAwait(false) != 4)
                    throw new CryptographicException("The encrypted data is truncated.");
                var word = BinaryPrimitives.ReadUInt32BigEndian(wordBuffer);
                var final = (word & FinalFlag) != 0;
                var length = (int)(word & ~FinalFlag);
                if (length > chunkSize || (!final && length != chunkSize)) throw new CryptographicException("Invalid chunk length.");
                if (await FillAsync(ciphertext, buffer.AsMemory(0, length + TagSize), cancellationToken).ConfigureAwait(false) != length + TagSize)
                    throw new CryptographicException("The encrypted data is truncated.");

                header.AsSpan(26, NoncePrefixSize).CopyTo(nonce);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8, 4), counter);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(HeaderSize, 4), counter);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(HeaderSize + 4, 4), word);
                aes.Decrypt(nonce, buffer.AsSpan(0, length), buffer.AsSpan(length, TagSize), output.AsSpan(0, length), aad);
                await plaintext.WriteAsync(output.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

                if (final) break;
                if (counter == uint.MaxValue) throw new CryptographicException("Too many chunks.");
            }
            // 最后一块之后不允许再有数据
            if (await ciphertext.ReadAsync(wordBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                throw new CryptographicException("Unexpected data after the final chunk.");
            await plaintext.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task<byte[]> EncryptBytesAsync(ReadOnlyMemory<byte> plaintext, EncryptionKeySource keySource, CancellationToken cancellationToken = default)
    {
        using var input = new MemoryStream(plaintext.ToArray(), writable: false);
        using var output = new MemoryStream();
        await EncryptAsync(input, output, keySource, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public static async Task<byte[]> DecryptBytesAsync(ReadOnlyMemory<byte> ciphertext, string? passphrase, ReadOnlyMemory<byte> rawKey = default, CancellationToken cancellationToken = default)
    {
        using var input = new MemoryStream(ciphertext.ToArray(), writable: false);
        using var output = new MemoryStream();
        await DecryptAsync(input, output, passphrase, rawKey, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>PBKDF2-HMAC-SHA256 派生 256 位密钥。口令按 NFC 规范化后取 UTF-8。</summary>
    public static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
