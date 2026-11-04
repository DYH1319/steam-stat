using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SteamStat.Core.Sync;

namespace SteamStat.Core.Tests.Sync;

[TestFixture]
public sealed class SyncStorageTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "steam-stat-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void Keys_RejectTraversalAndOddCharacters()
    {
        SyncKeys.IsValid("devices/abc/seg-000000000001.ssec").Should().BeTrue();
        SyncKeys.IsValid("../etc/passwd").Should().BeFalse();
        SyncKeys.IsValid("devices//x").Should().BeFalse();
        SyncKeys.IsValid("/abs").Should().BeFalse();
        SyncKeys.IsValid("a b").Should().BeFalse();
        SyncKeys.IsValid("devices/./x").Should().BeFalse();
        SyncKeys.NormalizeRoot(" /steam-stat-sync/ ").Should().Be("steam-stat-sync");
        var invalid = () => SyncKeys.NormalizeRoot("../up");
        invalid.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task LocalFolder_ListsReadsWritesAndDeletes()
    {
        var storage = new LocalFolderSyncStorage(_root, "steam-stat-sync");

        (await storage.ListAsync("devices/")).Should().BeEmpty();
        (await storage.GetAsync("vault.json")).Should().BeNull();

        await storage.PutAsync("vault.json", "{}"u8.ToArray());
        await storage.PutAsync("devices/a/seg-000000000001.ssec", new byte[] { 1, 2, 3 });
        await storage.PutAsync("devices/b/seg-000000000002.ssec", new byte[] { 4 });
        // 网盘客户端可能留下的临时文件不会被列出
        await File.WriteAllTextAsync(Path.Combine(_root, "steam-stat-sync", "devices", "a", "x.ssec.123.partial"), "partial");

        (await storage.ListAsync("devices/")).Select(item => (item.Key, item.Size)).Should().Equal(
            ("devices/a/seg-000000000001.ssec", 3L), ("devices/b/seg-000000000002.ssec", 1L));
        (await storage.ListAsync("")).Should().HaveCount(3);
        (await storage.GetAsync("devices/a/seg-000000000001.ssec")).Should().Equal(1, 2, 3);

        await storage.PutAsync("devices/a/seg-000000000001.ssec", new byte[] { 9 });
        (await storage.GetAsync("devices/a/seg-000000000001.ssec")).Should().Equal(9);
        await storage.DeleteAsync("devices/a/seg-000000000001.ssec");
        await storage.DeleteAsync("devices/a/seg-000000000001.ssec");
        (await storage.ListAsync("devices/a/")).Should().BeEmpty();
    }

    [Test]
    public async Task LocalFolder_RequiresTheChosenFolderToExist()
    {
        var storage = new LocalFolderSyncStorage(Path.Combine(_root, "unplugged-usb"));
        var act = () => storage.PutAsync("vault.json", new byte[] { 1 });
        (await act.Should().ThrowAsync<SyncStorageException>()).Which.Kind.Should().Be(SyncStorageErrorKind.Configuration);
    }

    [Test]
    public async Task WebDav_ParsesListingsCreatesCollectionsAndSendsBasicAuth()
    {
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return (request.Method.Method, path) switch
            {
                ("PROPFIND", "/dav/steam-stat-sync/devices/") => Xml("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <d:multistatus xmlns:d="DAV:">
                      <d:response><d:href>/dav/steam-stat-sync/devices/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
                      <d:response><d:href>https://dav.example.com/dav/steam-stat-sync/devices/dev1/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
                    </d:multistatus>
                    """),
                ("PROPFIND", "/dav/steam-stat-sync/devices/dev1/") => Xml("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <D:multistatus xmlns:D="DAV:">
                      <D:response><D:href>/dav/steam-stat-sync/devices/dev1/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop></D:propstat></D:response>
                      <D:response><D:href>/dav/steam-stat-sync/devices/dev1/seg-000000000001.ssec</D:href><D:propstat><D:prop><D:resourcetype/><D:getcontentlength>42</D:getcontentlength></D:prop></D:propstat></D:response>
                      <D:response><D:href>/dav/steam-stat-sync/devices/dev1/info.ssec</D:href><D:propstat><D:prop><D:resourcetype/><D:getcontentlength>7</D:getcontentlength></D:prop></D:propstat></D:response>
                    </D:multistatus>
                    """),
                ("MKCOL", _) => new HttpResponseMessage(path == "/dav/steam-stat-sync/" ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.Created),
                ("PUT", "/dav/steam-stat-sync/devices/dev1/seg-000000000002.ssec") => new HttpResponseMessage(HttpStatusCode.Created),
                ("GET", "/dav/steam-stat-sync/vault.json") => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });
        var storage = new WebDavSyncStorage(new HttpClient(handler), "https://dav.example.com/dav", "user", "p@ss", "steam-stat-sync");

        (await storage.ListAsync("devices/")).Select(item => (item.Key, item.Size)).Should().Equal(
            ("devices/dev1/info.ssec", 7L), ("devices/dev1/seg-000000000001.ssec", 42L));
        (await storage.GetAsync("vault.json")).Should().BeNull();
        await storage.PutAsync("devices/dev1/seg-000000000002.ssec", new byte[] { 1 });
        await storage.PutAsync("devices/dev1/seg-000000000002.ssec", new byte[] { 1 });

        handler.Requests.Where(request => request.Method == "MKCOL").Select(request => request.Path).Should().Equal(
            "/dav/steam-stat-sync/", "/dav/steam-stat-sync/devices/", "/dav/steam-stat-sync/devices/dev1/");
        handler.Requests.Should().OnlyContain(request => request.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss")));
        handler.Requests.Single(request => request.Method == "PROPFIND" && request.Path.EndsWith("devices/")).Depth.Should().Be("1");
    }

    [Test]
    public async Task WebDav_MapsAuthenticationFailures()
    {
        var storage = new WebDavSyncStorage(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), "https://dav.example.com/", "u", "bad");
        var act = () => storage.GetAsync("vault.json");
        (await act.Should().ThrowAsync<SyncStorageException>()).Which.Kind.Should().Be(SyncStorageErrorKind.Unauthorized);
    }

    [Test]
    public void S3Signer_MatchesAwsDocumentedGetObjectExample()
    {
        // AWS「Signature Calculations for the Authorization Header」文档中的 GET Object 示例
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z"
        };
        var authorization = S3Signer.Authorization("GET", "/test.txt", "", headers,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), "us-east-1",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

        authorization.Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }

    [Test]
    public void S3Signer_MatchesAwsDocumentedListObjectsExample()
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z"
        };
        var query = S3Signer.CanonicalQuery(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["prefix"] = "J", ["max-keys"] = "2" });
        query.Should().Be("max-keys=2&prefix=J");
        var authorization = S3Signer.Authorization("GET", "/", query, headers,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), "us-east-1",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

        authorization.Should().EndWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
    }

    [Test]
    public async Task S3_ListsWithPaginationAndStripsTheRemoteRoot()
    {
        var handler = new FakeHandler(request =>
        {
            var query = request.RequestUri!.Query;
            if (request.Method == HttpMethod.Get && query.Contains("list-type=2"))
            {
                return query.Contains("continuation-token=")
                    ? Xml("""<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated><Contents><Key>root/devices/b/seg-000000000001.ssec</Key><Size>5</Size></Contents></ListBucketResult>""")
                    : Xml("""<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>true</IsTruncated><NextContinuationToken>t+1/=</NextContinuationToken><Contents><Key>root/devices/a/seg-000000000001.ssec</Key><Size>3</Size></Contents></ListBucketResult>""");
            }
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var storage = new S3SyncStorage(new HttpClient(handler), new S3SyncOptions
        {
            Endpoint = "https://minio.example.com:9000", Region = "", Bucket = "archive",
            AccessKeyId = "AK", SecretAccessKey = "SK", PathStyle = true, RemoteRoot = "root"
        }, () => new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));

        (await storage.ListAsync("devices/")).Select(item => item.Key).Should().Equal(
            "devices/a/seg-000000000001.ssec", "devices/b/seg-000000000001.ssec");
        (await storage.GetAsync("vault.json")).Should().BeNull();
        await storage.PutAsync("vault.json", "{}"u8.ToArray());

        var list = handler.Requests[0];
        list.Path.Should().Be("/archive/");
        list.Query.Should().Be("?list-type=2&prefix=root%2Fdevices%2F");
        handler.Requests[1].Query.Should().Contain("continuation-token=t%2B1%2F%3D");
        var put = handler.Requests.Last();
        put.Path.Should().Be("/archive/root/vault.json");
        put.Host.Should().Be("minio.example.com:9000");
        put.Authorization.Should().StartWith("AWS4-HMAC-SHA256 Credential=AK/20261104/us-east-1/s3/aws4_request");
        put.ContentSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData("{}"u8)));
    }

    [Test]
    public async Task S3_MapsClockSkewAndAccessErrors()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<Error><Code>RequestTimeTooSkewed</Code></Error>")
        });
        var storage = new S3SyncStorage(new HttpClient(handler), new S3SyncOptions
        {
            Endpoint = "https://s3.amazonaws.com", Region = "us-east-1", Bucket = "b", AccessKeyId = "AK", SecretAccessKey = "SK", PathStyle = false
        });
        var act = () => storage.GetAsync("vault.json");
        (await act.Should().ThrowAsync<SyncStorageException>()).Which.Kind.Should().Be(SyncStorageErrorKind.ClockSkew);
        handler.Requests.Single().Host.Should().Be("b.s3.amazonaws.com");
    }

    [Test]
    public async Task Vault_UnlocksWithThePassphraseOnly()
    {
        var (file, key) = await SyncVault.CreateAsync("同步口令 sync passphrase", 1767312000);
        var parsed = SyncVault.Parse(SyncVault.Serialize(file))!;
        parsed.VaultId.Should().StartWith("v_");

        (await SyncVault.UnlockAsync(parsed, "同步口令 sync passphrase")).Should().Equal(key);
        var wrong = () => SyncVault.UnlockAsync(parsed, "wrong");
        await wrong.Should().ThrowAsync<CryptographicException>();

        var rewrapped = await SyncVault.RewrapAsync(parsed, key, "new passphrase");
        rewrapped.VaultId.Should().Be(parsed.VaultId);
        (await SyncVault.UnlockAsync(rewrapped, "new passphrase")).Should().Equal(key);
        SyncVault.Parse("{\"format\":\"other\"}"u8.ToArray()).Should().BeNull();
    }

    [Test]
    public async Task Segment_RoundTripsAndParsesObjectKeys()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var segment = new SyncSegment
        {
            DeviceId = "dev1",
            Seq = 7,
            CreatedAt = 1,
            Changes =
            [
                new SyncChange
                {
                    Table = "use_app_record", Op = "upsert", Key = "abc", At = 5, Device = "dev1",
                    Row = new Dictionary<string, object?> { ["app_id"] = 570L, ["note"] = "你好", ["tags"] = null }
                },
                new SyncChange { Table = "achievement_target", Op = "delete", Key = "[\"alice\",570,\"WIN\"]", At = 6, Device = "dev1" }
            ]
        };

        var encoded = await SyncSegment.EncodeAsync(segment, key);
        var decoded = (await SyncSegment.DecodeAsync<SyncSegment>(encoded, key))!;
        decoded.Seq.Should().Be(7);
        decoded.Changes.Should().HaveCount(2);
        ((JsonElement)decoded.Changes[0].Row!["note"]!).GetString().Should().Be("你好");
        ((JsonElement)decoded.Changes[0].Row!["app_id"]!).GetInt64().Should().Be(570);
        decoded.Changes[1].Row.Should().BeNull();

        var wrongKey = () => SyncSegment.DecodeAsync<SyncSegment>(encoded, RandomNumberGenerator.GetBytes(32));
        await wrongKey.Should().ThrowAsync<CryptographicException>();

        SyncSegment.ObjectKey("dev1", 7).Should().Be("devices/dev1/seg-000000000007.ssec");
        SyncSegment.ParseObjectKey("devices/dev1/seg-000000000007.ssec").Should().Be(("dev1", 7L));
        SyncSegment.ParseObjectKey("devices/dev1/info.ssec").Should().BeNull();
        SyncSegment.ParseObjectKey("vault.json").Should().BeNull();
    }

    private static HttpResponseMessage Xml(string body)
        => new((HttpStatusCode)207) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private sealed record RecordedRequest(string Method, string Path, string Query, string? Host, string? Authorization, string? Depth, string? ContentSha256);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Headers.Host,
                request.Headers.Authorization?.ToString() ?? (request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null),
                request.Headers.TryGetValues("Depth", out var depth) ? depth.Single() : null,
                request.Headers.TryGetValues("x-amz-content-sha256", out var sha) ? sha.Single() : null));
            var response = respond(request);
            if (response.StatusCode == (HttpStatusCode)207 || response.IsSuccessStatusCode) response.StatusCode = response.StatusCode == (HttpStatusCode)207 ? (HttpStatusCode)207 : response.StatusCode;
            return Task.FromResult(response);
        }
    }
}
