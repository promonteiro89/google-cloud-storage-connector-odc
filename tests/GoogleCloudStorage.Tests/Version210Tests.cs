using System.Net;
using System.Text;
using System.Text.Json;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using BinaryContent = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.BinaryContent;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using MetadataEntry = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.MetadataEntry;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// 2.1.0: parity with the O11 connector's 2.0.0 (its tests/Version2Tests.cs U1-U8, B1-B3, L1, A1-A3, S1-S3).
/// Expected values and messages come from the O11 2.0.0 spec. The upload tests run on both upload actions:
/// Object_UploadBinary (in-memory bytes) and Object_Upload (an ODC File).
/// </summary>
public sealed class Version210Tests : IDisposable
{
    private const string AlreadyExists = "Object 'docs/a.txt' already exists in bucket 'my-bucket'. OnlyIfNotExists is True, so the existing object was left unchanged.";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly FileTransferTests.FakeFileStorage _files = new();
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan[] _savedDelays;

    public Version210Tests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        _savedDelays = Connector.TransferRetryDelays;
        Connector.EmulatorBaseUri = null;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        FileStorage.Configure(() => _files);
    }

    public void Dispose()
    {
        FileStorage.Reset();
        Connector.HttpClientFactoryOverride = null;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.TransferRetryDelays = _savedDelays;
    }

    private static Authentication Federated() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        SubjectToken = "header.payload.signature"
    };

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("hello parity");
    private const string Key = "my-bucket/docs/a.txt";

    public enum Variant { Binary, File }

    /// <summary>Uploads <paramref name="data"/> to my-bucket/docs/a.txt with the given variant and returns the Generation output.</summary>
    private long Upload(Variant variant, byte[] data, bool onlyIfNotExists, string disposition = "", string cacheControl = "", string encoding = "")
    {
        long generation;
        if (variant == Variant.Binary)
            _sut.Object_UploadBinary(Federated(), "my-bucket", "docs/a.txt", new BinaryContent { Content = data, ContentType = "text/plain" },
                Array.Empty<MetadataEntry>(), out generation, onlyIfNotExists, disposition, cacheControl, encoding);
        else
            _sut.Object_Upload(Federated(), "my-bucket", "docs/a.txt", _files.Add(data, "text/plain"), "", Array.Empty<MetadataEntry>(),
                out generation, onlyIfNotExists, disposition, cacheControl, encoding);
        return generation;
    }

    private IEnumerable<FakeFederationBackend.Recorded> SessionStarts =>
        _backend.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal));

    private IEnumerable<FakeFederationBackend.Recorded> MetadataGets =>
        _backend.Requests.Where(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath == "/storage/v1/b/my-bucket/o/docs%2Fa.txt" && !r.Uri.Query.Contains("alt=media"));

    private static string? Query(FakeFederationBackend.Recorded r, string name) => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)[name];

    private void Existing(byte[] data, long generation = 42, DateTime? created = null)
    {
        _backend.StoredObjects[Key] = data;
        _backend.Objects[Key] = new FakeFederationBackend.FakeObject(generation, 1, "text/plain", data.Length,
            Md5Hash: Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(data)), TimeCreated: created ?? DateTime.UtcNow);
    }

    /// <summary>The first session start stores <paramref name="data"/> itself (as a lost-response send would) and answers 503.</summary>
    private void FirstStartStoresThenFails(byte[] data, DateTime created, long generation = 77)
    {
        int starts = 0;
        _backend.StorageResponseOverride = request =>
        {
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref starts) != 1) return null;
            lock (_backend.Objects) Existing(data, generation, created);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":503,\"message\":\"Backend Error\"}}", Encoding.UTF8, "application/json") };
        };
    }

    // ---- U: Object_Upload / Object_UploadBinary -------------------------------------------

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U1_only_if_not_exists_sends_if_generation_match_0_and_returns_the_generation(Variant variant)
    {
        long generation = Upload(variant, Content, onlyIfNotExists: true);

        Assert.Equal("0", Query(Assert.Single(SessionStarts), "ifGenerationMatch"));
        Assert.Equal(Content, _backend.StoredObjects[Key]);
        Assert.True(generation > 0);
        Assert.Equal(_backend.Objects[Key].Generation, generation);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U2_only_if_not_exists_on_an_existing_object_fails_and_leaves_it_unchanged(Variant variant)
    {
        var original = Encoding.ASCII.GetBytes("original data");
        Existing(original);

        var ex = Assert.Throws<Exception>(() => Upload(variant, Content, onlyIfNotExists: true));

        Assert.Equal(AlreadyExists, ex.Message);
        Assert.Equal(original, _backend.StoredObjects[Key]);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U3_without_only_if_not_exists_the_upload_overwrites(Variant variant)
    {
        Existing(Encoding.ASCII.GetBytes("original data"));

        Upload(variant, Content, onlyIfNotExists: false);

        Assert.Null(Query(Assert.Single(SessionStarts), "ifGenerationMatch"));
        Assert.Equal(Content, _backend.StoredObjects[Key]);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U4_a_412_after_a_repeat_adopts_the_object_this_call_stored(Variant variant)
    {
        FirstStartStoresThenFails(Content, DateTime.UtcNow, generation: 77);

        long generation = Upload(variant, Content, onlyIfNotExists: true);

        Assert.Equal(77, generation);
        Assert.Equal(2, SessionStarts.Count());
        Assert.Single(MetadataGets);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U5_a_412_after_a_repeat_with_different_bytes_of_the_same_length_is_already_exists(Variant variant)
    {
        // Same length, different bytes: only the MD5 tells them apart, so the MD5 check is what is tested.
        var other = Encoding.ASCII.GetBytes("HELLO PARITY");
        Assert.Equal(Content.Length, other.Length);
        FirstStartStoresThenFails(other, DateTime.UtcNow);

        var ex = Assert.Throws<Exception>(() => Upload(variant, Content, onlyIfNotExists: true));

        Assert.Equal(AlreadyExists, ex.Message);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U6_a_412_after_a_repeat_for_an_object_created_before_the_call_is_already_exists(Variant variant)
    {
        FirstStartStoresThenFails(Content, DateTime.UtcNow.AddHours(-2));

        var ex = Assert.Throws<Exception>(() => Upload(variant, Content, onlyIfNotExists: true));

        Assert.Equal(AlreadyExists, ex.Message);
    }

    [Theory, InlineData(Variant.Binary), InlineData(Variant.File)]
    public void U7_content_headers_are_sent_with_the_upload(Variant variant)
    {
        Upload(variant, Content, onlyIfNotExists: false, disposition: "attachment; filename=\"a.pdf\"", cacheControl: "no-store", encoding: "gzip");

        var body = JsonDocument.Parse(Assert.Single(SessionStarts).Body).RootElement;
        Assert.Equal("attachment; filename=\"a.pdf\"", body.GetProperty("contentDisposition").GetString());
        Assert.Equal("no-store", body.GetProperty("cacheControl").GetString());
        Assert.Equal("gzip", body.GetProperty("contentEncoding").GetString());
    }

    [Theory]
    [InlineData(Variant.Binary, "", "no-cache\r\nX-Evil: 1", "", "CacheControl must not contain line breaks.")]
    [InlineData(Variant.File, "", "no-cache\r\nX-Evil: 1", "", "CacheControl must not contain line breaks.")]
    [InlineData(Variant.Binary, "attachment; filename=\"relatório.pdf\"", "", "", "ContentDisposition must contain only printable ASCII characters (no control characters, accented letters or other non-ASCII characters).")]
    [InlineData(Variant.File, "", "", "gzíp", "ContentEncoding must contain only printable ASCII characters (no control characters, accented letters or other non-ASCII characters).")]
    public void U8_invalid_header_values_are_rejected_before_any_request(Variant variant, string disposition, string cacheControl, string encoding, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => Upload(variant, Content, onlyIfNotExists: false, disposition, cacheControl, encoding));

        Assert.Equal(expected, ex.Message);
        Assert.Empty(_backend.Requests);
    }

    // ---- B: Bucket_Create -------------------------------------------------------------

    private JsonElement CapturedBucketCreate(Action create)
    {
        _backend.StorageResponseOverride = request =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/storage/v1/b") return null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { kind = "storage#bucket", name = "new-bucket" }), Encoding.UTF8, "application/json")
            };
        };
        create();
        // The fake records the body already decompressed (Google's client gzips JSON request bodies).
        var recorded = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/storage/v1/b");
        return JsonDocument.Parse(recorded.Body).RootElement;
    }

    [Fact]
    public void B1_bucket_create_is_secure_by_default()
    {
        var body = CapturedBucketCreate(() => _sut.Bucket_Create(Federated(), "new-bucket", ""));

        var iam = body.GetProperty("iamConfiguration");
        Assert.True(iam.GetProperty("uniformBucketLevelAccess").GetProperty("enabled").GetBoolean());
        Assert.Equal("enforced", iam.GetProperty("publicAccessPrevention").GetString());
        Assert.False(body.TryGetProperty("storageClass", out _));
    }

    [Fact]
    public void B2_bucket_create_options_turn_the_defaults_off_and_set_the_storage_class()
    {
        var body = CapturedBucketCreate(() => _sut.Bucket_Create(Federated(), "new-bucket", "", "nearline", true, true));

        Assert.Equal("NEARLINE", body.GetProperty("storageClass").GetString());
        var iam = body.GetProperty("iamConfiguration");
        Assert.False(iam.GetProperty("uniformBucketLevelAccess").GetProperty("enabled").GetBoolean());
        Assert.Equal("inherited", iam.GetProperty("publicAccessPrevention").GetString());
    }

    [Fact]
    public void B3_an_unknown_storage_class_is_rejected_before_any_request()
    {
        var ex = Assert.Throws<ArgumentException>(() => _sut.Bucket_Create(Federated(), "new-bucket", "", "GLACIER"));

        Assert.Equal("Invalid StorageClass 'GLACIER'. Use STANDARD, NEARLINE, COLDLINE or ARCHIVE, or leave it empty for STANDARD.", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    // ---- L: Object_List ---------------------------------------------------------------

    [Fact]
    public void L1_listed_objects_carry_generation_md5_and_crc32c()
    {
        _backend.Objects["my-bucket/a.txt"] = new FakeFederationBackend.FakeObject(1700000000000001, 1, Md5Hash: "XrY7u+Ae7tCTyyK7j1rNww==", Crc32c: "yZRlqg==");
        _backend.Objects["my-bucket/b.txt"] = new FakeFederationBackend.FakeObject(5, 1);

        _sut.Object_List(Federated(), "my-bucket", "", 0, "", "", out var objects, out _, out _);

        var list = objects.ToList();
        var a = list.Single(o => o.Name == "a.txt");
        Assert.Equal(1700000000000001, a.Generation);
        Assert.Equal("XrY7u+Ae7tCTyyK7j1rNww==", a.MD5Hash);
        Assert.Equal("yZRlqg==", a.Crc32c);
        var b = list.Single(o => o.Name == "b.txt");
        Assert.Equal("", b.MD5Hash);
        Assert.Equal(5, b.Generation);
    }

    // ---- A: AuthenticationMethod is required --------------------------------------------

    [Theory]
    [InlineData("", "AuthenticationMethod is required. Set it to 'WorkloadIdentityFederation' or 'ServiceAccountKey'.")]
    [InlineData("   ", "AuthenticationMethod is required. Set it to 'WorkloadIdentityFederation' or 'ServiceAccountKey'.")]
    [InlineData("Key", "Invalid AuthenticationMethod 'Key'. Use 'WorkloadIdentityFederation' or 'ServiceAccountKey'.")]
    public void A1_A3_authentication_method_is_required_and_checked_before_any_request(string method, string expected)
    {
        var auth = TestSupport.Auth();
        auth.AuthenticationMethod = method;

        var ex = Assert.Throws<ArgumentException>(() => _sut.Object_Exists(auth, "my-bucket", "a.txt", out _));

        Assert.Equal(expected, ex.Message);
        Assert.Empty(_backend.Requests);
    }

    // ---- S: signed URLs with Workload Identity Federation -----------------------------

    [Fact]
    public void S1_federation_signed_urls_are_limited_to_720_minutes_before_any_request()
    {
        var ex = Assert.Throws<ArgumentException>(() => _sut.Object_GetSignedUrl(Federated(), "my-bucket", "a.txt", 721, out _, "Download"));

        Assert.Equal("ExpirationMinutes cannot exceed 720 minutes (12 hours) with Workload Identity Federation: Google signs these URLs with a key it rotates, so a longer-lived URL could stop working before it expires (received 721).", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void S2_federation_signed_url_of_720_minutes_is_signed()
    {
        _sut.Object_GetSignedUrl(Federated(), "my-bucket", "a.txt", 720, out var url, "Download");

        Assert.Contains("X-Goog-Expires=43200", url);
    }

    [Fact]
    public void S3_key_signed_url_keeps_the_7_day_limit()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "my-bucket", "a.txt", 10080, out var url, "Download");

        Assert.Contains("X-Goog-Expires=604800", url);
    }
}
