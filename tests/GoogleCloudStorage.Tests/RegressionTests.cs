using System.Net;
using System.Reflection;
using System.Text;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using GcsFile = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Regression checks for the correctness fixes applied to the actions: required-name validation,
/// timestamp parsing, bucket/object error classification, the download size cap (all five write
/// paths of the capped stream) and the update preconditions. Everything here is offline; the
/// emulator/backed-fake cases for Move/Copy/DeleteByPrefix live in IntegrationTests and in the
/// update-precondition test below (which uses the in-process fake Storage API because
/// fake-gcs-server ignores update preconditions).
/// </summary>
public class RegressionTests
{
    private readonly IGoogleCloudStorage _sut = new Connector();

    /// <summary>A 404 shaped like the ones the Google client raises, with Google's message text.</summary>
    private static Google.GoogleApiException NotFound(string message)
    {
        var e = new Google.GoogleApiException("storage", message);
        e.HttpStatusCode = HttpStatusCode.NotFound;
        e.Error = new Google.Apis.Requests.RequestError { Code = 404, Message = message };
        return e;
    }

    /// <summary>The connector's private capped download stream, created through reflection.</summary>
    private static Stream NewCappedStream()
    {
        var type = typeof(Connector).GetNestedType("CappedDownloadStream", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return (Stream)Activator.CreateInstance(type!, nonPublic: true)!;
    }

    // ---- O1-O7: required names are validated before any client/network ---------------

    [Fact]
    public void O1_SignedUrl_rejects_an_empty_object_name()
    {
        foreach (var objectName in new string?[] { "", null })
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", objectName!, 5, out _, "Download"));
            Assert.Contains("ObjectName", ex.Message);
        }
    }

    [Fact]
    public void O2_SignedUrl_rejects_an_empty_bucket_name()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(TestSupport.Auth(), "", "a.txt", 5, out _, "Download"));
        Assert.Contains("BucketName", ex.Message);
    }

    [Fact]
    public void O3_SignedUrl_Download_does_not_sign_a_content_type()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/report.pdf", 5, out var url, "Download", "application/pdf");
        Assert.DoesNotContain("content-type", url.ToLowerInvariant());
    }

    [Fact]
    public void O4_SignedUrl_Upload_signs_a_content_type()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/new.pdf", 5, out var url, "Upload", "application/pdf");
        Assert.Contains("content-type", url.ToLowerInvariant());
    }

    [Fact]
    public void O5_SignedUrl_rejects_a_content_type_with_line_breaks_for_every_operation()
    {
        const string injected = "text/plain\r\nx-goog-acl: public-read";
        foreach (var operation in new[] { "Upload", "Download" })
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/a.txt", 5, out _, operation, injected));
            Assert.Contains("ContentType", ex.Message);
        }
    }

    [Fact]
    public void O6_Move_onto_itself_is_rejected_before_any_call()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Move(TestSupport.Auth(), "b", "o", "b", "o"));
        Assert.Contains("same object", ex.Message);
    }

    [Fact]
    public void O7_required_names_are_rejected_by_object_and_bucket_actions()
    {
        var upload = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Upload(TestSupport.Auth(), "", "o.txt", new GcsFile { Content = [1], ContentType = "text/plain" }, []));
        Assert.Contains("BucketName", upload.Message);

        var download = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Download(TestSupport.Auth(), "test-bucket", "", out _));
        Assert.Contains("ObjectName", download.Message);

        var copy = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Copy(TestSupport.Auth(), "b1", "o.txt", "b2", ""));
        Assert.Contains("DestinationObjectName", copy.Message);

        var delete = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Delete(TestSupport.Auth(), "  ", "o.txt"));
        Assert.Contains("BucketName", delete.Message);

        var create = Assert.Throws<ArgumentException>(() =>
            _sut.Bucket_Create(TestSupport.Auth(), "", "US"));
        Assert.Contains("BucketName", create.Message);
    }

    // ---- O8-O9: timestamps -------------------------------------------------------------

    [Fact]
    public void O8_ParseTimestamp_normalizes_every_form_to_utc()
    {
        var expected = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        foreach (var raw in new[] { "2026-10-28T12:00:00Z", "2026-10-28T13:00:00+01:00" })
        {
            var parsed = Connector.ParseTimestamp(raw);
            Assert.Equal(expected, parsed);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        }

        // No offset at all: UTC, never the server's local time.
        var withoutOffset = Connector.ParseTimestamp("2026-10-28T12:00:00");
        Assert.Equal(expected, withoutOffset);
        Assert.Equal(DateTimeKind.Utc, withoutOffset.Kind);
    }

    [Fact]
    public void O9_ParseTimestamp_returns_the_OutSystems_null_date_when_missing_or_invalid()
    {
        var nullDate = new DateTime(1900, 1, 1);
        Assert.Equal(nullDate, Connector.ParseTimestamp(""));
        Assert.Equal(nullDate, Connector.ParseTimestamp(null));
        Assert.Equal(nullDate, Connector.ParseTimestamp("garbage"));
    }

    // ---- O10-O11: error classification -------------------------------------------------

    [Fact]
    public void O10_IsBucketNotFound_tells_object_and_bucket_404s_apart()
    {
        Assert.False(Connector.IsBucketNotFound(NotFound("No such object: acme-bucket/x.txt")));
        Assert.True(Connector.IsBucketNotFound(NotFound("The specified bucket does not exist.")));
        Assert.False(Connector.IsBucketNotFound(NotFound("Not Found")));
    }

    [Fact]
    public void O11_TranslateException_explains_network_and_timeout_failures()
    {
        var unreachable = Connector.TranslateException(new HttpRequestException("boom"), TestSupport.Auth(), "b", "o");
        Assert.StartsWith("Could not reach Google Cloud", unreachable.Message);

        var timeout = Connector.TranslateException(new TaskCanceledException("slow"), TestSupport.Auth(), "b", "o");
        Assert.Contains("timed out", timeout.Message);
        Assert.Contains("80 seconds", timeout.Message);
    }

    // ---- O12: the download cap on every write path --------------------------------------

    [Fact]
    public void O12_capped_stream_accepts_exactly_the_cap_and_rejects_one_more_byte_on_every_write_path()
    {
        const int max = Connector.MaxDownloadBytes;
        Assert.Equal(5_767_168, max);

        var paths = new (string Name, Action<Stream> WriteExactlyTheCap, Action<Stream> WriteOneByteOver)[]
        {
            ("Write(byte[],int,int)",
                s => s.Write(new byte[max], 0, max),
                s => s.Write(new byte[1], 0, 1)),
            ("Write(ReadOnlySpan<byte>)",
                s => s.Write(new byte[max]),
                s => s.Write(new byte[1])),
            ("WriteByte",
                s => { for (int i = 0; i < max; i++) s.WriteByte(0x41); },
                s => s.WriteByte(0x41)),
            ("WriteAsync(byte[],int,int,CancellationToken)",
                s => s.WriteAsync(new byte[max], 0, max, CancellationToken.None).GetAwaiter().GetResult(),
                s => s.WriteAsync(new byte[1], 0, 1, CancellationToken.None).GetAwaiter().GetResult()),
            ("WriteAsync(ReadOnlyMemory<byte>,CancellationToken)",
                s => s.WriteAsync(new ReadOnlyMemory<byte>(new byte[max]), CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                s => s.WriteAsync(new ReadOnlyMemory<byte>(new byte[1]), CancellationToken.None).AsTask().GetAwaiter().GetResult())
        };

        foreach (var path in paths)
        {
            var stream = NewCappedStream();
            path.WriteExactlyTheCap(stream);
            Assert.Equal(max, stream.Length);

            // One more byte on the same, now full, stream must be refused and not stored.
            var ex = Record.Exception(() => path.WriteOneByteOver(stream));
            Assert.True(ex != null, $"{path.Name}: writing past the cap did not throw");
            Assert.Equal("DownloadTooLargeException", ex!.GetType().Name);
            Assert.Equal(max, stream.Length);
        }
    }

    // ---- E11: the update carries both preconditions and explains a 412 --------------------
    // fake-gcs-server 1.54.0 silently accepts a stale ifGenerationMatch/ifMetagenerationMatch on
    // an object update (verified by experiment), so the race cannot be reproduced there. The
    // in-process fake Storage API records the exact update request instead.

    [Fact]
    public void E11_UpdateMetadata_sends_generation_and_metageneration_preconditions_and_explains_a_412()
    {
        var savedEmulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", null);
        var backend = new FakeFederationBackend();
        var previousFactory = Connector.HttpClientFactoryOverride;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(backend);
        var auth = FederationAuth();
        try
        {
            backend.Objects["test-bucket-alpha/docs/a.txt"] = new FakeFederationBackend.FakeObject(Generation: 100, Metageneration: 7);

            _sut.Object_UpdateMetadata(auth, "test-bucket-alpha", "docs/a.txt", "text/csv", "", "", "", []);

            var update = Assert.Single(backend.Requests, r =>
                r.Method != HttpMethod.Get
                && r.Uri.AbsolutePath.Contains("/storage/v1/b/test-bucket-alpha/o/", StringComparison.Ordinal));
            Assert.Equal(HttpMethod.Put, update.Method);
            var query = System.Web.HttpUtility.ParseQueryString(update.Uri.Query);
            Assert.Equal("100", query["ifGenerationMatch"]);
            Assert.Equal("7", query["ifMetagenerationMatch"]);

            backend.FailObjectUpdateWithPrecondition = true;
            var ex = Assert.ThrowsAny<Exception>(() =>
                _sut.Object_UpdateMetadata(auth, "test-bucket-alpha", "docs/a.txt", "text/csv", "", "", "", []));
            Assert.Contains("changed by another request", ex.Message);
            Assert.Contains("docs/a.txt", ex.Message);
            Assert.Contains("test-bucket-alpha", ex.Message);
        }
        finally
        {
            Connector.HttpClientFactoryOverride = previousFactory;
            Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", savedEmulatorHost);
        }
    }

    /// <summary>Workload Identity Federation credentials routed to <see cref="FakeFederationBackend"/>.</summary>
    private static Authentication FederationAuth() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov",
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
        ClientId = "my-client",
        ClientSecret = "my-secret"
    };
}
