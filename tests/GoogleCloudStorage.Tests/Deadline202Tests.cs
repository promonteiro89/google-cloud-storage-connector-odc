using Google.Apis.Http;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using GcsFile = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.BinaryContent;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests for the 2.0.2 action deadline: every action except the File transfers and
/// Object_DeleteByPrefix stops itself before ODC's 95-second kill, with a message that names what it
/// was doing and the state it leaves. Requests are stalled (honouring cancellation) by a delegating
/// handler around <see cref="FakeFederationBackend"/>, so no test waits for a real timeout.
/// </summary>
public sealed class Deadline202Tests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";
    private const string Bucket = "test-bucket-alpha";
    private const string TimeoutPhrase = "did not finish within 1 seconds, the most an ODC action can spend on it.";

    private const string UploadState = "The object may or may not have been stored: check with Object_Exists, or upload it again (an upload replaces the object).";
    private const string DownloadState = "Nothing was changed. Retry the action; for a large object use Object_Download or Object_GetSignedUrl.";
    private const string NothingChanged = "Nothing was changed. Retry the action.";
    private const string TooManyObjects = "There are too many objects for one call: set MaxResults and read the list page by page with PageToken.";
    private const string NothingDeleted = "Nothing was deleted. Retry the action.";
    private const string MaybeDeleted = "The object may or may not have been deleted: check with Object_Exists.";
    private const string CopyMayNotFinish = "Google copies a large object between locations or storage classes in several steps, which one ODC action may not be able to finish. The destination object exists only if the last step completed: check with Object_Exists.";
    private const string NothingMoved = "Nothing was moved. Retry the action.";
    private const string MoveCopyMayNotFinish = "Google copies a large object between locations or storage classes in several steps, which one ODC action may not be able to finish. The source was not deleted; the destination object exists only if the last step completed: check with Object_Exists.";
    private const string MoveDeleteUnfinished = "The object was copied to the destination, but deleting the source did not finish: both objects may exist. Check the source with Object_Exists.";
    private const string MaybeUpdated = "The update may or may not have been applied: read the object with Object_GetMetadata.";
    private const string MaybeCreated = "The bucket may or may not have been created: check with Bucket_Exists.";
    private const string MaybeDeletedBucket = "The bucket may or may not have been deleted: check with Bucket_Exists.";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan _savedActionDeadline = Connector.ActionDeadline;
    private readonly TimeSpan[] _savedRetryDelays = Connector.TransferRetryDelays;
    private readonly DeadlineHttpClientFactory _factory;

    public Deadline202Tests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        _previousFactory = Connector.HttpClientFactoryOverride;
        _factory = new DeadlineHttpClientFactory(_backend);
        Connector.HttpClientFactoryOverride = _factory;
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = _previousFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.ActionDeadline = _savedActionDeadline;
        Connector.TransferRetryDelays = _savedRetryDelays;
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static Authentication Federated() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
        ClientId = "my-client",
        ClientSecret = "my-secret",
        Scope = "api://gcs-connector/.default"
    };

    private static string Path(HttpRequestMessage r) => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath);

    private static bool IsDelete(HttpRequestMessage r) => r.Method == HttpMethod.Delete;
    private static bool IsRewrite(HttpRequestMessage r) => r.Method == HttpMethod.Post && Path(r).Contains("/rewriteTo/", StringComparison.Ordinal);
    private static bool IsMediaGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.RequestUri!.Query)["alt"] == "media";
    private static bool IsObjectGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal) && !IsMediaGet(r);
    private static bool IsBucketGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r) == $"/storage/v1/b/{Bucket}";
    private static bool IsRecordedDelete(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Delete;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage JsonError(HttpStatusCode status, string message) =>
        Json(status, JsonSerializer.Serialize(new { error = new { code = (int)status, message } }));

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

    private void Seed(string name, long generation, long size = 5, string contentType = "text/plain") =>
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(generation, 1, contentType, size);

    private void SeedContent(string name, byte[] bytes)
    {
        _backend.StoredObjects[$"{Bucket}/{name}"] = bytes;
        Seed(name, 3, bytes.Length, "application/octet-stream");
    }

    /// <summary>Asserts the deadline message of one action: its own text, what it was doing and the state it leaves.</summary>
    private static void AssertTimedOut(Exception ex, string what, string state, TimeSpan elapsed)
    {
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed}");
        Assert.StartsWith(what, ex.Message);
        Assert.Contains(TimeoutPhrase, ex.Message);
        Assert.Contains(state, ex.Message);
        Assert.DoesNotContain("timed out (no response within", ex.Message);
    }

    // ---- T1: every action stops at the deadline --------------------------------------

    [Fact]
    public void T1_every_action_stops_at_the_deadline_with_its_own_message()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _factory.Delay = TimeSpan.FromSeconds(30);

        var cases = new (string What, string State, Action<Authentication> Call)[]
        {
            ("Uploading 'up.bin'", UploadState,
                a => _sut.Object_UploadBinary(a, Bucket, "up.bin", new GcsFile { Content = new byte[] { 1, 2, 3 }, ContentType = "text/plain" }, Array.Empty<MetadataEntry>(), out _)),
            ("Downloading 'a.txt'", DownloadState,
                a => _sut.Object_DownloadBinary(a, Bucket, "a.txt", out _)),
            ($"Listing the objects of bucket '{Bucket}'", TooManyObjects,
                a => _sut.Object_List(a, Bucket, "", 0, "", "", out _, out _, out _)),
            ($"Listing the objects of bucket '{Bucket}'", NothingChanged,
                a => _sut.Object_List(a, Bucket, "", 10, "", "", out _, out _, out _)),
            ("Checking object 'a.txt'", NothingChanged,
                a => _sut.Object_Exists(a, Bucket, "a.txt", out _)),
            ("Reading the metadata of object 'a.txt'", NothingChanged,
                a => _sut.Object_GetMetadata(a, Bucket, "a.txt", out _, out _, out _)),
            ("Deleting object 'a.txt'", NothingDeleted,
                a => _sut.Object_Delete(a, Bucket, "a.txt")),
            ("Copying 'srcb/src.txt' to 'dstb/dst.txt'", CopyMayNotFinish,
                a => _sut.Object_Copy(a, "srcb", "src.txt", "dstb", "dst.txt")),
            ("Moving 'srcb/src.txt' to 'dstb/dst.txt'", NothingMoved,
                a => _sut.Object_Move(a, "srcb", "src.txt", "dstb", "dst.txt")),
            ("Updating the metadata of object 'a.txt'", NothingChanged,
                a => _sut.Object_UpdateMetadata(a, Bucket, "a.txt", "text/csv", "", "", "", Array.Empty<MetadataEntry>())),
            ("Listing the buckets of project 'proj'", NothingChanged,
                a => _sut.Bucket_List(a, out _)),
            ("Creating bucket 'new-bucket'", MaybeCreated,
                a => _sut.Bucket_Create(a, "new-bucket", "US")),
            ($"Deleting bucket '{Bucket}'", MaybeDeletedBucket,
                a => _sut.Bucket_Delete(a, Bucket)),
            ($"Checking bucket '{Bucket}'", NothingChanged,
                a => _sut.Bucket_Exists(a, Bucket, out _))
        };

        foreach (var (what, state, call) in cases)
        {
            var watch = Stopwatch.StartNew();
            var ex = Assert.ThrowsAny<Exception>(() => call(Federated()));
            watch.Stop();

            AssertTimedOut(ex, what, state, watch.Elapsed);
        }
    }

    // ---- T2: the body of a download stalls after the headers arrive -------------------

    [Fact]
    public void T2_download_binary_stops_when_the_body_stalls()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.StorageResponseOverride = req => IsMediaGet(req)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }
            : null;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "a.txt", out _));
        watch.Stop();

        AssertTimedOut(ex, "Downloading 'a.txt'", DownloadState, watch.Elapsed);
    }

    // ---- T3: the rewrite loop of a copy ---------------------------------------------

    [Fact]
    public void T3_copy_rewrite_loop_stops_at_the_deadline()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _factory.Delay = TimeSpan.FromMilliseconds(200);
        int rewrites = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsRewrite(req)) return null;
            Interlocked.Increment(ref rewrites);
            return Json(HttpStatusCode.OK,
                """{"kind":"storage#rewriteResponse","done":false,"rewriteToken":"t","totalBytesRewritten":"1","objectSize":"100"}""");
        };

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Copy(Federated(), "srcb", "src/a.txt", "dstb", "dst/a.txt"));
        watch.Stop();

        AssertTimedOut(ex, "Copying 'srcb/src/a.txt' to 'dstb/dst/a.txt'", CopyMayNotFinish, watch.Elapsed);
        Assert.True(rewrites >= 2, $"expected at least two rewrite requests, got {rewrites}");
    }

    [Fact]
    public void T3b_copy_whose_look_at_the_source_is_cut_short_reports_the_deadline()
    {
        // The copy answers an unclear 404, so the connector looks at the source; that look stalls.
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.StorageResponseOverride = req => IsRewrite(req) ? JsonError(HttpStatusCode.NotFound, "Not Found") : null;
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = req => req.Method == HttpMethod.Get;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Copy(Federated(), "srcb", "src/a.txt", "dstb", "dst/a.txt"));
        watch.Stop();

        AssertTimedOut(ex, "Copying 'srcb/src/a.txt' to 'dstb/dst/a.txt'", "Nothing was copied. Retry the action.", watch.Elapsed);
    }

    [Fact]
    public void T16_the_request_timeout_never_pre_empts_an_action_deadline_and_stays_under_odcs_limit()
    {
        // A request that hangs from the start must be stopped by the action's deadline, whose message
        // says what the action was doing, and not by the per-request timeout, whose message cannot.
        Assert.True(Connector.StorageRequestTimeout > Connector.ActionDeadline);
        Assert.True(Connector.StorageRequestTimeout > Connector.FileTransferDeadline);
        Assert.True(Connector.StorageRequestTimeout > Connector.DeleteByPrefixDeadline);
        Assert.True(Connector.StorageRequestTimeout < TimeSpan.FromSeconds(95));
    }

    // ---- T4-T7: the phase a timed-out action was in ----------------------------------

    [Fact]
    public void T4_delete_whose_delete_stalls_says_it_may_have_happened()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        Seed("doc.txt", 7);
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = IsDelete;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(Federated(), Bucket, "doc.txt"));
        watch.Stop();

        AssertTimedOut(ex, "Deleting object 'doc.txt'", MaybeDeleted, watch.Elapsed);
    }

    [Fact]
    public void T5_move_whose_rewrite_stalls_says_the_source_was_not_deleted()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = IsRewrite;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(Federated(), "srcb", "src/a.txt", "dstb", "dst/a.txt"));
        watch.Stop();

        AssertTimedOut(ex, "Moving 'srcb/src/a.txt' to 'dstb/dst/a.txt'", MoveCopyMayNotFinish, watch.Elapsed);
    }

    [Fact]
    public void T6_move_whose_source_delete_stalls_says_both_objects_may_exist()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = IsDelete;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(Federated(), "srcb", "src/a.txt", "dstb", "dst/a.txt"));
        watch.Stop();

        AssertTimedOut(ex, "Moving 'srcb/src/a.txt' to 'dstb/dst/a.txt'", MoveDeleteUnfinished, watch.Elapsed);
    }

    [Fact]
    public void T7_update_metadata_whose_patch_stalls_says_it_may_have_been_applied()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        Seed("doc.txt", 7);
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = req => req.Method == HttpMethod.Patch;

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_UpdateMetadata(Federated(), Bucket, "doc.txt", "text/csv", "", "", "", Array.Empty<MetadataEntry>()));
        watch.Stop();

        AssertTimedOut(ex, "Updating the metadata of object 'doc.txt'", MaybeUpdated, watch.Elapsed);
    }

    // ---- T8: the 404 probe of Object_Exists ------------------------------------------

    [Fact]
    public void T8_exists_stops_the_bucket_probe_and_keeps_the_404()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _backend.StorageResponseOverride = req => IsObjectGet(req)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
            : null;
        _factory.Delay = TimeSpan.FromSeconds(30);
        _factory.StallWhen = IsBucketGet;

        var watch = Stopwatch.StartNew();
        _sut.Object_Exists(Federated(), Bucket, "a.txt", out var exists);
        watch.Stop();

        Assert.False(exists);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    // ---- T9: the retry logic is unchanged under a long deadline ----------------------

    [Fact]
    public void T9_three_503s_on_a_delete_still_send_exactly_three_requests()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(85);
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = req => IsDelete(req) ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(Federated(), Bucket, "doc.txt"));

        Assert.Equal(3, _backend.Requests.Count(IsRecordedDelete));
        Assert.Contains("503", ex.Message);
    }

    // ---- T10-T11: the capped binary download -----------------------------------------

    [Fact]
    public void T10_download_binary_refuses_an_object_above_the_cap()
    {
        var bytes = new byte[7 * 1024 * 1024];
        new Random(1).NextBytes(bytes);
        SeedContent("large.bin", bytes);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "large.bin", out _));

        Assert.StartsWith("Object 'large.bin' is larger than 5767168 bytes (5.5 MB)", ex.Message);
    }

    [Fact]
    public void T11_download_binary_returns_the_bytes_of_a_smaller_object()
    {
        var bytes = new byte[3 * 1024 * 1024];
        new Random(2).NextBytes(bytes);
        SeedContent("small.bin", bytes);

        _sut.Object_DownloadBinary(Federated(), Bucket, "small.bin", out var content);

        Assert.Equal(bytes, content.Content);
        Assert.Equal("application/octet-stream", content.ContentType);
    }

    // ---- T12: listings keep their items and order ------------------------------------

    [Fact]
    public void T12_listings_return_the_same_items_in_the_same_order()
    {
        _backend.StorageResponseOverride = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(req.RequestUri.Query);
            if (req.Method == HttpMethod.Get && path == $"/storage/v1/b/{Bucket}/o")
            {
                return query["pageToken"] switch
                {
                    null or "" => ObjectPage(new[] { ("a.txt", 1L), ("b.txt", 2L) }, "p1", new[] { "docs/" }),
                    "p1" => ObjectPage(new[] { ("c.txt", 3L), ("d.txt", 4L) }, "p2", new[] { "docs/", "img/" }),
                    _ => ObjectPage(new[] { ("e.txt", 5L) }, null, null)
                };
            }
            if (req.Method == HttpMethod.Get && path == "/storage/v1/b")
            {
                return query["pageToken"] == "b2"
                    ? BucketPage(new[] { ("bucket-c", "EU") }, null)
                    : BucketPage(new[] { ("bucket-a", "EU"), ("bucket-b", "US") }, "b2");
            }
            return null;
        };

        _sut.Object_List(Federated(), Bucket, "", 0, "", "", out var objects, out var nextPageToken, out var prefixes);

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.txt", "e.txt" }, objects.Select(o => o.Name));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, objects.Select(o => o.Size));
        Assert.Equal("", nextPageToken);
        Assert.Equal(new[] { "docs/", "img/" }, prefixes.Select(p => p.Value));

        _sut.Bucket_List(Federated(), out var buckets);

        Assert.Equal(new[] { "bucket-a", "bucket-b", "bucket-c" }, buckets.Select(b => b.Name));
        Assert.Equal(new[] { "EU", "US", "EU" }, buckets.Select(b => b.Location));
    }

    // ---- T13-T14: signing and the default --------------------------------------------

    [Fact]
    public void T13_signed_urls_still_work_in_service_account_key_mode()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), Bucket, "a.txt", 5, out var url);

        Assert.StartsWith("https://storage.googleapis.com/", url);
    }

    [Fact]
    public void T15_a_cancellation_of_any_kind_is_translated_so_a_late_look_up_cannot_leak_it()
    {
        // The SDK throws a plain OperationCanceledException (not a TaskCanceledException) when it is
        // handed a token that is already cancelled, as a look-up made just after the deadline is.
        Assert.True(Connector.IsTranslatable(new OperationCanceledException()));
        Assert.True(Connector.IsTranslatable(new TaskCanceledException()));
    }

    [Fact]
    public void T17_the_deadline_message_cleans_the_object_name()
    {
        Connector.ActionDeadline = TimeSpan.FromSeconds(1);
        _factory.Delay = TimeSpan.FromSeconds(30);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), Bucket, "a\r\nERROR forged\u202Eb", out _));

        Assert.StartsWith("Checking object 'a  ERROR forged b'", ex.Message);
    }

    [Fact]
    public void T14_the_action_deadline_defaults_to_85_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(85), Connector.ActionDeadline);
    }

    // ---- Fakes -----------------------------------------------------------------------

    /// <summary>
    /// Google's HTTP client factory whose requests to the Storage host stall for <see cref="Delay"/>,
    /// ending as soon as the request's token is cancelled (a real slow server, not a sleeping thread).
    /// </summary>
    private sealed class DeadlineHttpClientFactory : HttpClientFactory
    {
        private readonly FakeFederationBackend _backend;

        public DeadlineHttpClientFactory(FakeFederationBackend backend) => _backend = backend;

        /// <summary>How long each stalled request waits; zero disables stalling.</summary>
        public TimeSpan Delay { get; set; }

        /// <summary>Which Storage requests to stall; every one by default.</summary>
        public Func<HttpRequestMessage, bool> StallWhen { get; set; } = _ => true;

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new DelayHandler(_backend, this);

        private sealed class DelayHandler : DelegatingHandler
        {
            private readonly DeadlineHttpClientFactory _owner;

            public DelayHandler(HttpMessageHandler backend, DeadlineHttpClientFactory owner) : base(backend) => _owner = owner;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_owner.Delay > TimeSpan.Zero
                    && request.RequestUri?.Host == "storage.googleapis.com"
                    && _owner.StallWhen(request))
                    await Task.Delay(_owner.Delay, cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }

            protected override void Dispose(bool disposing) { /* the fake backend outlives any one client */ }
        }
    }

    /// <summary>A response body that never delivers a byte until the read is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
