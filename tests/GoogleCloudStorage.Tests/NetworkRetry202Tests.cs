using Google.Apis.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests for the 2.0.2 network retry: an idempotent request is repeated after a connection failure
/// (HttpRequestException, network IOException), listings fetch one page per request so a failed page can be
/// repeated, and Object_DeleteByPrefix survives a failed delete and counts a delete whose answer was lost.
/// Connection failures are thrown by a delegating handler in front of <see cref="FakeFederationBackend"/>.
/// </summary>
public sealed class NetworkRetry202Tests : IDisposable
{
    private const string Bucket = "test-bucket-alpha";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly FileTransferTests.FakeFileStorage _files = new();
    private readonly RecordingFactory _factory;
    private readonly string? _savedEmulatorHost;
    private readonly IHttpClientFactory? _savedFactory;
    private readonly TimeSpan[] _savedDelays;
    private readonly TimeSpan _savedWindow;
    private readonly int _savedPageSize;

    public NetworkRetry202Tests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        _savedFactory = Connector.HttpClientFactoryOverride;
        _savedDelays = Connector.TransferRetryDelays;
        _savedWindow = Connector.TransferRetryWindow;
        _savedPageSize = Connector.DeleteByPrefixPageSize;
        Connector.EmulatorBaseUri = null;
        _factory = new RecordingFactory(_backend);
        Connector.HttpClientFactoryOverride = _factory;
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        FileStorage.Configure(() => _files);
    }

    public void Dispose()
    {
        FileStorage.Reset();
        Connector.HttpClientFactoryOverride = _savedFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.TransferRetryDelays = _savedDelays;
        Connector.TransferRetryWindow = _savedWindow;
        Connector.DeleteByPrefixPageSize = _savedPageSize;
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static Authentication Federated() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        SubjectToken = "header.payload.signature"
    };

    private static string Path(HttpRequestMessage r) => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath);
    private static string Query(HttpRequestMessage r, string key) => HttpUtility.ParseQueryString(r.RequestUri!.Query)[key] ?? "";

    private static bool IsMediaGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Query(r, "alt") == "media";
    private static bool IsObjectGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal) && !IsMediaGet(r);
    private static bool IsObjectDelete(HttpRequestMessage r) => r.Method == HttpMethod.Delete && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal);
    private static bool IsObjectPatch(HttpRequestMessage r) => r.Method == HttpMethod.Patch && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal);
    private static bool IsRewrite(HttpRequestMessage r) => r.Method == HttpMethod.Post && Path(r).Contains("/rewriteTo/", StringComparison.Ordinal);
    private static bool IsBucketGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r) == $"/storage/v1/b/{Bucket}";
    private static bool IsObjectList(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r) == $"/storage/v1/b/{Bucket}/o";
    private static bool IsBucketList(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r) == "/storage/v1/b";
    private static bool IsSignBlob(HttpRequestMessage r) => r.RequestUri!.ToString().Contains(":signBlob", StringComparison.Ordinal);

    private int Count(Func<HttpRequestMessage, bool> match) => _factory.Seen.Count(match);

    /// <summary>Throws <paramref name="failure"/> for the first matching request, then lets every later one through.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage?> FailOnce(Func<HttpRequestMessage, bool> match, Func<Exception> failure)
    {
        int failed = 0;
        return req =>
        {
            if (match(req) && Interlocked.Increment(ref failed) == 1) throw failure();
            return null;
        };
    }

    private static Exception ConnectionReset() => new HttpRequestException("connection reset");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ObjectPage((string Name, long Size)[] items, string? nextPageToken, string[]? prefixes)
    {
        var payload = new Dictionary<string, object>
        {
            ["kind"] = "storage#objects",
            ["items"] = items.Select(i => new { kind = "storage#object", bucket = Bucket, name = i.Name, size = i.Size.ToString(), contentType = "text/plain" }).ToArray()
        };
        if (prefixes != null) payload["prefixes"] = prefixes;
        if (nextPageToken != null) payload["nextPageToken"] = nextPageToken;
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
    }

    private static HttpResponseMessage BucketPage((string Name, string Location)[] items, string? nextPageToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["kind"] = "storage#buckets",
            ["items"] = items.Select(i => new { kind = "storage#bucket", name = i.Name, location = i.Location, storageClass = "STANDARD", timeCreated = "2024-01-02T03:04:05Z" }).ToArray()
        };
        if (nextPageToken != null) payload["nextPageToken"] = nextPageToken;
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
    }

    /// <summary>Three pages of two objects (tokens p2, p3); prefixes on pages 1 and 3.</summary>
    private static HttpResponseMessage? ThreeObjectPages(HttpRequestMessage req)
    {
        if (!IsObjectList(req)) return null;
        return Query(req, "pageToken") switch
        {
            "" => ObjectPage(new[] { ("a1.txt", 1L), ("a2.txt", 2L) }, "p2", new[] { "x/", "y/" }),
            "p2" => ObjectPage(new[] { ("b1.txt", 3L), ("b2.txt", 4L) }, "p3", null),
            "p3" => ObjectPage(new[] { ("c1.txt", 5L), ("c2.txt", 6L) }, null, new[] { "y/", "z/" }),
            _ => null
        };
    }

    private void Seed(string name, long generation = 1, string contentType = "text/plain") =>
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(generation, 1, contentType, 5);

    private void SeedUnderPrefix(int count)
    {
        for (int i = 1; i <= count; i++) Seed($"del/{i}");
    }

    private bool AnyLeft(string prefix) => _backend.Objects.Keys.Any(k => k.StartsWith($"{Bucket}/{prefix}", StringComparison.Ordinal));

    // ---- N1-N11: single requests are repeated -----------------------------------------

    [Fact]
    public void N1_exists_repeats_a_failed_object_get()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_Exists(Federated(), Bucket, "a.txt", out var exists);

        Assert.True(exists);
        Assert.Equal(2, Count(IsObjectGet));
    }

    [Fact]
    public void N2_get_metadata_repeats_a_failed_object_get()
    {
        Seed("a.txt", contentType: "text/csv");
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_GetMetadata(Federated(), Bucket, "a.txt", out var exists, out var metadata, out _);

        Assert.True(exists);
        Assert.Equal("a.txt", metadata.Name);
        Assert.Equal("text/csv", metadata.ContentType);
        Assert.Equal(2, Count(IsObjectGet));
    }

    [Fact]
    public void N3_delete_repeats_its_first_read()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_Delete(Federated(), Bucket, "a.txt");

        Assert.False(_backend.Objects.ContainsKey($"{Bucket}/a.txt"));
        Assert.Equal(2, Count(IsObjectGet));
        Assert.Equal(1, Count(IsObjectDelete));
    }

    [Fact]
    public void N4_move_repeats_its_first_read()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_Move(Federated(), Bucket, "a.txt", Bucket, "b.txt");

        Assert.True(_backend.Objects.ContainsKey($"{Bucket}/b.txt"));
        Assert.False(_backend.Objects.ContainsKey($"{Bucket}/a.txt"));
    }

    [Fact]
    public void N5_update_metadata_repeats_its_first_read()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_UpdateMetadata(Federated(), Bucket, "a.txt", "text/csv", "", "", "", Array.Empty<MetadataEntry>());

        Assert.Equal("text/csv", _backend.Objects[$"{Bucket}/a.txt"].ContentType);
        Assert.Equal(2, Count(IsObjectGet));
        Assert.Equal(1, Count(IsObjectPatch));
    }

    [Fact]
    public void N6_download_repeats_its_metadata_get()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        _backend.StoredObjects[$"{Bucket}/a.bin"] = data;
        _backend.Objects[$"{Bucket}/a.bin"] = new FakeFederationBackend.FakeObject(1, 1, "application/octet-stream", data.Length);
        _factory.Hook = FailOnce(IsObjectGet, ConnectionReset);

        _sut.Object_Download(Federated(), Bucket, "a.bin", out var file);

        Assert.Equal(data, _files.Get(file).Data);
    }

    [Fact]
    public void N7_copy_repeats_a_failed_rewrite()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsRewrite, ConnectionReset);

        _sut.Object_Copy(Federated(), Bucket, "a.txt", Bucket, "b.txt");

        Assert.True(_backend.Objects.ContainsKey($"{Bucket}/b.txt"));
        Assert.Equal(2, Count(IsRewrite));
    }

    [Fact]
    public void N8_move_repeats_a_failed_rewrite()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsRewrite, ConnectionReset);

        _sut.Object_Move(Federated(), Bucket, "a.txt", Bucket, "b.txt");

        Assert.True(_backend.Objects.ContainsKey($"{Bucket}/b.txt"));
        Assert.False(_backend.Objects.ContainsKey($"{Bucket}/a.txt"));
        Assert.Equal(2, Count(IsRewrite));
    }

    [Fact]
    public void N9_bucket_exists_repeats_a_failed_bucket_get()
    {
        _factory.Hook = FailOnce(IsBucketGet, ConnectionReset);

        _sut.Bucket_Exists(Federated(), Bucket, out var exists);

        Assert.True(exists);
        Assert.Equal(2, Count(IsBucketGet));
    }

    [Fact]
    public void N10_the_bucket_look_up_after_an_unclear_404_is_repeated()
    {
        var failBucketOnce = FailOnce(IsBucketGet, ConnectionReset);
        _factory.Hook = req =>
        {
            if (IsObjectGet(req)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            return failBucketOnce(req);
        };

        _sut.Object_Exists(Federated(), Bucket, "a.txt", out var exists);

        Assert.False(exists);
        Assert.Equal(2, Count(IsBucketGet));
    }

    [Fact]
    public void N11_signed_url_repeats_a_failed_signBlob()
    {
        _factory.Hook = FailOnce(IsSignBlob, ConnectionReset);

        _sut.Object_GetSignedUrl(Federated(), Bucket, "a.txt", 5, out var url);

        Assert.StartsWith("https://storage.googleapis.com/", url);
        Assert.Equal(2, Count(IsSignBlob));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public void N11b_signed_url_repeats_a_signBlob_that_google_refused_for_now(int status)
    {
        // Google's credential code repeats signBlob after 500/502/503/504 only; 408 and 429 are the connector's.
        int seen = 0;
        _factory.Hook = req => IsSignBlob(req) && ++seen == 1
            ? Json((HttpStatusCode)status, JsonSerializer.Serialize(new { error = new { code = status, message = "Try again." } }))
            : null;

        _sut.Object_GetSignedUrl(Federated(), Bucket, "a.txt", 5, out var url);

        Assert.StartsWith("https://storage.googleapis.com/", url);
        Assert.Equal(2, Count(IsSignBlob));
    }

    [Fact]
    public void N11c_signed_url_does_not_repeat_a_signBlob_that_google_denied()
    {
        _factory.Hook = req => IsSignBlob(req)
            ? Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = new { code = 403, message = "Permission 'iam.serviceAccounts.signBlob' denied." } }))
            : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_GetSignedUrl(Federated(), Bucket, "a.txt", 5, out _));

        Assert.Contains("Service Account Token Creator", ex.Message);
        Assert.Equal(1, Count(IsSignBlob));
    }

    // ---- N12-N15: listings repeat one page --------------------------------------------

    [Fact]
    public void N12_object_list_repeats_only_the_failed_page()
    {
        var failPage2 = FailOnce(r => IsObjectList(r) && Query(r, "pageToken") == "p2", ConnectionReset);
        _factory.Hook = req => failPage2(req) ?? ThreeObjectPages(req);

        _sut.Object_List(Federated(), Bucket, "", 0, "", "", out var objects, out var next, out var prefixes);

        Assert.Equal(new[] { "a1.txt", "a2.txt", "b1.txt", "b2.txt", "c1.txt", "c2.txt" }, objects.Select(o => o.Name));
        Assert.Equal("", next);
        Assert.Equal(new[] { "x/", "y/", "z/" }, prefixes.Select(p => p.Value));
        var lists = _factory.Seen.Where(IsObjectList).ToList();
        Assert.Equal(4, lists.Count);
        Assert.Equal("", Query(lists[0], "pageToken"));
        Assert.Equal("p2", Query(lists[1], "pageToken"));
        Assert.Equal("p2", Query(lists[2], "pageToken"));
        Assert.Equal("p3", Query(lists[3], "pageToken"));
    }

    [Fact]
    public void N13_paged_object_list_sends_one_request_per_call()
    {
        _factory.Hook = ThreeObjectPages;

        _sut.Object_List(Federated(), Bucket, "", 2, "", "", out var first, out var firstNext, out _);

        Assert.Equal(new[] { "a1.txt", "a2.txt" }, first.Select(o => o.Name));
        Assert.Equal("p2", firstNext);
        var lists = _factory.Seen.Where(IsObjectList).ToList();
        Assert.Single(lists);
        Assert.Equal("2", Query(lists[0], "maxResults"));

        _sut.Object_List(Federated(), Bucket, "", 2, "p2", "", out var second, out var secondNext, out _);

        Assert.Equal(new[] { "b1.txt", "b2.txt" }, second.Select(o => o.Name));
        Assert.Equal("p3", secondNext);
    }

    [Fact]
    public void N14_the_delimiter_travels_with_every_page()
    {
        _factory.Hook = ThreeObjectPages;

        _sut.Object_List(Federated(), Bucket, "", 0, "", "/", out var objects, out _, out _);

        Assert.Equal(6, objects.Count());
        var lists = _factory.Seen.Where(IsObjectList).ToList();
        Assert.Equal(3, lists.Count);
        Assert.All(lists, r => Assert.Equal("/", Query(r, "delimiter")));
    }

    [Fact]
    public void N15_bucket_list_repeats_only_the_failed_page()
    {
        var failPage2 = FailOnce(r => IsBucketList(r) && Query(r, "pageToken") == "b2", ConnectionReset);
        _factory.Hook = req =>
        {
            if (failPage2(req) is { } r) return r;
            if (!IsBucketList(req)) return null;
            return Query(req, "pageToken") == "b2"
                ? BucketPage(new[] { ("bucket-c", "EU") }, null)
                : BucketPage(new[] { ("bucket-a", "EU"), ("bucket-b", "US") }, "b2");
        };

        _sut.Bucket_List(Federated(), out var buckets);

        Assert.Equal(new[] { "bucket-a", "bucket-b", "bucket-c" }, buckets.Select(b => b.Name));
        Assert.Equal(3, Count(IsBucketList));
    }

    // ---- N16-N19, N25: Object_DeleteByPrefix ------------------------------------------

    [Fact]
    public void N16_delete_by_prefix_repeats_a_failed_listing_page()
    {
        Connector.DeleteByPrefixPageSize = 2;
        SeedUnderPrefix(5);
        _factory.Hook = FailOnce(r => IsObjectList(r) && Query(r, "pageToken") != "", ConnectionReset);

        _sut.Object_DeleteByPrefix(Federated(), Bucket, "del/", out var deleted);

        Assert.Equal(5, deleted);
        Assert.False(AnyLeft("del/"));
    }

    [Fact]
    public void N17_a_failed_delete_is_repeated_and_counted()
    {
        SeedUnderPrefix(3);
        _factory.Hook = FailOnce(r => IsObjectDelete(r) && Path(r).EndsWith("/o/del/2", StringComparison.Ordinal), ConnectionReset);

        _sut.Object_DeleteByPrefix(Federated(), Bucket, "del/", out var deleted);

        Assert.Equal(3, deleted);
        Assert.False(AnyLeft("del/"));
    }

    [Fact]
    public void N18_a_delete_whose_answer_was_lost_is_counted()
    {
        SeedUnderPrefix(3);
        int failed = 0;
        _factory.Hook = req =>
        {
            if (IsObjectDelete(req) && Path(req).EndsWith("/o/del/2", StringComparison.Ordinal) && Interlocked.Increment(ref failed) == 1)
            {
                lock (_backend.Objects) _backend.Objects.Remove($"{Bucket}/del/2"); // the delete happened ...
                throw ConnectionReset();                                              // ... but the answer was lost
            }
            return null;
        };

        _sut.Object_DeleteByPrefix(Federated(), Bucket, "del/", out var deleted);

        Assert.Equal(3, deleted);
        Assert.False(AnyLeft("del/"));
    }

    [Fact]
    public void N19_a_404_without_a_network_failure_is_not_counted()
    {
        SeedUnderPrefix(3);
        int answered = 0;
        _factory.Hook = req =>
        {
            if (IsObjectDelete(req) && Path(req).EndsWith("/o/del/2", StringComparison.Ordinal) && Interlocked.Increment(ref answered) == 1)
                return Json(HttpStatusCode.NotFound, JsonSerializer.Serialize(new { error = new { code = 404, message = "No such object: del/2" } }));
            return null;
        };

        _sut.Object_DeleteByPrefix(Federated(), Bucket, "del/", out var deleted);

        Assert.Equal(2, deleted);
    }

    [Fact]
    public void N25_delete_by_prefix_pages_with_the_token_it_was_given()
    {
        Connector.DeleteByPrefixPageSize = 2;
        SeedUnderPrefix(5);

        _sut.Object_DeleteByPrefix(Federated(), Bucket, "del/", out var deleted);

        Assert.Equal(5, deleted);
        var lists = _factory.Seen.Where(IsObjectList).ToList();
        Assert.Equal(3, lists.Count);
        Assert.Equal("", Query(lists[0], "pageToken"));
        // The fake's page token is the last name of the page it answered: del/1, del/2 -> "del/2".
        Assert.Equal("del/2", Query(lists[1], "pageToken"));
    }

    // ---- N20-N24: what is and is not repeated -----------------------------------------

    [Fact]
    public void N20_a_permanent_connection_failure_gives_up_after_the_delays()
    {
        _factory.Hook = req => IsObjectGet(req) ? throw ConnectionReset() : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), Bucket, "a.txt", out _));

        Assert.StartsWith("Could not reach Google Cloud", ex.Message);
        Assert.Equal(3, Count(IsObjectGet));
    }

    [Fact]
    public void N21_a_network_IOException_is_repeated()
    {
        Seed("a.txt");
        _factory.Hook = FailOnce(IsObjectGet, () => new IOException("connection reset"));

        _sut.Object_Exists(Federated(), Bucket, "a.txt", out var exists);

        Assert.True(exists);
        Assert.Equal(2, Count(IsObjectGet));
    }

    [Fact]
    public void N22_a_missing_assembly_is_not_repeated()
    {
        _factory.Hook = req => IsObjectGet(req) ? throw new FileNotFoundException("Could not load file or assembly 'X'") : null;

        Assert.Throws<FileNotFoundException>(() => _sut.Object_Exists(Federated(), Bucket, "a.txt", out _));

        Assert.Equal(1, Count(IsObjectGet));
    }

    [Fact]
    public void N23_a_status_from_google_is_not_repeated_by_the_helper()
    {
        _factory.Hook = req => IsObjectGet(req)
            ? Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = new { code = 403, message = "Caller does not have storage.objects.get access." } }))
            : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), Bucket, "a.txt", out _));

        Assert.Contains("Access denied", ex.Message);
        Assert.Equal(1, Count(IsObjectGet));
    }

    [Fact]
    public void N24_no_repeat_once_the_retry_window_has_passed()
    {
        Connector.TransferRetryWindow = TimeSpan.FromMilliseconds(100);
        int calls = 0;
        _factory.Hook = req =>
        {
            if (IsObjectGet(req) && Interlocked.Increment(ref calls) == 1)
            {
                Thread.Sleep(300);
                throw ConnectionReset();
            }
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), Bucket, "a.txt", out _));

        Assert.StartsWith("Could not reach Google Cloud", ex.Message);
        Assert.Equal(1, Count(IsObjectGet));
    }

    // ---- Fakes -----------------------------------------------------------------------

    /// <summary>Records every request that reaches it, then lets <see cref="Hook"/> answer or throw before the fake backend.</summary>
    private sealed class RecordingFactory : HttpClientFactory
    {
        private readonly FakeFederationBackend _backend;
        private readonly List<HttpRequestMessage> _seen = new();

        public RecordingFactory(FakeFederationBackend backend) => _backend = backend;

        public Func<HttpRequestMessage, HttpResponseMessage?>? Hook { get; set; }

        /// <summary>A snapshot of every request seen so far (including those the hook failed).</summary>
        public IReadOnlyList<HttpRequestMessage> Seen { get { lock (_seen) return _seen.ToList(); } }

        public void Record(HttpRequestMessage request) { lock (_seen) _seen.Add(request); }

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new RecordingHandler(_backend, this);

        private sealed class RecordingHandler : DelegatingHandler
        {
            private readonly RecordingFactory _owner;

            public RecordingHandler(HttpMessageHandler backend, RecordingFactory owner) : base(backend) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _owner.Record(CopyOf(request));
                if (_owner.Hook?.Invoke(request) is { } answer) return Task.FromResult(answer);
                return base.SendAsync(request, cancellationToken);
            }

            // The SDK may dispose a request after it is sent; keep only what the assertions read.
            private static HttpRequestMessage CopyOf(HttpRequestMessage request) => new(request.Method, request.RequestUri);

            protected override void Dispose(bool disposing) { /* the fake backend outlives any one client */ }
        }
    }
}
