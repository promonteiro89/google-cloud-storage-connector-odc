using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Google.Apis.Http;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using OutSystems.ExternalLibraries.SDK.FileStorage.Exceptions;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests for the repeat semantics of the requests the connector repeats itself (2.0.2): a 408 or
/// 429 is a refusal (the request was not applied) while a network error, 500, 502, 503 or 504 leaves the
/// outcome unknown, and a 404/412 on the repeat is read differently in each case. Covers Object_Delete,
/// Object_Move, Object_UpdateMetadata, Bucket_Delete, Bucket_Create, Object_Download, Object_DownloadBinary,
/// the upload deadline and the request-timeout message. Offline: every request goes to
/// <see cref="FakeFederationBackend"/>, and no expected value comes from the code's own output.
/// </summary>
public sealed class RepeatSemantics202Tests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";
    private const string Bucket = "test-bucket-alpha";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly MemoryFileStorage _files = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan _savedDeadline = Connector.FileTransferDeadline;
    private readonly TimeSpan[] _savedRetryDelays = Connector.TransferRetryDelays;

    public RepeatSemantics202Tests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        _previousFactory = Connector.HttpClientFactoryOverride;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        FileStorage.Reset();
        FileStorage.Configure(() => _files);
    }

    public void Dispose()
    {
        FileStorage.Reset();
        Connector.HttpClientFactoryOverride = _previousFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.FileTransferDeadline = _savedDeadline;
        Connector.TransferRetryDelays = _savedRetryDelays;
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static Authentication ClientCredentials(string projectId = "proj") => new()
    {
        ProjectId = projectId,
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
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

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string BucketJson(string name, DateTime timeCreated) =>
        JsonSerializer.Serialize(new
        {
            kind = "storage#bucket",
            name,
            id = name,
            timeCreated = timeCreated.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        });

    private static string Path(HttpRequestMessage r) => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath);

    private static bool IsDelete(HttpRequestMessage r) => r.Method == HttpMethod.Delete;

    private static bool IsMediaGet(HttpRequestMessage r) =>
        r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.RequestUri!.Query)["alt"] == "media";

    private static bool IsObjectGet(HttpRequestMessage r) =>
        r.Method == HttpMethod.Get
        && !IsMediaGet(r)
        && Path(r).Contains($"/b/{Bucket}/o/", StringComparison.Ordinal);

    private static bool IsBucketGet(HttpRequestMessage r) =>
        r.Method == HttpMethod.Get && Path(r) == $"/storage/v1/b/{Bucket}";

    private static bool IsBucketCreate(HttpRequestMessage r) =>
        r.Method == HttpMethod.Post && Path(r) == "/storage/v1/b";

    private static bool IsRecordedDelete(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Delete;

    private static bool IsRecordedMediaGet(FakeFederationBackend.Recorded r) =>
        r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.Uri.Query)["alt"] == "media";

    private static bool IsRecordedBucketGet(FakeFederationBackend.Recorded r) =>
        r.Method == HttpMethod.Get && r.Uri.AbsolutePath == $"/storage/v1/b/{Bucket}";

    private static bool IsRecordedBucketDelete(FakeFederationBackend.Recorded r) =>
        r.Method == HttpMethod.Delete && r.Uri.AbsolutePath == $"/storage/v1/b/{Bucket}";

    private static bool IsRecordedBucketCreate(FakeFederationBackend.Recorded r) =>
        r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/storage/v1/b";

    private int ObjectGetCount(string name) => _backend.Requests.Count(r =>
        r.Method == HttpMethod.Get
        && HttpUtility.ParseQueryString(r.Uri.Query)["alt"] != "media"
        && r.Uri.AbsolutePath.EndsWith($"/o/{name}", StringComparison.Ordinal));

    private void Seed(string name, long generation, string contentType = "text/plain", long size = 5) =>
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(generation, 1, contentType, size);

    private void SeedContent(string name, byte[] bytes, long generation = 3)
    {
        _backend.StoredObjects[$"{Bucket}/{name}"] = bytes;
        Seed(name, generation, "application/octet-stream", bytes.Length);
    }

    /// <summary>Answers the first DELETE with <paramref name="first"/>, the second with <paramref name="second"/>.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> TwoAnswers(HttpStatusCode first, HttpStatusCode second)
    {
        int seen = 0;
        return req =>
        {
            if (!IsDelete(req)) return null;
            return ++seen switch
            {
                1 => JsonError(first, "First attempt"),
                2 => JsonError(second, "Second attempt"),
                _ => null
            };
        };
    }

    // ---- S1-S9: Object_Delete ---------------------------------------------------------

    [Fact]
    public void S1_A_delete_refused_with_429_then_412_reports_the_new_version_without_probing()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = TwoAnswers(HttpStatusCode.TooManyRequests, HttpStatusCode.PreconditionFailed);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was replaced while it was being deleted: a newer version now exists", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(1, ObjectGetCount("doc.txt"));
    }

    [Fact]
    public void S2_A_delete_refused_with_408_then_412_reports_the_new_version_without_probing()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = TwoAnswers(HttpStatusCode.RequestTimeout, HttpStatusCode.PreconditionFailed);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was replaced while it was being deleted: a newer version now exists", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(1, ObjectGetCount("doc.txt"));
    }

    [Fact]
    public void S3_A_delete_repeated_after_a_503_and_answered_412_with_the_object_gone_counts_as_deleted()
    {
        Seed("doc.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
                return ++deletes == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
            if (IsObjectGet(req) && ++gets == 2)
                return JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/doc.txt");
            return null;
        };

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(2, ObjectGetCount("doc.txt"));
    }

    [Fact]
    public void S4_A_delete_repeated_after_a_503_and_answered_412_whose_object_still_exists_reports_it()
    {
        Seed("doc.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
                return ++deletes == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
            if (IsObjectGet(req) && ++gets == 2)
            {
                // Someone replaced the object with a newer version meanwhile.
                lock (_backend.Objects) _backend.Objects[$"{Bucket}/doc.txt"] = new FakeFederationBackend.FakeObject(8, 1);
            }
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was replaced while it was being deleted: a newer version now exists", ex.Message);
        Assert.Equal(2, ObjectGetCount("doc.txt"));
        Assert.Equal(8, _backend.Objects[$"{Bucket}/doc.txt"].Generation);
    }

    [Fact]
    public void S5_A_delete_repeated_after_a_503_and_answered_412_whose_probe_is_forbidden_says_it_could_not_be_confirmed()
    {
        Seed("doc.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
                return ++deletes == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
            if (IsObjectGet(req) && ++gets == 2)
                return JsonError(HttpStatusCode.Forbidden, "caller does not have storage.objects.get access");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("could not be confirmed", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(0)] // 0: the first attempt dies with a network error
    public void S6_A_delete_repeated_after_an_unknown_outcome_and_answered_404_counts_as_deleted(int firstAnswer)
    {
        Seed("doc.txt", 7);
        int deletes = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsDelete(req)) return null;
            deletes++;
            if (deletes > 1) return JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/doc.txt");
            if (firstAnswer == 0) throw new HttpRequestException("The connection was closed unexpectedly.");
            return JsonError((HttpStatusCode)firstAnswer, "Backend Error");
        };

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
    }

    [Fact]
    public void S7_A_delete_refused_with_429_then_answered_404_says_the_object_was_not_found()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = TwoAnswers(HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was not found in bucket", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
    }

    [Fact]
    public void S8_A_delete_503_then_429_then_412_whose_probe_is_404_counts_as_deleted()
    {
        Seed("doc.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
            {
                return ++deletes switch
                {
                    1 => JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error"),
                    2 => JsonError(HttpStatusCode.TooManyRequests, "Rate exceeded"),
                    _ => JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed")
                };
            }
            if (IsObjectGet(req) && ++gets == 2)
                return JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/doc.txt");
            return null;
        };

        _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt");

        Assert.Equal(3, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(2, ObjectGetCount("doc.txt"));
    }

    [Fact]
    public void S9_A_delete_answered_412_on_the_first_attempt_reports_a_replacement_without_a_second_read()
    {
        Seed("doc.txt", 7);
        _backend.StorageResponseOverride = req =>
            IsDelete(req) ? JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(ClientCredentials(), Bucket, "doc.txt"));

        Assert.Contains("was replaced while it was being deleted: a newer version now exists", ex.Message);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(1, ObjectGetCount("doc.txt"));
    }

    // ---- S10-S13: Object_Move --------------------------------------------------------

    [Fact]
    public void S10_A_move_whose_source_delete_is_refused_with_429_then_412_reports_the_newer_source()
    {
        Seed("src.txt", 7);
        _backend.StorageResponseOverride = TwoAnswers(HttpStatusCode.TooManyRequests, HttpStatusCode.PreconditionFailed);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt"));

        Assert.Contains("was modified during the move and was not deleted", ex.Message);
        Assert.DoesNotContain("generation", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        // A 429 is a refusal, so there is nothing to look at: only the read before the copy.
        Assert.Equal(1, ObjectGetCount("src.txt"));
    }

    [Fact]
    public void S11_A_move_whose_source_delete_is_answered_412_after_a_503_and_the_source_is_gone_succeeds()
    {
        Seed("src.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
                return ++deletes == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
            if (IsObjectGet(req) && ++gets == 2)
                return JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/src.txt");
            return null;
        };

        _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(2, ObjectGetCount("src.txt"));
    }

    [Fact]
    public void S12_A_move_whose_source_delete_is_answered_412_after_a_503_and_the_source_exists_reports_both()
    {
        Seed("src.txt", 7);
        int deletes = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsDelete(req)) return null;
            return ++deletes == 1
                ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt"));

        Assert.Contains("was modified during the move and was not deleted", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
        Assert.Equal(2, ObjectGetCount("src.txt"));
    }

    [Fact]
    public void S12b_A_move_whose_look_at_the_source_fails_says_the_state_is_not_known()
    {
        Seed("src.txt", 7);
        int deletes = 0;
        int gets = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req))
                return ++deletes == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
            // The read before the copy answers normally; the look after the 412 is refused.
            if (IsObjectGet(req) && Path(req).EndsWith("/o/src.txt", StringComparison.Ordinal) && ++gets > 1)
                return JsonError(HttpStatusCode.Forbidden, "Forbidden");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt"));

        Assert.Contains("deleting the source 'test-bucket-alpha/src.txt' could not be confirmed", ex.Message);
        Assert.Contains("Both objects may exist: check the source with Object_Exists.", ex.Message);
        Assert.DoesNotContain("holds newer data", ex.Message);
    }

    [Fact]
    public void S12c_A_move_whose_source_delete_keeps_answering_503_says_both_objects_may_exist()
    {
        Seed("src.txt", 7);
        _backend.StorageResponseOverride = req => IsDelete(req) ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt"));

        // A 503 may have been applied: the source may be gone, so the message must not say it exists.
        Assert.Contains("could not be confirmed. Both objects may exist: check the source with Object_Exists.", ex.Message);
        Assert.DoesNotContain("both objects currently exist", ex.Message);
        Assert.Equal(3, _backend.Requests.Count(IsRecordedDelete));
    }

    [Fact]
    public void S12d_A_move_whose_source_delete_is_refused_says_both_objects_exist()
    {
        Seed("src.txt", 7);
        _backend.StorageResponseOverride = req => IsDelete(req) ? JsonError(HttpStatusCode.Forbidden, "Forbidden") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt"));

        // A 403 is a refusal: the source is certainly still there.
        Assert.Contains("could not be deleted - both objects currently exist", ex.Message);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedDelete));
    }

    [Fact]
    public void S13_A_move_whose_source_delete_is_refused_with_429_then_answered_404_succeeds()
    {
        Seed("src.txt", 7);
        _backend.StorageResponseOverride = TwoAnswers(HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound);

        _sut.Object_Move(ClientCredentials(), Bucket, "src.txt", Bucket, "dst.txt");

        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
    }

    // ---- S14-S15: Object_UpdateMetadata ----------------------------------------------

    [Fact]
    public void S14_A_metadata_update_refused_with_429_then_412_changed_nothing()
    {
        Seed("doc.txt", 7);
        int patches = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method != HttpMethod.Patch) return null;
            return ++patches == 1
                ? JsonError(HttpStatusCode.TooManyRequests, "Rate exceeded")
                : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_UpdateMetadata(ClientCredentials(), Bucket, "doc.txt", "text/csv", "", "", "", Array.Empty<MetadataEntry>()));

        Assert.Contains("was changed by another request", ex.Message);
        Assert.DoesNotContain("may already have been applied", ex.Message);
        Assert.Equal(2, patches);
    }

    [Fact]
    public void S15_A_metadata_update_answered_412_after_a_503_may_already_have_been_applied()
    {
        Seed("doc.txt", 7);
        int patches = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (req.Method != HttpMethod.Patch) return null;
            return ++patches == 1
                ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                : JsonError(HttpStatusCode.PreconditionFailed, "Precondition Failed");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_UpdateMetadata(ClientCredentials(), Bucket, "doc.txt", "text/csv", "", "", "", Array.Empty<MetadataEntry>()));

        Assert.Contains("may already have been applied", ex.Message);
        Assert.Contains("whose outcome is unknown", ex.Message);
        Assert.Equal(2, patches);
    }

    // ---- S16-S19: Bucket_Delete ------------------------------------------------------

    [Fact]
    public void S16_A_bucket_delete_answered_404_after_a_503_counts_as_deleted()
    {
        int deletes = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsDelete(req) || Path(req) != $"/storage/v1/b/{Bucket}") return null;
            return ++deletes == 1
                ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                : JsonError(HttpStatusCode.NotFound, "The specified bucket does not exist.");
        };

        _sut.Bucket_Delete(ClientCredentials(), Bucket);

        Assert.Equal(2, _backend.Requests.Count(IsRecordedBucketDelete));
    }

    [Fact]
    public void S17_A_bucket_delete_answered_404_on_the_first_attempt_says_it_does_not_exist()
    {
        int deletes = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsDelete(req) || Path(req) != $"/storage/v1/b/{Bucket}") return null;
            deletes++;
            return JsonError(HttpStatusCode.NotFound, "The specified bucket does not exist.");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Delete(ClientCredentials(), Bucket));

        Assert.Contains("does not exist", ex.Message);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedBucketDelete));
    }

    [Fact]
    public void S18_A_bucket_delete_refused_with_429_then_answered_404_says_it_does_not_exist()
    {
        int deletes = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsDelete(req) || Path(req) != $"/storage/v1/b/{Bucket}") return null;
            return ++deletes == 1
                ? JsonError(HttpStatusCode.TooManyRequests, "Rate exceeded")
                : JsonError(HttpStatusCode.NotFound, "The specified bucket does not exist.");
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Delete(ClientCredentials(), Bucket));

        Assert.Contains("does not exist", ex.Message);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedBucketDelete));
    }

    [Fact]
    public void S19_Three_503s_on_a_bucket_delete_send_exactly_three_requests()
    {
        _backend.StorageResponseOverride = req =>
            IsDelete(req) && Path(req) == $"/storage/v1/b/{Bucket}"
                ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                : null;

        Assert.ThrowsAny<Exception>(() => _sut.Bucket_Delete(ClientCredentials(), Bucket));

        Assert.Equal(3, _backend.Requests.Count(IsRecordedBucketDelete));
    }

    // ---- S20-S23: Bucket_Create ------------------------------------------------------

    [Fact]
    public void S20_A_bucket_create_answered_409_after_a_503_whose_bucket_was_just_created_succeeds()
    {
        int posts = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsBucketCreate(req))
                return ++posts == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.Conflict, "You already own this bucket. Please select another name.");
            if (IsBucketGet(req)) return Json(HttpStatusCode.OK, BucketJson(Bucket, DateTime.UtcNow));
            return null;
        };

        _sut.Bucket_Create(ClientCredentials(), Bucket, "US");

        Assert.Equal(2, posts);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedBucketGet));
    }

    [Fact]
    public void S21_A_bucket_create_answered_409_after_a_503_whose_bucket_is_an_hour_old_is_a_conflict()
    {
        int posts = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsBucketCreate(req))
                return ++posts == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.Conflict, "You already own this bucket. Please select another name.");
            if (IsBucketGet(req)) return Json(HttpStatusCode.OK, BucketJson(Bucket, DateTime.UtcNow.AddHours(-1)));
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(ClientCredentials(), Bucket, "US"));

        Assert.StartsWith("Conflict:", ex.Message);
        Assert.Equal(2, posts);
    }

    [Fact]
    public void S21b_A_bucket_created_three_minutes_before_this_call_is_someone_elses()
    {
        int posts = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsBucketCreate(req))
                return ++posts == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.Conflict, "You already own this bucket. Please select another name.");
            // Recent, but older than this call (plus a minute for clock difference): not this call's bucket.
            if (IsBucketGet(req)) return Json(HttpStatusCode.OK, BucketJson(Bucket, DateTime.UtcNow.AddMinutes(-3)));
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(ClientCredentials(), Bucket, "US"));

        Assert.StartsWith("Conflict:", ex.Message);
    }

    [Fact]
    public void S22_A_bucket_create_answered_409_after_a_503_whose_bucket_cannot_be_read_is_a_conflict()
    {
        int posts = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (IsBucketCreate(req))
                return ++posts == 1
                    ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error")
                    : JsonError(HttpStatusCode.Conflict, "You already own this bucket. Please select another name.");
            if (IsBucketGet(req)) return JsonError(HttpStatusCode.Forbidden, "caller does not have storage.buckets.get access");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(ClientCredentials(), Bucket, "US"));

        Assert.StartsWith("Conflict:", ex.Message);
        Assert.Equal(2, posts);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedBucketGet));
    }

    [Fact]
    public void S23_A_first_bucket_create_answered_409_is_a_conflict_without_looking_at_the_bucket()
    {
        _backend.StorageResponseOverride = req =>
            IsBucketCreate(req) ? JsonError(HttpStatusCode.Conflict, "You already own this bucket. Please select another name.") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(ClientCredentials(), Bucket, "US"));

        Assert.StartsWith("Conflict:", ex.Message);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedBucketCreate));
        Assert.Equal(0, _backend.Requests.Count(IsRecordedBucketGet));
    }

    // ---- S24-S29: Object_Download (File) ---------------------------------------------

    [Fact]
    public void S24_A_file_download_asks_for_the_generation_it_read()
    {
        SeedContent("doc.bin", new byte[] { 1, 2, 3, 4 }, generation: 42);

        _sut.Object_Download(ClientCredentials(), Bucket, "doc.bin", out var file);

        Assert.NotNull(file);
        var media = Assert.Single(_backend.Requests, IsRecordedMediaGet);
        Assert.Equal("42", HttpUtility.ParseQueryString(media.Uri.Query)["generation"]);
    }

    [Fact]
    public void S25_A_file_download_whose_media_request_is_404_after_the_metadata_read_reports_a_replacement()
    {
        SeedContent("doc.bin", new byte[] { 1, 2, 3, 4 });
        _backend.StorageResponseOverride = req =>
            IsMediaGet(req) ? JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/doc.bin") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "doc.bin", out _));

        Assert.Contains("was replaced or deleted while it was being downloaded", ex.Message);
        Assert.Equal(1, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void S26_A_file_download_whose_metadata_read_is_404_says_the_object_was_not_found()
    {
        _backend.StorageResponseOverride = req =>
            IsObjectGet(req) ? JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/a.txt") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "a.txt", out _));

        Assert.Contains("was not found in bucket", ex.Message);
        Assert.DoesNotContain("while it was being downloaded", ex.Message);
    }

    [Fact]
    public void S27_A_file_download_whose_storage_wraps_a_503_still_retries_and_succeeds()
    {
        _files.WrapWriterFailures = true;
        var bytes = new byte[] { 9, 8, 7, 6 };
        SeedContent("doc.bin", bytes);
        int media = 0;
        _backend.StorageResponseOverride = req =>
            IsMediaGet(req) && ++media == 1 ? JsonError(HttpStatusCode.ServiceUnavailable, "Backend Error") : null;

        _sut.Object_Download(ClientCredentials(), Bucket, "doc.bin", out var file);

        Assert.Equal(bytes, _files.Read(file));
        Assert.Equal(2, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void S28_A_file_download_whose_storage_wraps_a_404_reports_the_replacement_not_a_storage_failure()
    {
        _files.WrapWriterFailures = true;
        SeedContent("doc.bin", new byte[] { 1, 2, 3, 4 });
        _backend.StorageResponseOverride = req =>
            IsMediaGet(req) ? JsonError(HttpStatusCode.NotFound, $"No such object: {Bucket}/doc.bin") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "doc.bin", out _));

        Assert.Contains("was replaced or deleted while it was being downloaded", ex.Message);
        Assert.DoesNotContain("ODC file storage failed", ex.Message);
    }

    [Fact]
    public void S29_A_file_download_whose_storage_fails_by_itself_is_reported_as_a_storage_failure()
    {
        SeedContent("doc.bin", new byte[] { 1, 2, 3, 4 });
        _files.FailBeforeWriter = new StorageWrapperException("The temporary file has expired.");

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(ClientCredentials(), Bucket, "doc.bin", out _));

        Assert.StartsWith("ODC file storage failed:", ex.Message);
        Assert.Contains("The temporary file has expired.", ex.Message);
    }

    // ---- S30-S31: Object_DownloadBinary ----------------------------------------------

    [Theory]
    [InlineData("FileNotFound")]
    [InlineData("FileLoad")]
    public void S30_A_file_system_exception_from_a_binary_download_is_not_a_network_error(string kind)
    {
        SeedContent("doc.bin", new byte[] { 1, 2, 3 });
        _backend.StorageResponseOverride = req =>
        {
            if (!IsMediaGet(req)) return null;
            if (kind == "FileNotFound") throw new FileNotFoundException("Could not load file or assembly 'X'");
            throw new FileLoadException("Could not load file or assembly 'X'");
        };

        if (kind == "FileNotFound")
            Assert.Throws<FileNotFoundException>(() => _sut.Object_DownloadBinary(ClientCredentials(), Bucket, "doc.bin", out _));
        else
            Assert.Throws<FileLoadException>(() => _sut.Object_DownloadBinary(ClientCredentials(), Bucket, "doc.bin", out _));

        Assert.Equal(1, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void S31_A_plain_IOException_from_a_binary_download_is_retried_twice_then_succeeds()
    {
        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        SeedContent("doc.bin", bytes);
        int media = 0;
        _backend.StorageResponseOverride = req =>
        {
            if (!IsMediaGet(req)) return null;
            if (++media <= 2) throw new IOException("connection reset");
            return null;
        };

        _sut.Object_DownloadBinary(ClientCredentials(), Bucket, "doc.bin", out var content);

        Assert.Equal(bytes, content.Content);
        Assert.Equal(3, _backend.Requests.Count(IsRecordedMediaGet));
    }

    // ---- S32-S33: deadline and timeout messages ---------------------------------------

    [Fact]
    public void S32_The_upload_deadline_message_says_the_object_may_not_have_been_stored()
    {
        Connector.FileTransferDeadline = TimeSpan.FromMilliseconds(300);
        _files.ReadDelay = TimeSpan.FromMilliseconds(100);
        var file = _files.Add(new byte[200_000], "application/octet-stream");

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Upload(ClientCredentials(), Bucket, "slow.bin", file, "", Array.Empty<MetadataEntry>(), out _));

        Assert.Contains("may not have been stored: check with Object_Exists", ex.Message);
        Assert.DoesNotContain("nothing was stored", ex.Message);
    }

    [Fact]
    public void S33_A_cancelled_object_read_is_the_exact_request_timeout_message()
    {
        _backend.StorageResponseOverride = req => IsObjectGet(req) ? throw new TaskCanceledException() : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(ClientCredentials(), Bucket, "a.txt", out _));

        Assert.Equal(
            "The request to Google Cloud timed out (no response within 90 seconds). Retry the action; for a large upload or download, use Object_GetSignedUrl and let the client transfer the file directly.",
            ex.Message);
    }

    // ---- Fake ODC storage --------------------------------------------------------------

    private sealed class TestFile : IOSFile
    {
        public string Key { get; set; } = "";
        public string Version { get; set; } = "v1";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>Our own ODC storage failure, thrown by the fake with or without a failing writer.</summary>
    private sealed class StorageWrapperException : FileStorageException
    {
        public StorageWrapperException(string message) : base(message) { }
        public StorageWrapperException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// ODC's file storage, in memory. Can wrap whatever the writer throws in a
    /// <see cref="StorageWrapperException"/> (as the real storage does for a failed transfer), or fail by
    /// itself before calling the writer.
    /// </summary>
    private sealed class MemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public bool WrapWriterFailures { get; set; }
        public FileStorageException? FailBeforeWriter { get; set; }
        public TimeSpan ReadDelay { get; set; }
        public int Opens;
        public int Created;

        public IOSFile Add(byte[] data, string contentType)
        {
            var key = Guid.NewGuid().ToString("N");
            _files[key] = data;
            return new TestFile { Key = key, ContentType = contentType, Size = data.Length };
        }

        public byte[] Read(IOSFile file) => _files[file.Key];

        public Task<Stream> ReadFileAsStreamAsync(IOSFile file, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Opens);
            return Task.FromResult<Stream>(new SlowReadStream(new MemoryStream(_files[file.Key]), ReadDelay));
        }

        public async Task<IOSFile> CreateFileFromWriterAsync(string fileName, string contentType, Func<Stream, CancellationToken, Task> writeAsync, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default)
        {
            if (FailBeforeWriter != null) throw FailBeforeWriter;

            var buffer = new MemoryStream();
            try
            {
                await writeAsync(buffer, cancellationToken);
            }
            catch (Exception e) when (WrapWriterFailures)
            {
                throw new StorageWrapperException("The file storage could not save the downloaded data.", e);
            }

            Interlocked.Increment(ref Created);
            var key = Guid.NewGuid().ToString("N");
            _files[key] = buffer.ToArray();
            return new TestFile { Key = key, ContentType = contentType, Size = buffer.Length };
        }

        public Task<IOSFile> CreateFileAsync(string fileName, string contentType, byte[] content, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IOSFile> CreateFileFromStreamAsync(string fileName, string contentType, Stream contentStream, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadFileAsync(IOSFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> ReadFileRangeAsync(IOSFile file, long startByte, long endByte, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IFileMetadata> GetFileMetadataAsync(IOSFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A stream that waits before every read, to simulate a slow transfer.</summary>
    private sealed class SlowReadStream(Stream inner, TimeSpan readDelay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readDelay > TimeSpan.Zero) Thread.Sleep(readDelay);
            return inner.Read(buffer, offset, Math.Min(count, 16 * 1024));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (readDelay > TimeSpan.Zero) await Task.Delay(readDelay, cancellationToken);
            return inner.Read(buffer.Span[..Math.Min(buffer.Length, 16 * 1024)]);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
