using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Security;

namespace SteamStat.Core.Tests.Security;

[TestFixture]
public sealed class ChunkedEncryptionTests
{
    private static readonly EncryptionKeySource Passphrase = new EncryptionKeySource.Passphrase("correct horse battery staple", 100_000);

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(1000)]
    public async Task RoundTripsAcrossChunkBoundaries(int length)
    {
        var plaintext = RandomNumberGenerator.GetBytes(length);
        var key = RandomNumberGenerator.GetBytes(32);

        var encrypted = await EncryptAsync(plaintext, new EncryptionKeySource.RawKey(key), chunkSize: 64);
        ChunkedEncryption.HasMagic(encrypted).Should().BeTrue();

        var decrypted = await ChunkedEncryption.DecryptBytesAsync(encrypted, null, key);
        decrypted.Should().Equal(plaintext);
    }

    [Test]
    public async Task PassphraseRoundTripAndWrongPassphraseFails()
    {
        var plaintext = "档案馆 archive"u8.ToArray();
        var encrypted = await ChunkedEncryption.EncryptBytesAsync(plaintext, Passphrase);

        (await ChunkedEncryption.DecryptBytesAsync(encrypted, "correct horse battery staple")).Should().Equal(plaintext);

        var wrong = () => ChunkedEncryption.DecryptBytesAsync(encrypted, "wrong");
        await wrong.Should().ThrowAsync<CryptographicException>();
        var missing = () => ChunkedEncryption.DecryptBytesAsync(encrypted, null);
        await missing.Should().ThrowAsync<CryptographicException>();
    }

    [Test]
    public async Task EncryptingTwiceProducesDifferentCiphertexts()
    {
        var plaintext = RandomNumberGenerator.GetBytes(100);
        var key = RandomNumberGenerator.GetBytes(32);
        var first = await ChunkedEncryption.EncryptBytesAsync(plaintext, new EncryptionKeySource.RawKey(key));
        var second = await ChunkedEncryption.EncryptBytesAsync(plaintext, new EncryptionKeySource.RawKey(key));
        first.Should().NotEqual(second);
    }

    [Test]
    public async Task DetectsTruncationTamperingAndTrailingData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(200), new EncryptionKeySource.RawKey(key), chunkSize: 64);

        // 去掉最后一块：缺少 final 标记
        var truncated = encrypted[..^(4 + 8 + 16)];
        await Decrypting(truncated, key).Should().ThrowAsync<CryptographicException>();

        // 翻转一个密文字节
        var tampered = encrypted.ToArray();
        tampered[50] ^= 0x01;
        await Decrypting(tampered, key).Should().ThrowAsync<CryptographicException>();

        // 篡改文件头（属于 AAD）
        var header = encrypted.ToArray();
        header[30] ^= 0x01;
        await Decrypting(header, key).Should().ThrowAsync<CryptographicException>();

        // 最后一块之后追加数据
        var trailing = encrypted.Concat(new byte[] { 0 }).ToArray();
        await Decrypting(trailing, key).Should().ThrowAsync<CryptographicException>();

        // 交换两个完整块
        const int headerSize = 38;
        const int block = 4 + 64 + 16;
        var swapped = encrypted.ToArray();
        encrypted.AsSpan(headerSize, block).CopyTo(swapped.AsSpan(headerSize + block, block));
        encrypted.AsSpan(headerSize + block, block).CopyTo(swapped.AsSpan(headerSize, block));
        await Decrypting(swapped, key).Should().ThrowAsync<CryptographicException>();
    }

    [Test]
    public async Task RejectsForeignDataAndWrongKeys()
    {
        var notEncrypted = () => ChunkedEncryption.DecryptBytesAsync("PK\u0003\u0004 not encrypted at all, long enough"u8.ToArray(), null);
        await notEncrypted.Should().ThrowAsync<InvalidDataException>();

        var encrypted = await ChunkedEncryption.EncryptBytesAsync(new byte[10], new EncryptionKeySource.RawKey(RandomNumberGenerator.GetBytes(32)));
        await Decrypting(encrypted, RandomNumberGenerator.GetBytes(32)).Should().ThrowAsync<CryptographicException>();
    }

    private static Func<Task> Decrypting(byte[] data, byte[] key) => () => ChunkedEncryption.DecryptBytesAsync(data, null, key);

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, EncryptionKeySource source, int chunkSize)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await ChunkedEncryption.EncryptAsync(input, output, source, chunkSize: chunkSize);
        return output.ToArray();
    }
}
