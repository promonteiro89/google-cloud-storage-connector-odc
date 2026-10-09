using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using File = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;
using MetadataEntry = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.MetadataEntry;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// End-to-end upload integrity (Google.Cloud.Storage.V1 5.0+): the connector sends the CRC32C of
/// the exact bytes in an x-goog-hash header, so Google rejects data corrupted in transit before
/// storing it. Runs against the in-process fake of Google's resumable-upload endpoint. Offline.
/// </summary>
public sealed class UploadIntegrityTests : IDisposable
{
    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly string? _savedEmulatorHost;

    public UploadIntegrityTests()
    {
        _savedEmulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", null);
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = null;
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", _savedEmulatorHost);
    }

    // The HTTP seam is wired into the federated client, so drive the upload through that method.
    private static Authentication Federated() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        SubjectToken = "header.payload.signature"
    };

    private static byte[] SampleBytes(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    [Fact]
    public void Crc32c_reference_matches_the_published_check_value()
    {
        // CRC-32C check value for "123456789" (RFC 3720, appendix B.4): 0xE3069283.
        Assert.Equal(0xE3069283u, Crc32c.Compute("123456789"u8.ToArray()));
    }

    [Fact]
    public void Upload_sends_the_crc32c_of_the_exact_bytes_and_stores_them_unchanged()
    {
        var data = SampleBytes(300_000);
        _sut.Object_Upload(Federated(), "my-bucket", "docs/report.bin",
            new File { Content = data, ContentType = "application/octet-stream" }, Array.Empty<MetadataEntry>());

        var finalPut = _backend.To("/upload/storage/v1/b/my-bucket/o").Last(r => r.Method == HttpMethod.Put);
        Assert.True(finalPut.Headers.TryGetValue("x-goog-hash", out var hash), "The upload did not send an x-goog-hash header.");
        Assert.Contains("crc32c=" + Crc32c.Base64(data), hash);
        Assert.Equal(data, _backend.StoredObjects["my-bucket/docs/report.bin"]);
    }

    [Fact]
    public void Data_corrupted_in_transit_is_rejected_by_google_and_explained()
    {
        _backend.CorruptUploadInTransit = true;

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Upload(Federated(), "my-bucket", "docs/report.bin",
            new File { Content = SampleBytes(50_000), ContentType = "application/octet-stream" }, Array.Empty<MetadataEntry>()));

        Assert.Contains("CRC32C", ex.Message);
        Assert.Contains("nothing was stored", ex.Message);
        Assert.Contains("retry", ex.Message);
        Assert.Empty(_backend.StoredObjects);
    }
}
