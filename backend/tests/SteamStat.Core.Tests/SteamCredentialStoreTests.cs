using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamCredentialStoreTests
{
    [Test]
    public async Task SaveAsync_WhenProtectReturnsNull_DoesNotPersist()
    {
        var tokens = new RecordingTokenStore();
        var store = new SteamCredentialStore(
            tokens, new FailingSecretStore(), TimeProvider.System,
            NullLogger<SteamCredentialStore>.Instance);

        await store.SaveAsync(new SteamAuthenticationResult("alice", "access", "refresh", "guard"));

        tokens.Written.Should().BeNull();
    }

    [Test]
    public async Task SaveAsync_WhenProtectReturnsPlaintext_DoesNotPersist()
    {
        var tokens = new RecordingTokenStore();
        var store = new SteamCredentialStore(
            tokens, new PassThroughSecretStore(), TimeProvider.System,
            NullLogger<SteamCredentialStore>.Instance);

        await store.SaveAsync(new SteamAuthenticationResult("alice", "access", "refresh", "guard"));

        tokens.Written.Should().BeNull();
    }

    [Test]
    public async Task SaveAsync_WhenProtectionSucceeds_PersistsProtectedValues()
    {
        var tokens = new RecordingTokenStore();
        var store = new SteamCredentialStore(
            tokens, new PrefixSecretStore(), TimeProvider.System,
            NullLogger<SteamCredentialStore>.Instance);

        await store.SaveAsync(new SteamAuthenticationResult("alice", "access", "refresh", "guard"));

        tokens.Written.Should().NotBeNull();
        tokens.Written!.AccessToken.Should().Be("protected:access");
        tokens.Written.RefreshToken.Should().Be("protected:refresh");
        tokens.Written.GuardData.Should().Be("protected:guard");
    }

    private sealed class RecordingTokenStore : ISteamLoginTokenStore
    {
        public SteamLoginTokenWrite? Written { get; private set; }
        public SteamLoginTokenData? FindByAccountName(string accountName) => null;
        public Task<SteamLoginTokenData?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamLoginTokenData?>(null);
        public IReadOnlyList<SteamLoginTokenData> List() => [];
        public Task UpsertAsync(SteamLoginTokenWrite token, CancellationToken cancellationToken = default)
        {
            Written = token;
            return Task.CompletedTask;
        }
        public Task<int> EncryptLegacyAsync(ISecretStore secretStore, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<SteamLoginTokenData?> DeleteAsync(int id, CancellationToken cancellationToken = default)
            => Task.FromResult<SteamLoginTokenData?>(null);
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public string? Protect(string? plainText) => null;
        public string? Unprotect(string? protectedText) => protectedText;
        public bool IsProtected(string? value)
            => value?.StartsWith("protected:", StringComparison.Ordinal) == true;
    }

    private sealed class PassThroughSecretStore : ISecretStore
    {
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
        public bool IsProtected(string? value)
            => value?.StartsWith("protected:", StringComparison.Ordinal) == true;
    }

    private sealed class PrefixSecretStore : ISecretStore
    {
        public string? Protect(string? plainText)
            => plainText == null ? null : $"protected:{plainText}";
        public string? Unprotect(string? protectedText) => protectedText;
        public bool IsProtected(string? value)
            => value?.StartsWith("protected:", StringComparison.Ordinal) == true;
    }
}
