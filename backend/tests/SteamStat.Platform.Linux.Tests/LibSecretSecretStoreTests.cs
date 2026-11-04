using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Platform.Linux;

namespace SteamStat.Platform.Linux.Tests;

public sealed class LibSecretSecretStoreTests
{
    [Test]
    public void ProtectThenUnprotect_RoundTripsWithOneKeyringItem()
    {
        var keyring = new MemoryKeyring();
        var store = new LibSecretSecretStore(keyring, NullLogger<LibSecretSecretStore>.Instance);

        var first = store.Protect("refresh-token-value");
        var second = store.Protect("refresh-token-value");

        first.Should().StartWith("libsecret:v1:").And.NotContain("refresh-token-value");
        second.Should().NotBe(first, "every value gets its own random nonce");
        keyring.StoreCount.Should().Be(1);

        var restarted = new LibSecretSecretStore(keyring, NullLogger<LibSecretSecretStore>.Instance);
        restarted.Unprotect(first).Should().Be("refresh-token-value");
        restarted.IsProtected(first).Should().BeTrue();
    }

    [Test]
    public void Protect_WhenKeyringIsUnavailable_UsesSessionKeyThatIsNeverPersisted()
    {
        var store = new LibSecretSecretStore(new MemoryKeyring { Unavailable = true }, NullLogger<LibSecretSecretStore>.Instance);

        var sessionValue = store.Protect("secret");

        sessionValue.Should().StartWith("session:v1:").And.NotContain("secret");
        store.IsProtected(sessionValue).Should().BeFalse("callers refuse to persist values that are not durably protected");
        store.Unprotect(sessionValue).Should().Be("secret");
        store.Protect(sessionValue).Should().Be(sessionValue);
        store.CanPersist.Should().BeFalse();
        new LibSecretSecretStore(new MemoryKeyring { Unavailable = true }, NullLogger<LibSecretSecretStore>.Instance)
            .Unprotect(sessionValue).Should().BeNull("the session key dies with the process");
    }

    [Test]
    public void CanPersist_WhenKeyringIsReachable_IsTrueWithoutCreatingAKey()
    {
        var keyring = new MemoryKeyring();
        new LibSecretSecretStore(keyring, NullLogger<LibSecretSecretStore>.Instance).CanPersist.Should().BeTrue();
        keyring.StoreCount.Should().Be(0);
    }

    [Test]
    public void Unprotect_WithDifferentKeyOrTamperedPayload_ReturnsNull()
    {
        var store = new LibSecretSecretStore(new MemoryKeyring(), NullLogger<LibSecretSecretStore>.Instance);
        var protectedValue = store.Protect("secret")!;

        new LibSecretSecretStore(new MemoryKeyring(), NullLogger<LibSecretSecretStore>.Instance)
            .Unprotect(protectedValue).Should().BeNull();

        var payload = Convert.FromBase64String(protectedValue["libsecret:v1:".Length..]);
        payload[^1] ^= 0xFF;
        store.Unprotect("libsecret:v1:" + Convert.ToBase64String(payload)).Should().BeNull();
    }

    [Test]
    public void Unprotect_PassesThroughValuesItDidNotProtect()
    {
        var store = new LibSecretSecretStore(new MemoryKeyring(), NullLogger<LibSecretSecretStore>.Instance);
        store.Unprotect("plain").Should().Be("plain");
        store.Unprotect("dpapi:v1:abc").Should().Be("dpapi:v1:abc");
    }

    private sealed class MemoryKeyring : ICredentialKeyring
    {
        private byte[]? _key;
        public bool Unavailable { get; init; }
        public int StoreCount { get; private set; }

        public byte[]? Load() => Unavailable ? throw new KeyringUnavailableException("no secret service") : _key;

        public void Store(byte[] key)
        {
            if (Unavailable) throw new KeyringUnavailableException("no secret service");
            _key = key;
            StoreCount++;
        }
    }
}
