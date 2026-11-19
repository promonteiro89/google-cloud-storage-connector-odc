using System.Reflection;
using System.Text;
using System.Text.Json;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// The tokens the connector uses for Storage are limited to Storage (devstorage.full_control), so a
/// token in memory or in a dump cannot reach other Google Cloud APIs. Only the federated source token
/// keeps a broader scope, because it calls IAM Credentials (generateAccessToken, signBlob).
/// </summary>
public sealed class ScopeTests : IDisposable
{
    private const string StorageScope = "https://www.googleapis.com/auth/devstorage.full_control";
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly string? _savedEmulatorHost;

    public ScopeTests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = null;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
    }

    private static string Unique(string domain) => $"sa-{Guid.NewGuid():N}"[..20] + domain;

    private FakeFederationBackend.Recorded Single(string urlPart) =>
        Assert.Single(_backend.Requests, r => r.Uri.ToString().Contains(urlPart, StringComparison.Ordinal));

    private static JsonElement JwtPayload(string jwt)
    {
        string part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))).RootElement;
    }

    [Fact]
    public async Task S1_key_sign_in_requests_the_storage_scope_only()
    {
        var auth = new Authentication
        {
            ProjectId = TestSupport.ProjectId,
            ClientEmail = Unique("@fake-project.iam.gserviceaccount.com"),
            PrivateKey = TestSupport.PrivateKeyPem
        };

        // Ask the connector's own key credential for a token; the request goes to the fake, never to Google.
        var create = typeof(Connector).GetMethod("CreateServiceAccountCredential", BindingFlags.NonPublic | BindingFlags.Static)!;
        var credential = (Google.Apis.Auth.OAuth2.ServiceAccountCredential)create.Invoke(null, [auth])!;
        await credential.GetAccessTokenForRequestAsync();

        var token = Single("https://oauth2.googleapis.com/token");
        string scope = JwtPayload(token.Form["assertion"]).GetProperty("scope").GetString()!;
        Assert.Equal(StorageScope, scope);
        Assert.DoesNotContain("cloud-platform", scope);
    }

    [Fact]
    public void S2_impersonated_token_is_storage_only_and_the_federated_source_is_not()
    {
        var auth = new Authentication
        {
            ProjectId = "proj",
            AuthenticationMethod = "WorkloadIdentityFederation",
            WorkloadIdentityProvider = Provider,
            ServiceAccountEmail = Unique("@proj.iam.gserviceaccount.com"),
            TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
            ClientId = "my-client",
            ClientSecret = "my-secret",
            Scope = "api://gcs-connector/.default"
        };

        _sut.Object_Exists(auth, "test-bucket-alpha", "docs/missing.txt", out _);

        var generate = Single(":generateAccessToken");
        var scopes = JsonDocument.Parse(generate.Body).RootElement.GetProperty("scope").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { StorageScope }, scopes);

        // The federated source token is only exchanged for IAM use (Google's library re-scopes it to iam for
        // generateAccessToken and signBlob), never for Storage.
        var sts = Single("https://sts.googleapis.com/v1/token");
        Assert.DoesNotContain(StorageScope, sts.Body);
        Assert.DoesNotContain(Uri.EscapeDataString(StorageScope), sts.Body);
    }
}
