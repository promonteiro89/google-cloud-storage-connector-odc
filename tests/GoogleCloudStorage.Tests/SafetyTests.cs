using Google.Apis.Http;
using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using GcsFile = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline safety and resilience tests against <see cref="FakeFederationBackend"/>: up-front
/// bucket/object name guards, SDK client-side validation error formatting, generation preconditions
/// and retries on move/copy/delete-by-prefix, copy-404 source probing, metadata PATCH payload shape,
/// bounded cache sizing/expiry, and IOException / IdP message formatting.
/// </summary>
public sealed class SafetyTests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;

    public SafetyTests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        _previousFactory = Connector.HttpClientFactoryOverride;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = _previousFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
    }

    private static string UniqueServiceAccount() => $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com";

    private static Authentication ClientCredentials(string? serviceAccount = null) => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = serviceAccount ?? UniqueServiceAccount(),
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
        ClientId = "my-client",
        ClientSecret = "my-secret",
        Scope = "api://gcs-connector/.default"
    };

    private static Authentication SuppliedToken(string token, string? serviceAccount = null) => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = serviceAccount ?? UniqueServiceAccount(),
        SubjectToken = token
    };

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string MakeJwt(string payloadJson) =>
        Base64Url("{\"alg\":\"none\"}") + "." + Base64Url(payloadJson) + ".sig";

    private static HttpResponseMessage JsonError(HttpStatusCode status, string message) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { code = (int)status, message } }),
                Encoding.UTF8,
                "application/json")
        };

    // ---- G1: required bucket and object names rejected before any client/network -----

    [Fact]
    public void G1_Empty_bucket_and_whitespace_object_names_are_rejected_before_any_request()
    {
        var sampleFile = new GcsFile { Content = [1], ContentType = "text/plain" };
        var oneChange = new List<MetadataEntry> { new() { Key = "k", Value = "v" } };

        var cases = new (string ExpectedParam, Action<Authentication> Call)[]
        {
            // Empty bucket names
            ("BucketName", auth => _sut.Object_Upload(auth, "", "obj.txt", sampleFile, [])),
            ("BucketName", auth => _sut.Object_Download(auth, "", "obj.txt", out _)),
            ("BucketName", auth => _sut.Object_Exists(auth, "", "obj.txt", out _)),
            ("BucketName", auth => _sut.Object_GetMetadata(auth, "", "obj.txt", out _, out _, out _)),
            ("BucketName", auth => _sut.Object_Delete(auth, "", "obj.txt")),
            ("BucketName", auth => _sut.Object_UpdateMetadata(auth, "", "obj.txt", "", "", "", "", oneChange)),
            ("SourceBucketName", auth => _sut.Object_Copy(auth, "", "src.txt", "dst-bucket", "dst.txt")),
            ("DestinationBucketName", auth => _sut.Object_Copy(auth, "src-bucket", "src.txt", "", "dst.txt")),
            ("SourceBucketName", auth => _sut.Object_Move(auth, "", "src.txt", "dst-bucket", "dst.txt")),
            ("DestinationBucketName", auth => _sut.Object_Move(auth, "src-bucket", "src.txt", "", "dst.txt")),
            ("BucketName", auth => _sut.Object_List(auth, "", "", 0, "", "", out _, out _, out _)),
            ("BucketName", auth => _sut.Object_DeleteByPrefix(auth, "", "prefix/", out _)),
            ("BucketName", auth => _sut.Object_GetSignedUrl(auth, "", "obj.txt", 5, out _)),
            ("BucketName", auth => _sut.Bucket_Create(auth, "", "US")),
            ("BucketName", auth => _sut.Bucket_Delete(auth, "")),
            ("BucketName", auth => _sut.Bucket_Exists(auth, "", out _)),

            // Object actions with whitespace object name ("   ") and empty destination object on Move
            ("ObjectName", auth => _sut.Object_Upload(auth, "test-bucket-alpha", "   ", sampleFile, [])),
            ("ObjectName", auth => _sut.Object_Download(auth, "test-bucket-alpha", "   ", out _)),
            ("ObjectName", auth => _sut.Object_Exists(auth, "test-bucket-alpha", "   ", out _)),
            ("ObjectName", auth => _sut.Object_GetMetadata(auth, "test-bucket-alpha", "   ", out _, out _, out _)),
            ("ObjectName", auth => _sut.Object_Delete(auth, "test-bucket-alpha", "   ")),
            ("ObjectName", auth => _sut.Object_UpdateMetadata(auth, "test-bucket-alpha", "   ", "", "", "", "", oneChange)),
            ("SourceObjectName", auth => _sut.Object_Copy(auth, "src-bucket", "   ", "dst-bucket", "dst.txt")),
            ("DestinationObjectName", auth => _sut.Object_Copy(auth, "src-bucket", "src.txt", "dst-bucket", "   ")),
            ("SourceObjectName", auth => _sut.Object_Move(auth, "src-bucket", "   ", "dst-bucket", "dst.txt")),
            ("DestinationObjectName", auth => _sut.Object_Move(auth, "src-bucket", "src.txt", "dst-bucket", "")),
            ("DestinationObjectName", auth => _sut.Object_Move(auth, "src-bucket", "src.txt", "dst-bucket", "   ")),
            ("ObjectName", auth => _sut.Object_GetSignedUrl(auth, "test-bucket-alpha", "   ", 5, out _)),
        };

        foreach (var (expectedParam, call) in cases)
        {
            _backend.Requests.Clear();
            var auth = ClientCredentials();
            var ex = Assert.Throws<ArgumentException>(() => call(auth));
            Assert.Contains(expectedParam, ex.Message);
            Assert.Empty(_backend.Requests);
        }
    }

    // ---- S0: client-side SDK validation (HttpStatusCode = 0) -------------------------

    [Fact]
    public void S0_TranslateException_formats_status_zero_parameter_validation_without_0_0()
    {
        var apiEx = new Google.GoogleApiException("storage", "Parameter validation failed for \"object\"")
        {
            HttpStatusCode = 0,
            Error = new Google.Apis.Requests.RequestError { Message = "Parameter validation failed for \"object\"" }
        };

        var translated = Connector.TranslateException(apiEx, ClientCredentials(), "test-bucket-alpha", "obj.txt");
        Assert.Contains("rejected before it was sent", translated.Message);
        Assert.DoesNotContain("(0 0)", translated.Message);
    }

    // ---- M1-M4: Object_Move preconditions and error handling -------------------------

    [Fact]
    public void M1_Move_sends_generation_precondition_on_rewrite_and_delete()
    {
        _backend.Objects["src-bucket/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);

        _sut.Object_Move(ClientCredentials(), "src-bucket", "docs/a.txt", "dst-bucket", "moved/a.txt");

        var rewrite = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal));
        Assert.Equal("42", HttpUtility.ParseQueryString(rewrite.Uri.Query)["ifSourceGenerationMatch"]);

        var delete = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal("42", HttpUtility.ParseQueryString(delete.Uri.Query)["ifGenerationMatch"]);
    }

    [Fact]
    public void M2_Move_rewrite_412_reports_source_changed_and_skips_delete()
    {
        _backend.Objects["src-bucket/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal)
                ? JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Move(ClientCredentials(), "src-bucket", "docs/a.txt", "dst-bucket", "moved/a.txt"));

        Assert.Contains("changed while it was being moved; nothing was moved", ex.Message);
        Assert.DoesNotContain(_backend.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void M3_Move_delete_412_reports_source_was_modified_during_move()
    {
        _backend.Objects["src-bucket/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Delete
                ? JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Move(ClientCredentials(), "src-bucket", "docs/a.txt", "dst-bucket", "moved/a.txt"));

        Assert.Contains("was modified during the move and was not deleted", ex.Message);
    }

    [Fact]
    public void M4_Move_delete_404_succeeds_without_exception()
    {
        _backend.Objects["src-bucket/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Delete
                ? JsonError(HttpStatusCode.NotFound, "No such object: src-bucket/docs/a.txt")
                : null;

        _sut.Object_Move(ClientCredentials(), "src-bucket", "docs/a.txt", "dst-bucket", "moved/a.txt");
    }

    // ---- D1-D3: Object_DeleteByPrefix preconditions and retries ----------------------

    [Fact]
    public void D1_DeleteByPrefix_sends_listed_generation_on_each_delete()
    {
        _backend.Objects["test-bucket-alpha/pfx/a"] = new FakeFederationBackend.FakeObject(Generation: 11, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/b"] = new FakeFederationBackend.FakeObject(Generation: 12, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/c"] = new FakeFederationBackend.FakeObject(Generation: 13, Metageneration: 1);

        _sut.Object_DeleteByPrefix(ClientCredentials(), "test-bucket-alpha", "pfx/", out var deletedCount);

        Assert.Equal(3, deletedCount);
        var deletes = _backend.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal(3, deletes.Count);
        Assert.Equal("11", HttpUtility.ParseQueryString(deletes[0].Uri.Query)["ifGenerationMatch"]);
        Assert.Equal("12", HttpUtility.ParseQueryString(deletes[1].Uri.Query)["ifGenerationMatch"]);
        Assert.Equal("13", HttpUtility.ParseQueryString(deletes[2].Uri.Query)["ifGenerationMatch"]);
    }

    [Fact]
    public void D2_DeleteByPrefix_skips_412_without_throwing_or_counting()
    {
        _backend.Objects["test-bucket-alpha/pfx/a"] = new FakeFederationBackend.FakeObject(Generation: 11, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/b"] = new FakeFederationBackend.FakeObject(Generation: 12, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/c"] = new FakeFederationBackend.FakeObject(Generation: 13, Metageneration: 1);

        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Delete && Uri.UnescapeDataString(req.RequestUri!.AbsolutePath).EndsWith("/o/pfx/b", StringComparison.Ordinal)
                ? JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed")
                : null;

        _sut.Object_DeleteByPrefix(ClientCredentials(), "test-bucket-alpha", "pfx/", out var deletedCount);

        Assert.Equal(2, deletedCount);
    }

    [Fact]
    public void D3_DeleteByPrefix_retries_503_on_delete_because_generation_precondition_is_set()
    {
        _backend.Objects["test-bucket-alpha/pfx/a"] = new FakeFederationBackend.FakeObject(Generation: 11, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/b"] = new FakeFederationBackend.FakeObject(Generation: 12, Metageneration: 1);
        _backend.Objects["test-bucket-alpha/pfx/c"] = new FakeFederationBackend.FakeObject(Generation: 13, Metageneration: 1);

        int aDeleteAttempts = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method == HttpMethod.Delete && Uri.UnescapeDataString(req.RequestUri!.AbsolutePath).EndsWith("/o/pfx/a", StringComparison.Ordinal))
            {
                aDeleteAttempts++;
                if (aDeleteAttempts == 1)
                    return JsonError(HttpStatusCode.ServiceUnavailable, "Transient backend error");
            }
            return null;
        };

        _sut.Object_DeleteByPrefix(ClientCredentials(), "test-bucket-alpha", "pfx/", out var deletedCount);

        Assert.Equal(3, deletedCount);
        var deletesForA = _backend.Requests.Where(r =>
            r.Method == HttpMethod.Delete && Uri.UnescapeDataString(r.Uri.AbsolutePath).EndsWith("/o/pfx/a", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, deletesForA.Count);
    }

    // ---- C1-C3: Copy 404 probe disambiguation ----------------------------------------

    [Fact]
    public void C1_Copy_404_when_source_probe_fails_with_403_names_both_and_probe_error()
    {
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal))
                return JsonError(HttpStatusCode.NotFound, "Not Found");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/storage/v1/b/srcb/o/", StringComparison.Ordinal))
                return JsonError(HttpStatusCode.Forbidden, "Caller does not have storage.objects.get access");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Copy(ClientCredentials(), "srcb", "src.txt", "dstb", "dst.txt"));

        Assert.Contains("src.txt", ex.Message);
        Assert.Contains("dstb", ex.Message);
        Assert.Contains("checking the source failed", ex.Message);
    }

    [Fact]
    public void C2_Copy_404_when_source_probe_returns_404_names_missing_source_object()
    {
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal))
                return JsonError(HttpStatusCode.NotFound, "Not Found");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/storage/v1/b/srcb/o/", StringComparison.Ordinal))
                return JsonError(HttpStatusCode.NotFound, "No such object: srcb/src.txt");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Copy(ClientCredentials(), "srcb", "src.txt", "dstb", "dst.txt"));

        Assert.Contains("Object 'src.txt' was not found", ex.Message);
    }

    [Fact]
    public void C3_Copy_404_when_source_probe_returns_200_names_missing_destination_bucket()
    {
        _backend.Objects["srcb/src.txt"] = new FakeFederationBackend.FakeObject(Generation: 10, Metageneration: 1);
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal))
                return JsonError(HttpStatusCode.NotFound, "Not Found");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Copy(ClientCredentials(), "srcb", "src.txt", "dstb", "dst.txt"));

        Assert.Contains("dstb", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    // ---- U1: UpdateMetadata sends a minimal PATCH with null for removed keys ---------

    [Fact]
    public void U1_UpdateMetadata_sends_PATCH_with_preconditions_and_null_for_removed_key()
    {
        _backend.Objects["test-bucket-alpha/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 100, Metageneration: 7);

        _sut.Object_UpdateMetadata(
            ClientCredentials(),
            "test-bucket-alpha",
            "docs/a.txt",
            contentType: "text/csv",
            contentEncoding: "",
            contentDisposition: "",
            cacheControl: "",
            metadata:
            [
                new MetadataEntry { Key = "keep", Value = "v2" },
                new MetadataEntry { Key = "gone", Value = "" }
            ]);

        Assert.DoesNotContain(_backend.Requests, r => r.Method == HttpMethod.Put);
        var patch = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Patch);

        var query = HttpUtility.ParseQueryString(patch.Uri.Query);
        Assert.Equal("100", query["ifGenerationMatch"]);
        Assert.Equal("7", query["ifMetagenerationMatch"]);

        using var doc = JsonDocument.Parse(patch.Body);
        var root = doc.RootElement;
        Assert.Equal("text/csv", root.GetProperty("contentType").GetString());

        var metadataObj = root.GetProperty("metadata");
        Assert.Equal("v2", metadataObj.GetProperty("keep").GetString());
        Assert.True(metadataObj.TryGetProperty("gone", out var goneProp));
        Assert.Equal(JsonValueKind.Null, goneProp.ValueKind);

        Assert.False(root.TryGetProperty("size", out _));
        Assert.False(root.TryGetProperty("md5Hash", out _));
        Assert.False(root.TryGetProperty("crc32c", out _));
        Assert.False(root.TryGetProperty("acl", out _));
    }

    // ---- K1-K2: static ExpiringCache limits and supplied token absolute expiry -------

    [Fact]
    public void K1_All_four_static_caches_are_ExpiringCache_with_maxEntries_500()
    {
        var cacheNames = new[]
        {
            "StorageClientCache",
            "UrlSignerCache",
            "FederatedIdentityCache",
            "ServiceAccountCredentialCache"
        };

        foreach (var name in cacheNames)
        {
            var field = typeof(Connector).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var cache = field!.GetValue(null);
            Assert.NotNull(cache);
            var cacheType = cache!.GetType();
            Assert.True(cacheType.IsGenericType && cacheType.GetGenericTypeDefinition().Name.StartsWith("ExpiringCache", StringComparison.Ordinal));

            var maxEntriesField = cacheType.GetField("_maxEntries", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(maxEntriesField);
            Assert.Equal(500, (int)maxEntriesField!.GetValue(cache)!);
        }
    }

    [Fact]
    public void K2_Supplied_SubjectToken_sets_exact_jwt_exp_as_FederatedIdentityCache_absolute_expiry()
    {
        var expectedExpiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long expUnix = new DateTimeOffset(expectedExpiry).ToUnixTimeSeconds();
        string jwt = MakeJwt($"{{\"exp\":{expUnix}}}");
        var auth = SuppliedToken(jwt);

        _sut.Bucket_Exists(auth, "test-bucket-alpha", out var exists);
        Assert.True(exists);

        var fedCacheField = typeof(Connector).GetField("FederatedIdentityCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fedCache = fedCacheField.GetValue(null)!;
        var entriesField = fedCache.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entriesDict = (IDictionary)entriesField.GetValue(fedCache)!;

        var fedKeyMethod = typeof(Connector).GetMethod("FederatedCacheKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fedKey = (string)fedKeyMethod.Invoke(null, [auth])!;

        var entryObj = entriesDict[fedKey];
        Assert.NotNull(entryObj);
        var absExpiryProp = entryObj!.GetType().GetProperty("AbsoluteExpiryUtc", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var actualExpiry = (DateTime)absExpiryProp.GetValue(entryObj)!;

        Assert.Equal(expectedExpiry, actualExpiry);
    }

    // ---- IO1-IO2: IOException translation --------------------------------------------

    [Fact]
    public void IO1_TranslateException_explains_download_hash_mismatch_IOException()
    {
        var io = new IOException("Incorrect hash: expected 'a' (base64), was 'b' (base64)");
        var translated = Connector.TranslateException(io, ClientCredentials(), "test-bucket-alpha", "obj.txt");
        Assert.Contains("did not match the object's checksum", translated.Message);
    }

    [Fact]
    public void IO2_TranslateException_explains_transport_interruption_IOException_without_double_dots()
    {
        var io = new IOException("Unable to read data from the transport connection.");
        var translated = Connector.TranslateException(io, ClientCredentials(), "test-bucket-alpha", "obj.txt");
        Assert.Contains("was interrupted", translated.Message);
        Assert.DoesNotContain("..", translated.Message);
    }

    // ---- P1: IdP rejection strips trailing period before ". Check" -------------------

    [Fact]
    public void P1_Idp_error_description_ending_with_period_does_not_produce_double_dots()
    {
        _backend.IdpResponseOverride = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_scope","error_description":"Bad scope."}""", Encoding.UTF8, "application/json")
        };

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));

        Assert.Contains("Bad scope. Check", ex.Message);
        Assert.DoesNotContain("Bad scope..", ex.Message);
    }
}
