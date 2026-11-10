using Google.Apis.Http;
using System.Net;
using System.Text;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using GcsFile = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.BinaryContent;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests for the 2.0.2 message hardening: bucket-name validation with a whole-string regex,
/// cleaning of control/format/separator characters and lone surrogates, surrogate-safe truncation,
/// File name derivation, printable-ASCII ContentType validation, raw response bodies in error details
/// and the "not valid JSON" message for a 2xx answer that is not JSON. Offline: every request goes to
/// <see cref="FakeFederationBackend"/>, and no test depends on the code's own output for an expected
/// value.
/// </summary>
public sealed class Messages202Tests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan[] _savedRetryDelays = Connector.TransferRetryDelays;

    public Messages202Tests()
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

    private static HttpResponseMessage Text(HttpStatusCode status, string mediaType, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage MinimalObjectJson() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"kind":"storage#object","bucket":"b","name":"a.txt","size":"1","contentType":"text/plain","generation":"1","metageneration":"1"}""",
                Encoding.UTF8,
                "application/json")
        };

    /// <summary>An ODC File reference good enough for the name/type checks, which never open it.</summary>
    internal sealed class StubFile : IOSFile
    {
        public string Key { get; set; } = "storage-key-1";
        public string Version { get; set; } = "v1";
        public string ContentType { get; set; } = "text/plain";
        public long Size { get; set; } = 3;
    }

    // ---- M1-M8: Clean ----------------------------------------------------------------

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\nb")]
    [InlineData("a\u0085b")]
    public void M1_control_format_and_separator_characters_become_one_space(string input)
    {
        Assert.Equal("a b", Connector.Clean(input));
    }

    [Fact]
    public void M2_a_supplementary_plane_format_character_becomes_one_space()
    {
        // U+E0001 LANGUAGE TAG, which is a single Rune but two UTF-16 units.
        Assert.Equal("a b", Connector.Clean("a\U000E0001b"));
    }

    [Fact]
    public void M3_a_lone_high_surrogate_becomes_one_space()
    {
        Assert.Equal("a b", Connector.Clean("a" + '\uD800' + "b"));
    }

    [Fact]
    public void M3_a_lone_low_surrogate_becomes_one_space()
    {
        Assert.Equal("a b", Connector.Clean("a" + '\uDC00' + "b"));
    }

    [Fact]
    public void M4_a_surrogate_pair_is_left_alone()
    {
        Assert.Equal("a\U0001F600b", Connector.Clean("a\U0001F600b"));
    }

    [Fact]
    public void M5_null_and_empty_clean_to_empty()
    {
        Assert.Equal("", Connector.Clean(null));
        Assert.Equal("", Connector.Clean(""));
    }

    [Fact]
    public void M6_text_longer_than_500_units_is_cut_at_500_with_an_ellipsis()
    {
        Assert.Equal(new string('x', 500) + "...", Connector.Clean(new string('x', 600)));
    }

    [Fact]
    public void M7_the_cut_never_splits_a_surrogate_pair()
    {
        var input = new string('x', 499) + "\U0001F600" + "yyy";

        Assert.Equal(new string('x', 499) + "...", Connector.Clean(input));
    }

    [Fact]
    public void M8_the_max_length_is_honoured()
    {
        Assert.Equal("abc...", Connector.Clean("abcdef", 3));
    }

    // ---- M9: FileNameFor -------------------------------------------------------------

    public static IEnumerable<object[]> FileNames() => new[]
    {
        new object[] { "a/b/c.pdf", "c.pdf" },
        new object[] { "dir/", "download" },
        new object[] { "x/na\nme.txt", "na me.txt" },
        new object[] { "x/\u202E", "download" },
        new object[] { "a/..", "download" },
        new object[] { "a/.", "download" },
        new object[] { "..", "download" },
        new object[] { "x/" + new string('n', 300), new string('n', 200) },
        new object[] { "x/" + new string('n', 199) + "\U0001F600", new string('n', 199) }
    };

    [Theory]
    [MemberData(nameof(FileNames))]
    public void M9_downloaded_file_names_are_the_cleaned_last_part_cut_at_200(string objectName, string expected)
    {
        Assert.Equal(expected, Connector.FileNameFor(objectName));
    }

    // ---- M10: every bucket-taking action rejects a bad bucket name -------------------

    [Fact]
    public void M10_every_action_rejects_a_bucket_name_with_a_line_break_before_any_request()
    {
        const string bad = "my\nbucket";
        var oneChange = new List<MetadataEntry> { new() { Key = "k", Value = "v" } };
        var content = new GcsFile { Content = new byte[] { 1 }, ContentType = "text/plain" };

        var calls = new (string Label, Action<Authentication> Call)[]
        {
            ("Object_Upload", a => _sut.Object_Upload(a, bad, "a.txt", new StubFile(), "", Array.Empty<MetadataEntry>())),
            ("Object_Download", a => _sut.Object_Download(a, bad, "a.txt", out _)),
            ("Object_UploadBinary", a => _sut.Object_UploadBinary(a, bad, "a.txt", content, Array.Empty<MetadataEntry>())),
            ("Object_DownloadBinary", a => _sut.Object_DownloadBinary(a, bad, "a.txt", out _)),
            ("Object_List", a => _sut.Object_List(a, bad, "", 0, "", "", out _, out _, out _)),
            ("Object_Exists", a => _sut.Object_Exists(a, bad, "a.txt", out _)),
            ("Object_GetMetadata", a => _sut.Object_GetMetadata(a, bad, "a.txt", out _, out _, out _)),
            ("Object_Delete", a => _sut.Object_Delete(a, bad, "a.txt")),
            ("Object_Copy source", a => _sut.Object_Copy(a, bad, "src.txt", "dst-bucket", "dst.txt")),
            ("Object_Copy destination", a => _sut.Object_Copy(a, "src-bucket", "src.txt", bad, "dst.txt")),
            ("Object_Move source", a => _sut.Object_Move(a, bad, "src.txt", "dst-bucket", "dst.txt")),
            ("Object_Move destination", a => _sut.Object_Move(a, "src-bucket", "src.txt", bad, "dst.txt")),
            ("Object_UpdateMetadata", a => _sut.Object_UpdateMetadata(a, bad, "a.txt", "text/csv", "", "", "", oneChange)),
            ("Object_DeleteByPrefix", a => _sut.Object_DeleteByPrefix(a, bad, "pfx/", out _)),
            ("Object_GetSignedUrl", a => _sut.Object_GetSignedUrl(a, bad, "a.txt", 5, out _)),
            ("Bucket_Create", a => _sut.Bucket_Create(a, bad, "US")),
            ("Bucket_Delete", a => _sut.Bucket_Delete(a, bad)),
            ("Bucket_Exists", a => _sut.Bucket_Exists(a, bad, out _))
        };

        foreach (var (label, call) in calls)
        {
            _backend.Requests.Clear();

            var ex = Assert.Throws<ArgumentException>(() => call(Federated()));

            Assert.True(ex.Message.Contains("is not a valid bucket name", StringComparison.Ordinal), $"{label}: {ex.Message}");
            Assert.DoesNotContain('\n', ex.Message);
            Assert.False(ex.Message.StartsWith("Invalid bucket name", StringComparison.Ordinal), label);
            Assert.True(_backend.Requests.Count == 0, $"{label} sent {_backend.Requests.Count} request(s)");
        }
    }

    // ---- M11: other invalid bucket names --------------------------------------------

    [Fact]
    public void M11_other_invalid_bucket_names_are_rejected_without_control_or_override_characters()
    {
        var names = new[]
        {
            "bucket\n",
            ".",
            "..",
            "-ab",
            "ab-",
            ".ab",
            "ab.",
            "Bucket",
            "b\u202Eevil",
            new string('a', 223),
            "my bucket"
        };

        foreach (var bucket in names)
        {
            _backend.Requests.Clear();

            var ex = Assert.Throws<ArgumentException>(() => _sut.Object_Exists(Federated(), bucket, "a.txt", out _));

            Assert.Contains("is not a valid bucket name", ex.Message);
            Assert.DoesNotContain(ex.Message, c => char.IsControl(c));
            Assert.DoesNotContain('\u202E', ex.Message);
            Assert.Empty(_backend.Requests);
        }
    }

    [Fact]
    public void M12_signed_url_with_a_bad_bucket_name_is_rejected_in_service_account_key_mode()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(TestSupport.Auth(), "bucket\n", "a.txt", 5, out _));

        Assert.Contains("is not a valid bucket name", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    // ---- M13-M14: valid and empty bucket names ---------------------------------------

    [Theory]
    [InlineData("my-bucket")]
    [InlineData("a.b_c-1")]
    [InlineData("a")]
    public void M13_valid_bucket_names_reach_the_fake(string bucket)
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? MinimalObjectJson()
                : null;

        _sut.Object_Exists(Federated(), bucket, "a.txt", out var exists);

        Assert.True(exists);
    }

    [Fact]
    public void M13_a_222_character_bucket_name_is_valid()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? MinimalObjectJson()
                : null;

        _sut.Object_Exists(Federated(), new string('a', 222), "a.txt", out var exists);

        Assert.True(exists);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void M14_an_empty_bucket_name_keeps_its_own_message(string bucket)
    {
        var ex = Assert.Throws<ArgumentException>(() => _sut.Object_Exists(Federated(), bucket, "a.txt", out _));

        Assert.Equal("BucketName cannot be empty.", ex.Message);
    }

    // ---- M15-M17: ContentType --------------------------------------------------------

    [Fact]
    public void M15_a_non_ascii_content_type_is_rejected_before_any_request()
    {
        const string badType = "text/plain\u00E9";

        var calls = new (string Label, Action Call)[]
        {
            ("Object_Upload explicit", () => _sut.Object_Upload(Federated(), "my-bucket", "a.txt", new StubFile(), badType, Array.Empty<MetadataEntry>())),
            ("Object_Upload file's own", () => _sut.Object_Upload(Federated(), "my-bucket", "a.txt", new StubFile { ContentType = badType }, "", Array.Empty<MetadataEntry>())),
            ("Object_UploadBinary", () => _sut.Object_UploadBinary(Federated(), "my-bucket", "a.txt", new GcsFile { Content = new byte[] { 1 }, ContentType = badType }, Array.Empty<MetadataEntry>())),
            ("Object_GetSignedUrl Upload", () => _sut.Object_GetSignedUrl(Federated(), "my-bucket", "a.txt", 5, out _, "Upload", badType))
        };

        foreach (var (label, call) in calls)
        {
            _backend.Requests.Clear();

            var ex = Assert.Throws<ArgumentException>(call);

            Assert.True(ex.Message.StartsWith("ContentType can only contain printable ASCII", StringComparison.Ordinal), $"{label}: {ex.Message}");
            Assert.True(_backend.Requests.Count == 0, $"{label} sent {_backend.Requests.Count} request(s)");
        }
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    public void M16_control_characters_in_a_content_type_are_an_ArgumentException_not_a_FormatException(string contentType)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_UploadBinary(Federated(), "my-bucket", "a.txt", new GcsFile { Content = new byte[] { 1 }, ContentType = contentType }, Array.Empty<MetadataEntry>()));

        Assert.StartsWith("ContentType can only contain printable ASCII", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void M17_printable_or_empty_content_types_are_accepted_and_the_upload_reaches_the_fake()
    {
        var contentTypes = new[] { "text/plain; charset=utf-8", "", null! };

        for (int i = 0; i < contentTypes.Length; i++)
        {
            var name = $"ok-{i}.txt";
            var bytes = new byte[] { 1, 2, 3 };

            var exception = Record.Exception(() =>
                _sut.Object_UploadBinary(Federated(), "my-bucket", name, new GcsFile { Content = bytes, ContentType = contentTypes[i] }, Array.Empty<MetadataEntry>()));

            Assert.Null(exception);
            Assert.Equal(bytes, _backend.StoredObjects[$"my-bucket/{name}"]);
        }
    }

    // ---- M18-M20: raw error bodies and an ambiguous 404 -------------------------------

    [Fact]
    public void M18_a_plain_text_404_names_the_missing_object_and_probes_no_bucket()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? Text(HttpStatusCode.NotFound, "text/plain", "No such object: my-bucket/a.txt")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), "my-bucket", "a.txt", out _));

        Assert.StartsWith("Object 'a.txt' was not found in bucket 'my-bucket'", ex.Message);
        Assert.Contains("No such object: my-bucket/a.txt", ex.Message);

        var bucketLookups = _backend.Requests.Where(r =>
            r.Method == HttpMethod.Get
            && r.Uri.AbsolutePath.EndsWith("/b/my-bucket", StringComparison.Ordinal)
            && !r.Uri.AbsolutePath.Contains("/o/", StringComparison.Ordinal)).ToList();
        Assert.Empty(bucketLookups);
    }

    [Fact]
    public void M19_a_plain_text_403_error_body_is_shown_instead_of_No_error_message_was_specified()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? Text(HttpStatusCode.Forbidden, "text/plain", "Forbidden by proxy rule 17")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), "my-bucket", "a.txt", out _));

        Assert.Contains("Forbidden by proxy rule 17", ex.Message);
        Assert.DoesNotContain("No error message was specified", ex.Message);
    }

    [Fact]
    public void M20_a_404_with_an_empty_body_and_an_existing_bucket_means_the_object_is_missing()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
                : null;

        _sut.Object_Exists(Federated(), "my-bucket", "a.txt", out var exists);

        Assert.False(exists);
    }

    // ---- M21: a 2xx answer that is not JSON ------------------------------------------

    [Fact]
    public void M21_a_200_html_answer_is_reported_as_not_valid_json()
    {
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? Text(HttpStatusCode.OK, "text/html", "<html>Sign in</html>")
                : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_GetMetadata(Federated(), "my-bucket", "a.txt", out _, out _, out _));

        Assert.Contains("not valid JSON", ex.Message);
        Assert.DoesNotContain("an input is invalid", ex.Message);
    }

    // ---- M22-M28: fixes from the second independent review ---------------------------

    [Theory]
    [InlineData("x Basic dXNlcjpwYXNzd29yZDEyMw== y", "x Basic *** y")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz-0123456789 end", "Authorization: Bearer *** end")]
    [InlineData("token ya29.a0AfH6SMBx-abc_DEF.123 end", "token *** end")]
    [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ3b3JrbG9hZCJ9.c2lnbmF0dXJl was refused", "token *** was refused")]
    [InlineData("eyJ is how a JWT starts", "eyJ is how a JWT starts")]
    [InlineData("Basic authentication is required", "Basic authentication is required")]
    [InlineData("Bearer token missing", "Bearer token missing")]
    public void M22_text_shaped_like_a_credential_is_masked_and_plain_words_are_not(string input, string expected)
    {
        Assert.Equal(expected, Connector.Clean(input));
    }

    [Fact]
    public void M23_a_proxy_that_echoes_the_authorization_header_cannot_put_the_token_in_the_message()
    {
        const string token = "sa-impersonated-token-0123456789abcdef";
        _backend.StorageResponseOverride = req =>
            req.Method == HttpMethod.Get && Path(req).Contains("/o/a.txt", StringComparison.Ordinal)
                ? Text(HttpStatusCode.Forbidden, "text/plain", "Blocked by policy. Request headers: Authorization: Bearer " + token)
                : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(Federated(), "my-bucket", "a.txt", out _));

        Assert.DoesNotContain(token, ex.Message);
        Assert.Contains("Blocked by policy", ex.Message);
        Assert.Contains("Bearer ***", ex.Message);
    }

    [Fact]
    public void M24_a_huge_text_is_cut_before_it_is_cleaned()
    {
        var huge = new string('x', 5_000_000) + "\u2028";

        // Walking all 5 million characters would allocate megabytes; cutting first allocates a few kilobytes.
        long before = GC.GetAllocatedBytesForCurrentThread();
        string cleaned = Connector.Clean(huge);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new string('x', 500) + "...", cleaned);
        Assert.True(allocated < 200_000, $"allocated {allocated} bytes");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void M25_the_object_names_dot_and_dot_dot_are_rejected_before_any_request(string name)
    {
        var content = new GcsFile { Content = new byte[] { 1 }, ContentType = "text/plain" };
        var calls = new (string Label, Action<Authentication> Call)[]
        {
            ("Object_DownloadBinary", a => _sut.Object_DownloadBinary(a, "my-bucket", name, out _)),
            ("Object_Download", a => _sut.Object_Download(a, "my-bucket", name, out _)),
            ("Object_UploadBinary", a => _sut.Object_UploadBinary(a, "my-bucket", name, content, Array.Empty<MetadataEntry>())),
            ("Object_Exists", a => _sut.Object_Exists(a, "my-bucket", name, out _)),
            ("Object_Delete", a => _sut.Object_Delete(a, "my-bucket", name)),
            ("Object_Copy source", a => _sut.Object_Copy(a, "my-bucket", name, "my-bucket", "dst.txt")),
            ("Object_Copy destination", a => _sut.Object_Copy(a, "my-bucket", "src.txt", "my-bucket", name)),
            ("Object_GetSignedUrl", a => _sut.Object_GetSignedUrl(a, "my-bucket", name, 5, out _))
        };

        foreach (var (label, call) in calls)
        {
            _backend.Requests.Clear();

            var ex = Assert.Throws<ArgumentException>(() => call(Federated()));

            Assert.True(ex.Message.Contains("is not a valid object name", StringComparison.Ordinal), $"{label}: {ex.Message}");
            Assert.True(_backend.Requests.Count == 0, $"{label} sent {_backend.Requests.Count} request(s)");
        }
    }

    [Theory]
    [InlineData("text/html\r\nX-Evil: 1", "", "", "", "ContentType can only contain printable ASCII")]
    [InlineData("", "gzip\nX-Evil: 1", "", "", "ContentEncoding cannot contain line breaks")]
    [InlineData("", "", "attachment\r\nX-Evil: 1", "", "ContentDisposition cannot contain line breaks")]
    [InlineData("", "", "", "no-cache\u0000", "CacheControl cannot contain line breaks")]
    public void M26_update_metadata_rejects_header_values_with_control_characters(string contentType, string contentEncoding, string contentDisposition, string cacheControl, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_UpdateMetadata(Federated(), "my-bucket", "a.txt", contentType, contentEncoding, contentDisposition, cacheControl, Array.Empty<MetadataEntry>()));

        Assert.StartsWith(expected, ex.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void M27_update_metadata_still_accepts_a_non_ascii_content_disposition()
    {
        _backend.Objects["my-bucket/a.txt"] = new FakeFederationBackend.FakeObject(7, 1);

        var outcome = Record.Exception(() =>
            _sut.Object_UpdateMetadata(Federated(), "my-bucket", "a.txt", "", "", "attachment; filename=\"relat\u00F3rio.pdf\"", "", Array.Empty<MetadataEntry>()));

        Assert.False(outcome is ArgumentException, outcome?.Message);
        Assert.Contains(_backend.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public void M28_caller_values_are_cleaned_in_messages_beyond_the_not_found_one()
    {
        const string nasty = "a\r\nERROR forged\u202Eb";
        var savedBudget = Connector.DeleteByPrefixBudget;
        var messages = new List<(string Label, string Message)>();
        try
        {
            // Object_Move onto itself: the names are echoed.
            messages.Add(("Move onto itself", Assert.Throws<ArgumentException>(() =>
                _sut.Object_Move(Federated(), "my-bucket", nasty, "my-bucket", nasty)).Message));

            // Bucket_Create with a project Google does not know: the ProjectId is echoed.
            _backend.StorageResponseOverride = req => req.Method == HttpMethod.Post && Path(req) == "/storage/v1/b"
                ? Text(HttpStatusCode.NotFound, "application/json", "{\"error\":{\"code\":404,\"message\":\"Not Found\"}}")
                : null;
            var auth = Federated(); auth.ProjectId = "proj\r\nFAKE\u2028x";
            messages.Add(("Bucket_Create project", Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(auth, "my-bucket", "US")).Message));
            _backend.StorageResponseOverride = null;

            // Object_DeleteByPrefix out of time: the prefix is echoed.
            Connector.DeleteByPrefixBudget = TimeSpan.Zero;
            messages.Add(("DeleteByPrefix budget", Assert.ThrowsAny<Exception>(() =>
                _sut.Object_DeleteByPrefix(Federated(), "my-bucket", nasty, out _)).Message));
        }
        finally
        {
            Connector.DeleteByPrefixBudget = savedBudget;
        }

        foreach (var (label, message) in messages)
        {
            Assert.True(message.Contains("forged", StringComparison.Ordinal) || message.Contains("FAKE", StringComparison.Ordinal), $"{label} does not echo the value: {message}");
            Assert.False(message.Any(c => char.IsControl(c) || c == '\u202E' || c == '\u2028'), $"{label}: {message}");
        }
    }
}
