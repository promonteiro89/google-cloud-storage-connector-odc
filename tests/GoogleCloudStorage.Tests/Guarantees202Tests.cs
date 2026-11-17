using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Google.Apis.Http;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests for guarantees the documentation makes and no other test proved (found by mutation: with
/// each behaviour broken in the source, the suite still passed). Every expected value comes from the
/// documented behaviour, never from the code's output.
/// </summary>
public sealed class Guarantees202Tests : IDisposable
{
    private const string Bucket = "test-bucket-alpha";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly FileTransferTests.FakeFileStorage _files = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;
    private readonly TimeSpan[] _savedDelays;
    private readonly TimeSpan _savedWindow;
    private readonly TimeSpan _savedBudget;
    private readonly TimeSpan _savedDeleteDeadline;
    private readonly int _savedPageSize;

    public Guarantees202Tests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        _previousFactory = Connector.HttpClientFactoryOverride;
        _savedDelays = Connector.TransferRetryDelays;
        _savedWindow = Connector.TransferRetryWindow;
        _savedBudget = Connector.DeleteByPrefixBudget;
        _savedDeleteDeadline = Connector.DeleteByPrefixDeadline;
        _savedPageSize = Connector.DeleteByPrefixPageSize;
        Connector.EmulatorBaseUri = null;
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
        Connector.TransferRetryDelays = _savedDelays;
        Connector.TransferRetryWindow = _savedWindow;
        Connector.DeleteByPrefixBudget = _savedBudget;
        Connector.DeleteByPrefixDeadline = _savedDeleteDeadline;
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
    private static bool IsMediaGet(HttpRequestMessage r) => r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.RequestUri!.Query)["alt"] == "media";
    private static bool IsRewrite(HttpRequestMessage r) => r.Method == HttpMethod.Post && Path(r).Contains("/rewriteTo/", StringComparison.Ordinal);
    private static bool IsList(HttpRequestMessage r) => r.Method == HttpMethod.Get && Path(r).EndsWith($"/b/{Bucket}/o", StringComparison.Ordinal);
    private static bool IsObjectGet(HttpRequestMessage r, string bucket, string name) =>
        r.Method == HttpMethod.Get && !IsMediaGet(r) && Path(r) == $"/storage/v1/b/{bucket}/o/{name}";

    private static bool IsRecordedDelete(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Delete;
    private static bool IsRecordedMediaGet(FakeFederationBackend.Recorded r) => r.Method == HttpMethod.Get && HttpUtility.ParseQueryString(r.Uri.Query)["alt"] == "media";

    private static byte[] Bytes(int length, int seed = 7)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private void Seed(string name, long generation, long size = 5) =>
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(Generation: generation, Metageneration: 1, Size: size);

    private void StoreInGoogle(string name, byte[] data, string contentType = "application/octet-stream", long generation = 1)
    {
        _backend.StoredObjects[$"{Bucket}/{name}"] = data;
        _backend.Objects[$"{Bucket}/{name}"] = new FakeFederationBackend.FakeObject(generation, 1, contentType, data.Length);
    }

    /// <summary>Answers the first request matching <paramref name="match"/> with <paramref name="status"/>; later ones go through.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> FailFirst(Func<HttpRequestMessage, bool> match, HttpStatusCode status)
    {
        int seen = 0;
        return req => match(req) && Interlocked.Increment(ref seen) == 1 ? JsonError(status, "Transient backend error") : null;
    }

    /// <summary>A media download that serves <paramref name="served"/> with the CRC32C of <paramref name="hashedBytes"/>.</summary>
    private static HttpResponseMessage MediaWithHashOf(HttpRequestMessage request, byte[] served, byte[] hashedBytes)
    {
        var media = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) };
        media.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        media.Headers.TryAddWithoutValidation("x-goog-hash", "crc32c=" + Crc32c.Base64(hashedBytes));
        // The SDK's checksum check is a response-stream interceptor that Google's real handler chain applies;
        // the fake replaces that chain, so feed the served bytes to it here, as the fake's own route does.
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<Func<HttpResponseMessage, StreamInterceptor>>(ConfigurableMessageHandler.ResponseStreamInterceptorProviderKey), out var provider)
            && provider(media) is { } interceptor)
            interceptor(served, 0, served.Length);
        return media;
    }

    private const string ChecksumStart = "The downloaded data did not match the object's checksum";

    // ---- G1-G2: a download is checked against Google's CRC32C -------------------------

    [Fact]
    public void G1_DownloadBinary_checks_the_download_against_googles_crc32c()
    {
        byte[] data = Bytes(1000);
        byte[] other = Bytes(1000, seed: 99);
        _backend.Objects[$"{Bucket}/doc.bin"] = new FakeFederationBackend.FakeObject(1, 1, "application/octet-stream", data.Length);
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? MediaWithHashOf(req, data, other) : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "doc.bin", out _));

        Assert.StartsWith(ChecksumStart, ex.Message);
    }

    [Fact]
    public void G2_Download_checks_the_download_against_googles_crc32c()
    {
        byte[] data = Bytes(1000);
        byte[] other = Bytes(1000, seed: 99);
        _backend.Objects[$"{Bucket}/doc.bin"] = new FakeFederationBackend.FakeObject(1, 1, "application/octet-stream", data.Length);
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? MediaWithHashOf(req, data, other) : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(Federated(), Bucket, "doc.bin", out _));

        Assert.StartsWith(ChecksumStart, ex.Message);
        Assert.Equal(0, _files.Created);
    }

    // ---- G3-G6: Object_DeleteByPrefix -------------------------------------------------

    [Fact]
    public void G3_DeleteByPrefix_stops_between_objects_when_its_time_budget_is_spent()
    {
        Connector.DeleteByPrefixBudget = TimeSpan.FromMilliseconds(300);
        Connector.DeleteByPrefixDeadline = TimeSpan.FromSeconds(30);
        for (int i = 0; i < 10; i++) Seed($"pfx/{i}", 10 + i);
        _backend.StorageResponseOverride = req =>
        {
            if (IsDelete(req)) Thread.Sleep(100);
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(Federated(), Bucket, "pfx/", out _));

        var match = Regex.Match(ex.Message, @"Deleted (\d+) object\(s\) under prefix 'pfx/' before the 0-second time budget");
        Assert.True(match.Success, ex.Message);
        int n = int.Parse(match.Groups[1].Value);
        Assert.InRange(n, 1, 9);
        Assert.Equal(n, _backend.Requests.Count(IsRecordedDelete));
        Assert.Contains("call Object_DeleteByPrefix again with the same prefix", ex.Message);
    }

    [Fact]
    public void G4_DeleteByPrefix_reports_the_progress_so_far_when_a_delete_fails()
    {
        Seed("pfx/a", 11); Seed("pfx/b", 12); Seed("pfx/c", 13);
        _backend.StorageResponseOverride = req =>
            IsDelete(req) && Path(req).EndsWith("/o/pfx/b", StringComparison.Ordinal) ? JsonError(HttpStatusCode.Forbidden, "Forbidden") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(Federated(), Bucket, "pfx/", out _));

        Assert.StartsWith("Deleted 1 object(s) under prefix 'pfx/', then failed on 'pfx/b':", ex.Message);
        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public void G5_DeleteByPrefix_reports_the_progress_so_far_when_a_later_listing_fails()
    {
        Connector.DeleteByPrefixPageSize = 2;
        for (int i = 1; i <= 4; i++) Seed($"pfx/{i}", 10 + i);
        _backend.StorageResponseOverride = req =>
            IsList(req) && HttpUtility.ParseQueryString(req.RequestUri!.Query)["pageToken"] != null
                ? JsonError(HttpStatusCode.Forbidden, "Forbidden") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(Federated(), Bucket, "pfx/", out _));

        Assert.StartsWith("Deleted 2 object(s) under prefix 'pfx/', then listing the next page failed:", ex.Message);
    }

    [Fact]
    public void G6_DeleteByPrefix_reports_a_failed_first_listing_plainly()
    {
        Seed("pfx/a", 11);
        _backend.StorageResponseOverride = req => IsList(req) ? JsonError(HttpStatusCode.Forbidden, "Forbidden") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_DeleteByPrefix(Federated(), Bucket, "pfx/", out _));

        Assert.StartsWith("Access denied", ex.Message);
        Assert.DoesNotContain("Deleted 0 object(s)", ex.Message);
    }

    // ---- G7: the signed URL is signed for the right method ----------------------------

    [Theory]
    [InlineData("Delete", "DELETE", "GET")]
    [InlineData("Upload", "PUT", "GET")]
    [InlineData("Download", "GET", "DELETE")]
    public void G7_SignedUrl_is_signed_for_the_method_of_its_operation(string operation, string signedMethod, string otherMethod)
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "my-bucket", "docs/a.txt", 15, out var url, operation);

        Assert.True(VerifiesFor(url, signedMethod), $"the signature must verify for {signedMethod}");
        Assert.False(VerifiesFor(url, otherMethod), $"the signature must not verify for {otherMethod}");
    }

    /// <summary>Recomputes the V4 string to sign for <paramref name="method"/> and verifies the signature with the key's public part.</summary>
    private static bool VerifiesFor(string url, string method)
    {
        var uri = new Uri(url);
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        string Value(string name) => Uri.UnescapeDataString(pairs.First(p => p.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1]);

        string canonicalQuery = string.Join("&", pairs
            .Where(p => !p.StartsWith("X-Goog-Signature=", StringComparison.Ordinal))
            .OrderBy(p => p.Split('=')[0], StringComparer.Ordinal));
        string credential = Value("X-Goog-Credential");
        string scope = credential.Substring(credential.IndexOf('/') + 1);
        string canonicalRequest = method + "\n" + uri.AbsolutePath + "\n" + canonicalQuery + "\n"
            + "host:storage.googleapis.com\n" + "\n" + Value("X-Goog-SignedHeaders") + "\n" + "UNSIGNED-PAYLOAD";
        string stringToSign = "GOOG4-RSA-SHA256\n" + Value("X-Goog-Date") + "\n" + scope + "\n"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();

        using var rsa = RSA.Create();
        rsa.ImportFromPem(TestSupport.PrivateKeyPem);
        return rsa.VerifyData(Encoding.UTF8.GetBytes(stringToSign), Convert.FromHexString(Value("X-Goog-Signature")), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // ---- G8-G10, G13-G14: which failures a transfer repeats ---------------------------

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public void G8_DownloadBinary_repeats_each_transient_status(int status)
    {
        byte[] data = Bytes(500);
        StoreInGoogle("doc.bin", data);
        _backend.StorageResponseOverride = FailFirst(IsMediaGet, (HttpStatusCode)status);

        _sut.Object_DownloadBinary(Federated(), Bucket, "doc.bin", out var content);

        Assert.Equal(data, content.Content);
        Assert.Equal(2, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public void G9_Delete_repeats_each_transient_status_on_a_guarded_write(int status)
    {
        Seed("a.txt", 5);
        _backend.StorageResponseOverride = FailFirst(IsDelete, (HttpStatusCode)status);

        _sut.Object_Delete(Federated(), Bucket, "a.txt");

        Assert.False(_backend.Objects.ContainsKey($"{Bucket}/a.txt"));
        Assert.Equal(2, _backend.Requests.Count(IsRecordedDelete));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(409)]
    // 401 is left out on purpose: Google's SDK, not the connector, answers it by refreshing the token
    // and sending the request again.
    public void G10_DownloadBinary_does_not_repeat_other_statuses(int status)
    {
        StoreInGoogle("doc.bin", Bytes(500));
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? JsonError((HttpStatusCode)status, "Refused") : null;

        Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "doc.bin", out _));

        Assert.Equal(1, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void G11_an_unguarded_delete_is_never_repeated()
    {
        Seed("a.txt", 5);
        _backend.StorageResponseOverride = req =>
        {
            if (IsObjectGet(req, Bucket, "a.txt")) return JsonError(HttpStatusCode.Forbidden, "Forbidden");
            if (IsDelete(req)) return JsonError(HttpStatusCode.ServiceUnavailable, "Unavailable");
            return null;
        };

        Assert.ThrowsAny<Exception>(() => _sut.Object_Delete(Federated(), Bucket, "a.txt"));

        var delete = Assert.Single(_backend.Requests, IsRecordedDelete);
        Assert.DoesNotContain("ifGenerationMatch", delete.Uri.Query);
    }

    [Fact]
    public void G12_a_guarded_delete_carries_the_generation_it_read()
    {
        Seed("a.txt", 7);

        _sut.Object_Delete(Federated(), Bucket, "a.txt");

        var delete = Assert.Single(_backend.Requests, IsRecordedDelete);
        Assert.Equal("7", HttpUtility.ParseQueryString(delete.Uri.Query)["ifGenerationMatch"]);
    }

    [Fact]
    public void G13_no_repeat_starts_once_the_retry_window_has_passed()
    {
        Connector.TransferRetryWindow = TimeSpan.FromMilliseconds(100);
        StoreInGoogle("doc.bin", Bytes(500));
        _backend.StorageResponseOverride = req =>
        {
            if (!IsMediaGet(req)) return null;
            Thread.Sleep(300);
            return JsonError(HttpStatusCode.ServiceUnavailable, "Unavailable");
        };

        Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "doc.bin", out _));

        Assert.Equal(1, _backend.Requests.Count(IsRecordedMediaGet));
    }

    [Fact]
    public void G14_a_transfer_makes_at_most_three_attempts()
    {
        StoreInGoogle("doc.bin", Bytes(500));
        _backend.StorageResponseOverride = req => IsMediaGet(req) ? JsonError(HttpStatusCode.ServiceUnavailable, "Unavailable") : null;

        Assert.ThrowsAny<Exception>(() => _sut.Object_DownloadBinary(Federated(), Bucket, "doc.bin", out _));

        Assert.Equal(3, _backend.Requests.Count(IsRecordedMediaGet));
    }

    // ---- G15: every Storage client has the 90-second request timeout ------------------

    private static Google.Cloud.Storage.V1.StorageClient GetStorageClient(Authentication authentication)
    {
        var method = typeof(Connector).GetMethod("GetStorageClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Google.Cloud.Storage.V1.StorageClient)method.Invoke(null, new object[] { authentication })!;
    }

    [Fact]
    public void G15_a_federated_storage_client_has_the_80_second_timeout()
    {
        var client = GetStorageClient(Federated());

        Assert.Equal(TimeSpan.FromSeconds(90), client.Service.HttpClient.Timeout);
    }

    [Fact]
    public void G15_a_service_account_key_storage_client_has_the_80_second_timeout()
    {
        Connector.HttpClientFactoryOverride = null;
        Connector.EmulatorBaseUri = null;
        var auth = TestSupport.Auth(clientEmail: $"g15-{Guid.NewGuid():N}@fake-project.iam.gserviceaccount.com");

        var client = GetStorageClient(auth);

        Assert.Equal(TimeSpan.FromSeconds(90), client.Service.HttpClient.Timeout);
    }

    // ---- G16-G18: a 404 on a copy whose wording does not say what is missing ----------

    [Fact]
    public void G16_Copy_404_with_an_existing_source_names_the_destination_bucket()
    {
        Seed("src.txt", 3);
        _backend.Objects["src-bucket/src.txt"] = new FakeFederationBackend.FakeObject(3, 1);
        _backend.StorageResponseOverride = req => IsRewrite(req) ? JsonError(HttpStatusCode.NotFound, "Not Found") : null;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Copy(Federated(), "src-bucket", "src.txt", "dst-bucket", "dst.txt"));

        Assert.StartsWith("Bucket 'dst-bucket' does not exist", ex.Message);
    }

    [Fact]
    public void G17_Copy_404_with_a_missing_source_names_the_source_object()
    {
        _backend.StorageResponseOverride = req =>
        {
            if (IsRewrite(req)) return JsonError(HttpStatusCode.NotFound, "Not Found");
            if (IsObjectGet(req, "src-bucket", "src.txt")) return JsonError(HttpStatusCode.NotFound, "No such object: src-bucket/src.txt");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Copy(Federated(), "src-bucket", "src.txt", "dst-bucket", "dst.txt"));

        Assert.StartsWith("Object 'src.txt' was not found in bucket 'src-bucket'", ex.Message);
    }

    [Fact]
    public void G18_Copy_404_when_the_source_look_up_fails_names_both_candidates()
    {
        _backend.StorageResponseOverride = req =>
        {
            if (IsRewrite(req)) return JsonError(HttpStatusCode.NotFound, "Not Found");
            if (IsObjectGet(req, "src-bucket", "src.txt")) return JsonError(HttpStatusCode.Forbidden, "Forbidden");
            return null;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Copy(Federated(), "src-bucket", "src.txt", "dst-bucket", "dst.txt"));

        Assert.StartsWith("The source object 'src-bucket/src.txt' or the destination bucket 'dst-bucket' does not exist, and checking which one failed:", ex.Message);
    }

    // ---- G19-G20: an empty upload stores an empty object ------------------------------

    private int UploadSessionRequests() =>
        _backend.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal));

    [Fact]
    public void G19_UploadBinary_of_no_bytes_stores_an_empty_object()
    {
        _sut.Object_UploadBinary(Federated(), Bucket, "empty.txt",
            new BinaryContent { Content = Array.Empty<byte>(), ContentType = "text/plain" }, Array.Empty<MetadataEntry>(), out _);

        Assert.True(_backend.StoredObjects.TryGetValue($"{Bucket}/empty.txt", out var stored));
        Assert.Empty(stored!);
        Assert.True(UploadSessionRequests() < 5, $"{UploadSessionRequests()} requests to the upload session");
    }

    [Fact]
    public void G20_Upload_of_a_0_byte_file_stores_an_empty_object()
    {
        var file = _files.Add(Array.Empty<byte>(), "text/plain");

        _sut.Object_Upload(Federated(), Bucket, "empty.txt", file, "", Array.Empty<MetadataEntry>(), out _);

        Assert.True(_backend.StoredObjects.TryGetValue($"{Bucket}/empty.txt", out var stored));
        Assert.Empty(stored!);
        Assert.True(UploadSessionRequests() < 5, $"{UploadSessionRequests()} requests to the upload session");
    }
}
