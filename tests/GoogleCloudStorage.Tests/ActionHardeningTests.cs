using Google.Apis.Http;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
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
/// Offline tests for the 1.7.2 action hardening, against <see cref="FakeFederationBackend"/> (which
/// enforces generation preconditions, 404s and paging like Google): page-by-page DeleteByPrefix with a
/// budget and a hard deadline, generation-guarded Object_Delete, retried copies/moves/uploads/downloads,
/// ContentType and ProjectId validation, missing-bucket vs missing-object detection and early
/// Operation validation on signed URLs.
/// </summary>
public sealed class ActionHardeningTests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";
    private const string Bucket = "test-bucket-alpha";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan _savedBudget = Connector.DeleteByPrefixBudget;
    private readonly TimeSpan _savedDeadline = Connector.DeleteByPrefixDeadline;
    private readonly int _savedPageSize = Connector.DeleteByPrefixPageSize;
    private readonly TimeSpan[] _savedRetryDelays = Connector.TransferRetryDelays;

    public ActionHardeningTests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        _previousFactory = Connector.HttpClientFactoryOverride;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = _previousFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.DeleteByPrefixBudget = _savedBudget;
        Connector.DeleteByPrefixDeadline = _savedDeadline;
        Connector.DeleteByPrefixPageSize = _savedPageSize;
        Connector.TransferRetryDelays = _savedRetryDelays;
    }

    private static string UniqueServiceAccount() => $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com";

    private static Authentication ClientCredentials(string projectId = "proj") => new()
    {
        ProjectId = projectId,
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = UniqueServiceAccount(),
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
        ClientId = "my-client",
        ClientSecret = "my-secret",
        Scope = "api://gcs-connector/.default"
    };

    private static HttpResponseMessage JsonError(HttpStatusCode status, string message) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { code = (int)status, message } }),
                Encoding.UTF8,
                "application/json")
        };

    private static string Path(HttpRequestMessage r) => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath);

    private static bool IsDelete(HttpRequestMessage r) => r.Method == HttpMethod.Delete;
    private static bool IsRewrite(HttpRequestMessage r) => r.Method == HttpMethod.Post && Path(r).Contains("/rewriteTo/", StringComparison.Ordinal);
    private static bool IsMediaGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.RequestUri!.Query)["alt"] == "media";
    private static bool IsSessionPost(HttpRequestMessage r) => r.Method == HttpMethod.Post && Path(r).StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal);

    private static bool IsRecordedDelete(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Delete;
    private static bool IsRecordedRewrite(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal);
    private static bool IsRecordedMediaGet(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.Uri.Query)["alt"] == "media";
    private static bool IsRecordedSessionPost(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal);
    private static bool IsRecordedList(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith($"/b/{Bucket}/o", StringComparison.Ordinal);

    private void Seed(string name, long generation, long size = 5) =>
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(Generation: generation, Metageneration: 1, Size: size);

    /// <summary>Builds a hook that answers the first request matching <paramref name="match"/> with <paramref name="status"/> and lets every later one through.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> FailFirst(Func<HttpRequestMessage, bool> match, HttpStatusCode status)
    {
        int seen = 0;
        return req => match(req) && Interlocked.Increment(ref seen) == 1 ? JsonError(status, "Transient backend error") : null;
    }

    // ---- P1-P4: Object_DeleteByPrefix ----------------------------------------------------

    [Fact]
    public void P1_DeleteByPrefix_skips_an_object_deleted_after_it_was_listed()
    {
        Seed("pfx/a", 11); Seed("pfx/b", 12); Seed("pfx/c", 13);
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req) && Path(req).EndsWith("/o/pfx/a", StringComparison.Ordinal))
                _backend.Objects.Remove($"{Bucket}/pfx/b");
            return null;
        };

        _sut.Object_DeleteByPrefix(ClientCredentials(), Bucket, "pfx/", out var deletedCount);

        Assert.Equal(2, deletedCount);
    }

    [Fact]
    public void P2_DeleteByPrefix_deletes_each_page_before_fetching_the_next()
    {
        Connector.DeleteByPrefixPageSize = 2;
        for (int i = 1; i <= 5; i++) Seed($"pfx/{i}", 10 + i);

        _sut.Object_DeleteByPrefix(ClientCredentials(), Bucket, "pfx/", out var deletedCount);

        Assert.Equal(5, deletedCount);
        Assert.Empty(_backend.Objects);
        var requests = _backend.Requests.ToList();
        int firstDelete = requests.FindIndex(IsRecordedDelete);
        var lists = requests.Select((r, i) => (r, i)).Where(x => IsRecordedList(x.r)).Select(x => x.i).ToList();
        Assert.True(lists.Count >= 2, "expected at least two list requests");
        Assert.True(firstDelete >= 0 && firstDelete < lists[1], "the first delete must come before the second list request");
    }

    [Fact]
    public void P3_DeleteByPrefix_with_an_exhausted_budget_deletes_nothing_and_says_so()
    {
        Connector.DeleteByPrefixBudget = TimeSpan.Zero;
        Seed("pfx/a", 11); Seed("pfx/b", 12); Seed("pfx/c", 13);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(ClientCredentials(), Bucket, "pfx/", out _));

        Assert.Contains("Deleted 0 objects", ex.Message);
        Assert.DoesNotContain(_backend.Requests, IsRecordedDelete);
    }

    [Fact]
    public void P4_DeleteByPrefix_deadline_cancels_a_request_in_flight()
    {
        Connector.DeleteByPrefixDeadline = TimeSpan.FromMilliseconds(300);
        Seed("pfx/a", 11); Seed("pfx/b", 12); Seed("pfx/c", 13);
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req)) Thread.Sleep(2000);
            return null;
        };

        var watch = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(ClientCredentials(), Bucket, "pfx/", out _));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"took {watch.Elapsed}");
        Assert.Contains("time budget", ex.Message);
        Assert.True(_backend.Requests.Count(IsRecordedDelete) <= 1);
    }

    // ---- R1-R3: Copy and Move ------------------------------------------------------------

    [Fact]
    public void R1_Copy_retries_a_503_on_the_rewrite()
    {
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = FailFirst(IsRewrite, HttpStatusCode.ServiceUnavailable);

        _sut.Object_Copy(ClientCredentials(), "srcb", "src/a.txt", "dstb", "dst/a.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedRewrite));
    }

    [Fact]
    public void R2_Move_retries_a_503_on_the_rewrite_and_deletes_the_source()
    {
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = FailFirst(IsRewrite, HttpStatusCode.ServiceUnavailable);

        _sut.Object_Move(ClientCredentials(), "srcb", "src/a.txt", "dstb", "dst/a.txt");

        var rewrites = _backend.Requests.Where(IsRecordedRewrite).ToList();
        Assert.Equal(2, rewrites.Count);
        Assert.All(rewrites, r => Assert.Equal("42", HttpUtility.ParseQueryString(r.Uri.Query)["ifSourceGenerationMatch"]));
        Assert.False(_backend.Objects.ContainsKey("srcb/src/a.txt"));
        Assert.True(_backend.Objects.ContainsKey("dstb/dst/a.txt"));
    }

    [Fact]
    public void R3_Move_whose_source_changed_before_the_404_probe_reports_a_change_not_a_missing_bucket()
    {
        _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 42, Metageneration: 1);
        _backend.StorageResponseOverride = req =>
        {
            if (!IsRewrite(req)) return null;
            _backend.Objects["srcb/src/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 43, Metageneration: 1);
            return JsonError(HttpStatusCode.NotFound, "No such object: srcb/src/a.txt");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), "srcb", "src/a.txt", "dstb", "dst/a.txt"));

        Assert.Contains("changed while it was being moved", ex.Message);
        Assert.DoesNotContain("does not exist", ex.Message);
    }

    // ---- D1-D4: Object_Delete ------------------------------------------------------------

    [Fact]
    public void D1_Delete_sends_the_generation_it_read()
    {
        Seed("doc.txt", 7);

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        var delete = Assert.Single(_backend.Requests, IsRecordedDelete);
        Assert.Equal("7", HttpUtility.ParseQueryString(delete.Uri.Query)["ifGenerationMatch"]);
        Assert.Empty(_backend.Objects);
    }

    [Fact]
    public void D2_Delete_retries_a_503()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = FailFirst(IsDelete, HttpStatusCode.ServiceUnavailable);

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Empty(_backend.Objects);
    }

    [Fact]
    public void D3_Delete_of_a_replaced_object_reports_it_and_deletes_nothing()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = req => IsDelete(req) ? JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was replaced while it was being deleted", ex.Message);
    }

    [Fact]
    public void D4_Delete_of_a_missing_object_says_not_found_and_sends_no_delete()
    {
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "nothing.txt"));

        Assert.Contains("was not found", ex.Message);
        Assert.DoesNotContain(_backend.Requests, IsRecordedDelete);
    }

    // ---- DL1-DL4: Object_Download --------------------------------------------------------

    private void SeedContent(string name, byte[] bytes)
    {
        _backend.StoredObjects[$"{Bucket}/{name}"] = bytes;
        Seed(name, 3, bytes.Length);
    }

    [Fact]
    public void DL1_Download_retries_a_503()
    {
        var bytes = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        SeedContent("big.bin", bytes);
        _backend.StorageResponseOverride = FailFirst(IsMediaGet, HttpStatusCode.ServiceUnavailable);

        _sut.Object_Download(ClientCredentials(), Bucket, "big.bin", out var file);

        Assert.Equal(bytes, file.Content);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void DL2_Download_gives_up_after_three_attempts()
    {
        SeedContent("big.bin", new byte[10]);
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? JsonError(HttpStatusCode.ServiceUnavailable, "Transient backend error") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "big.bin", out _));

        Assert.Contains("503", ex.Message);
        Assert.Equal(3, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void DL3_Download_does_not_retry_a_400()
    {
        SeedContent("big.bin", new byte[10]);
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? JsonError(HttpStatusCode.BadRequest, "Bad request") : null;

        Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "big.bin", out _));

        Assert.Single(_backend.Requests, IsRecordedMediaGet);
    }

    [Fact]
    public void DL4_Download_of_a_gzip_transcoded_object_returns_the_plain_bytes()
    {
        var plain = Encoding.UTF8.GetBytes("hello gzip world");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(plain, 0, plain.Length);
        SeedContent("zipped.txt", compressed.ToArray());
        _backend.GzipTranscoded.Add($"{Bucket}/zipped.txt");

        _sut.Object_Download(ClientCredentials(), Bucket, "zipped.txt", out var file);

        Assert.Equal(plain, file.Content);
    }

    // ---- UP1-UP2: Object_Upload ----------------------------------------------------------

    [Fact]
    public void UP1_Upload_retries_a_503_when_opening_the_session()
    {
        _backend.StorageResponseOverride = FailFirst(IsSessionPost, HttpStatusCode.ServiceUnavailable);
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };

        _sut.Object_Upload(ClientCredentials(), Bucket, "up.bin", new GcsFile { Content = bytes, ContentType = "application/octet-stream" }, []);

        Assert.Equal(bytes, _backend.StoredObjects[$"{Bucket}/up.bin"]);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedSessionPost));
    }

    [Fact]
    public void UP2_Upload_rejects_a_content_type_with_line_breaks_before_any_request()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Upload(ClientCredentials(), Bucket, "up.txt", new GcsFile { Content = [1], ContentType = "text/plain\r\nX-Evil: 1" }, []));

        Assert.Contains("ContentType cannot contain line breaks", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    // ---- B1-B3: ProjectId ----------------------------------------------------------------

    [Fact]
    public void B1_Bucket_create_404_names_the_project_not_the_bucket()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Post && Path(req) == "/storage/v1/b"
                ? JsonError(HttpStatusCode.NotFound, "The requested project was not found.")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(ClientCredentials(), "new-bucket", "US"));

        Assert.Contains("project 'proj'", ex.Message);
        Assert.DoesNotContain("case-sensitive", ex.Message);
    }

    [Fact]
    public void B2_Bucket_actions_reject_a_blank_project_id_before_any_request()
    {
        var auth = ClientCredentials("  ");

        var list = Assert.Throws<ArgumentException>(() => _sut.Bucket_List(auth, out _));
        var create = Assert.Throws<ArgumentException>(() => _sut.Bucket_Create(auth, "new-bucket", "US"));

        Assert.Contains("ProjectId cannot be empty", list.Message);
        Assert.Contains("ProjectId cannot be empty", create.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void B3_Bucket_list_sends_the_trimmed_project_id()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req) == "/storage/v1/b"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"kind":"storage#buckets","items":[]}""", Encoding.UTF8, "application/json") }
                : null;

        _sut.Bucket_List(ClientCredentials(" proj\n"), out _);

        var list = Assert.Single(_backend.Requests, r => r.Uri.AbsolutePath == "/storage/v1/b");
        Assert.Equal("proj", HttpUtility.ParseQueryString(list.Uri.Query)["project"]);
    }

    // ---- X1-X2: missing bucket vs missing object without relying on wording --------------

    private static bool IsObjectGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal) && !IsMediaGet(r);
    private static bool IsBucketGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r) == $"/storage/v1/b/{Bucket}";

    [Fact]
    public void X1_An_ambiguous_404_with_a_missing_bucket_is_an_error_not_a_missing_object()
    {
        _backend.StorageResponseOverride = req => IsObjectGet(req) || IsBucketGet(req) ? JsonError(HttpStatusCode.NotFound, "Not Found") : null;

        var exists = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(ClientCredentials(), Bucket, "a.txt", out _));
        Assert.Contains("Bucket", exists.Message);
        Assert.ThrowsAny<Exception>(() => _sut.Object_GetMetadata(ClientCredentials(), Bucket, "a.txt", out _, out _, out _));
    }

    [Fact]
    public void X2_An_ambiguous_404_with_an_existing_bucket_means_the_object_is_missing()
    {
        _backend.StorageResponseOverride = req => IsObjectGet(req) ? JsonError(HttpStatusCode.NotFound, "Not Found") : null;

        _sut.Object_Exists(ClientCredentials(), Bucket, "a.txt", out var exists);
        _sut.Object_GetMetadata(ClientCredentials(), Bucket, "a.txt", out var metadataExists, out _, out _);

        Assert.False(exists);
        Assert.False(metadataExists);
    }

    // ---- S1: signed URL Operation validated before any network call ---------------------

    [Fact]
    public void S1_GetSignedUrl_rejects_an_invalid_operation_without_any_request()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(ClientCredentials(), Bucket, "a.txt", 5, out _, operation: "downloads"));

        Assert.Contains("Invalid Operation", ex.Message);
        Assert.Empty(_backend.Requests);

        // Building the signer is lazy and sends nothing, so also prove the order: with credentials that
        // cannot build a signer at all, the Operation is still the first thing reported.
        var unusable = ClientCredentials();
        unusable.ServiceAccountEmail = "";
        var first = Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(unusable, Bucket, "a.txt", 5, out _, operation: "downloads"));
        Assert.Contains("Invalid Operation", first.Message);
    }

    // ---- Follow-ups from the O11 1.7.3 review --------------------------------------------

    [Fact]
    public void DL5_Download_timeout_is_translated_not_an_AggregateException_and_not_retried()
    {
        SeedContent("big.bin", new byte[10]);
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? throw new TaskCanceledException("simulated 80-second timeout") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "big.bin", out _));

        Assert.IsNotType<AggregateException>(ex);
        Assert.Contains("timed out", ex.Message);
        Assert.Single(_backend.Requests, IsRecordedMediaGet);
    }

    [Fact]
    public void X3_An_ambiguous_404_whose_bucket_probe_is_forbidden_means_the_object_is_missing()
    {
        // Storage Object Admin cannot read buckets: the probe's 403 must not turn into "bucket does not exist".
        _backend.StorageResponseOverride = req =>
            IsObjectGet(req) ? JsonError(HttpStatusCode.NotFound, "Not Found")
            : IsBucketGet(req) ? JsonError(HttpStatusCode.Forbidden, "caller does not have storage.buckets.get access")
            : null;

        _sut.Object_Exists(ClientCredentials(), Bucket, "a.txt", out var exists);

        Assert.False(exists);
    }

    [Fact]
    public void D5_Delete_with_a_role_that_cannot_read_falls_back_to_an_unguarded_delete()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = req => IsObjectGet(req) ? JsonError(HttpStatusCode.Forbidden, "caller does not have storage.objects.get access") : null;

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        var delete = Assert.Single(_backend.Requests, IsRecordedDelete);
        Assert.Null(HttpUtility.ParseQueryString(delete.Uri.Query)["ifGenerationMatch"]);
        Assert.Empty(_backend.Objects);
    }
}
