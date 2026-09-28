using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// End-to-end Workload Identity Federation against REAL Google Cloud, with no secrets anywhere.
/// GitHub Actions is the identity provider: the workflow's OIDC token is exchanged with Google STS,
/// the connector impersonates the sandbox service account, and calls real Storage / signBlob.
///
/// Runs only inside GitHub Actions with 'id-token: write' and the GCP_* repository variables;
/// everywhere else it skips. Trait "Live" lets CI run it in its own job.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveFederationTests : IDisposable
{
    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly string? _savedEmulatorHost;

    private static readonly string? RequestUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
    private static readonly string? RequestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
    private static readonly string? Provider = Environment.GetEnvironmentVariable("GCP_WIF_PROVIDER");
    private static readonly string? ServiceAccount = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT");
    private static readonly string? ProjectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID");

    public LiveFederationTests()
    {
        _savedEmulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", null);
        Connector.HttpClientFactoryOverride = null; // real Google endpoints only
    }

    public void Dispose() => Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", _savedEmulatorHost);

    private static void RequireLiveEnvironment() =>
        Skip.If(string.IsNullOrEmpty(RequestUrl) || string.IsNullOrEmpty(RequestToken)
                || string.IsNullOrEmpty(Provider) || string.IsNullOrEmpty(ServiceAccount),
            "Live federation tests run only in GitHub Actions with 'id-token: write' and the GCP_* repository variables.");

    /// <summary>Requests a GitHub Actions OIDC token whose audience is the workload identity provider.</summary>
    private static async Task<string> GetGitHubOidcTokenAsync()
    {
        string audience = "https:" + Connector.NormalizeWorkloadIdentityProvider(Provider!);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RequestUrl}&audience={Uri.EscapeDataString(audience)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequestToken);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("value").GetString()!;
    }

    private static async Task<Authentication> FederatedAuthAsync() => new()
    {
        ProjectId = ProjectId ?? string.Empty,
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider!,
        ServiceAccountEmail = ServiceAccount!,
        SubjectToken = await GetGitHubOidcTokenAsync()
    };

    [SkippableFact]
    public async Task Google_accepts_the_federated_identity_for_storage_calls()
    {
        RequireLiveEnvironment();
        var auth = await FederatedAuthAsync();

        // A bucket that cannot exist. A 404 (Exists = false) proves the whole chain worked: GitHub
        // token -> STS -> impersonated service account -> Storage. A broken chain throws 401/403.
        _sut.Bucket_Exists(auth, $"wif-probe-{Guid.NewGuid():N}", out var exists);
        Assert.False(exists);
    }

    [SkippableFact]
    public async Task Signed_url_signature_verifies_against_the_service_accounts_published_certificate()
    {
        RequireLiveEnvironment();
        var auth = await FederatedAuthAsync();

        _sut.Object_GetSignedUrl(auth, "wif-probe-bucket", "probe/report.pdf", 15, out var url, "Download");

        Assert.Contains(Uri.EscapeDataString(ServiceAccount!), url); // X-Goog-Credential names the service account
        var certificates = await GetServiceAccountCertificatesAsync(ServiceAccount!);
        Assert.True(VerifyV4SignedUrl(url, certificates),
            "The signed URL's signature does not verify against the service account's public certificates.");
    }

    /// <summary>Google publishes every service account's public signing certificates (no auth needed).</summary>
    private static async Task<List<X509Certificate2>> GetServiceAccountCertificatesAsync(string serviceAccount)
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"https://www.googleapis.com/service_accounts/v1/metadata/x509/{Uri.EscapeDataString(serviceAccount)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => X509Certificate2.CreateFromPem(p.Value.GetString()!)).ToList();
    }

    /// <summary>
    /// Recomputes the V4 string-to-sign from the URL itself and verifies the RSA-SHA256 signature,
    /// which is exactly what Google Cloud Storage does when the URL is used.
    /// </summary>
    internal static bool VerifyV4SignedUrl(string url, IReadOnlyCollection<X509Certificate2> certificates)
    {
        var uri = new Uri(url);
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        string Value(string name) => Uri.UnescapeDataString(pairs.First(p => p.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1]);

        string canonicalQuery = string.Join("&", pairs
            .Where(p => !p.StartsWith("X-Goog-Signature=", StringComparison.Ordinal))
            .OrderBy(p => p.Split('=')[0], StringComparer.Ordinal));
        string credential = Value("X-Goog-Credential");                 // SA/DATE/auto/storage/goog4_request
        string scope = credential.Substring(credential.IndexOf('/') + 1);
        string canonicalRequest = string.Join("\n",
            "GET", uri.AbsolutePath, canonicalQuery, $"host:{uri.Host}\n", Value("X-Goog-SignedHeaders"), "UNSIGNED-PAYLOAD");
        string stringToSign = string.Join("\n",
            "GOOG4-RSA-SHA256", Value("X-Goog-Date"), scope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());

        byte[] signature = Convert.FromHexString(Value("X-Goog-Signature"));
        byte[] data = Encoding.UTF8.GetBytes(stringToSign);
        return certificates.Any(c => c.GetRSAPublicKey()!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

/// <summary>
/// Proves the live test's signature verifier is correct, offline: a URL signed locally with the
/// throwaway test key must verify, and a tampered one must not. So if the live test ever fails,
/// the cause is the federation chain, not the verifier.
/// </summary>
public sealed class SignedUrlVerifierTests
{
    private static X509Certificate2 TestKeyCertificate()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(TestSupport.PrivateKeyPem);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert)); // public part only
    }

    [Fact]
    public void Verifier_accepts_a_genuine_v4_signed_url_and_rejects_a_tampered_one()
    {
        IGoogleCloudStorage sut = new Connector();
        sut.Object_GetSignedUrl(TestSupport.Auth(), "my-bucket", "folder/report 2025.pdf", 15, out var url, "Download");
        var certs = new[] { TestKeyCertificate() };

        Assert.True(LiveFederationTests.VerifyV4SignedUrl(url, certs));
        Assert.False(LiveFederationTests.VerifyV4SignedUrl(url.Replace("my-bucket", "other-bucket"), certs));
    }
}
