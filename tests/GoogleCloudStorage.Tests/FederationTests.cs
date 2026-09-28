using System.Net;
using System.Reflection;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Workload Identity Federation: input validation, backward compatibility of the method switch,
/// cache isolation, and full protocol contract tests against an in-process fake of the identity
/// provider, Google STS, IAM Credentials and Storage. Fully offline.
/// </summary>
public sealed class FederationTests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly string? _savedEmulatorHost;

    public FederationTests()
    {
        // These tests exercise the real Google endpoints (faked); make sure the emulator hook is off.
        _savedEmulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", null);
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = null;
        Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", _savedEmulatorHost);
    }

    /// <summary>A unique service account per test keeps the connector's static caches from leaking between tests.</summary>
    private static string UniqueServiceAccount() => $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com";

    private static Authentication ClientCredentials(string? serviceAccount = null) => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = serviceAccount ?? UniqueServiceAccount(),
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint,
        ClientId = "my-client",
        ClientSecret = "my-secret",
        Scope = "api://gcs-connector/.default"
    };

    private static Authentication SuppliedToken(string token, string? serviceAccount = null) => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = serviceAccount ?? UniqueServiceAccount(),
        SubjectToken = token
    };

    private static readonly MethodInfo GetStorageClientMethod =
        typeof(Connector).GetMethod("GetStorageClient", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object? StorageClientFor(Authentication a)
    {
        try { return GetStorageClientMethod.Invoke(null, [a]); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    // ---- method switch & validation -----------------------------------------------------

    [Fact]
    public void Invalid_method_is_rejected_with_the_valid_values()
    {
        var a = TestSupport.Auth();
        a.AuthenticationMethod = "Kerberos";
        var ex = Assert.Throws<ArgumentException>(() => _sut.Bucket_Exists(a, "b", out _));
        Assert.Contains("WorkloadIdentityFederation", ex.Message);
        Assert.Contains("ServiceAccountKey", ex.Message);
    }

    [Fact]
    public void Explicit_ServiceAccountKey_method_behaves_like_the_default()
    {
        var a = TestSupport.Auth();
        a.AuthenticationMethod = "serviceaccountkey"; // case-insensitive
        _sut.Object_GetSignedUrl(a, "b", "o", 5, out var url, "Download");
        Assert.Contains("X-Goog-Signature", url);
        Assert.Empty(_backend.Requests); // key mode signs locally: no network at all
    }

    [Fact]
    public void ServiceAccountKey_without_key_material_names_the_missing_fields()
    {
        var a = new Authentication { ProjectId = "proj", ClientEmail = "x@proj.iam.gserviceaccount.com" };
        var ex = Assert.Throws<ArgumentException>(() => _sut.Bucket_Exists(a, "b", out _));
        Assert.Contains("missing: PrivateKey", ex.Message);
    }

    [Fact]
    public void Federation_without_required_fields_names_every_missing_field()
    {
        var a = new Authentication { ProjectId = "proj", AuthenticationMethod = "WorkloadIdentityFederation" };
        var ex = Assert.Throws<ArgumentException>(() => _sut.Bucket_Exists(a, "b", out _));
        foreach (var field in new[] { "WorkloadIdentityProvider", "ServiceAccountEmail", "TokenEndpoint", "ClientId", "ClientSecret" })
            Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Federation_with_a_supplied_token_does_not_need_client_credentials()
    {
        Assert.NotNull(StorageClientFor(SuppliedToken("a.b.c"))); // validation passes; no network until first call
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void Malformed_provider_is_rejected_with_the_expected_format()
    {
        var a = ClientCredentials();
        a.WorkloadIdentityProvider = "my-pool";
        var ex = Assert.Throws<ArgumentException>(() => StorageClientFor(a));
        Assert.Contains("workloadIdentityPools", ex.Message);
    }

    [Theory]
    [InlineData("//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x")]
    [InlineData("https://iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x")]
    [InlineData("projects/1/locations/global/workloadIdentityPools/p/providers/x")]
    public void Provider_is_accepted_in_every_common_form(string provider) =>
        Assert.Equal("//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
            Connector.NormalizeWorkloadIdentityProvider(provider));

    [Fact]
    public void Plain_http_token_endpoint_is_rejected_unless_loopback()
    {
        var a = ClientCredentials();
        a.TokenEndpoint = "http://idp.example.com/token";
        var ex = Assert.Throws<ArgumentException>(() => StorageClientFor(a));
        Assert.Contains("https://", ex.Message);

        a.TokenEndpoint = "http://localhost:8080/token";
        Assert.NotNull(StorageClientFor(a));
    }

    [Fact]
    public void Key_and_federation_never_share_a_cached_client_and_federation_is_cached_per_identity()
    {
        var key = StorageClientFor(TestSupport.Auth());
        var sa = UniqueServiceAccount();
        var wif1 = StorageClientFor(ClientCredentials(sa));
        var wif2 = StorageClientFor(ClientCredentials(sa));
        var other = StorageClientFor(ClientCredentials());

        Assert.NotSame(key, wif1);
        Assert.Same(wif1, wif2);
        Assert.NotSame(wif1, other);
    }

    // ---- protocol contract: client credentials -> STS -> impersonation -> Storage --------

    [Fact]
    public void Client_credentials_flow_speaks_the_exact_protocol_end_to_end()
    {
        var a = ClientCredentials();

        _sut.Bucket_Exists(a, "my-bucket", out var exists);
        Assert.True(exists);

        // 1) Identity provider: standard client-credentials grant (client_secret_post).
        var idp = Assert.Single(_backend.To("idp.example.test"));
        Assert.Equal("client_credentials", idp.Form["grant_type"]);
        Assert.Equal("my-client", idp.Form["client_id"]);
        Assert.Equal("my-secret", idp.Form["client_secret"]);
        Assert.Equal("api://gcs-connector/.default", idp.Form["scope"]);

        // 2) Google STS: RFC 8693 token exchange of the provider's JWT.
        var sts = Assert.Single(_backend.To("sts.googleapis.com"));
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", sts.Form["grant_type"]);
        Assert.Equal(Provider, sts.Form["audience"]);
        Assert.Equal(_backend.IdpToken, sts.Form["subject_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:jwt", sts.Form["subject_token_type"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", sts.Form["requested_token_type"]);

        // 3) IAM Credentials: impersonate the service account using the federated token.
        var impersonation = Assert.Single(_backend.To(":generateAccessToken"));
        Assert.True(
            impersonation.Uri.ToString().Contains(a.ServiceAccountEmail, StringComparison.Ordinal)
            || impersonation.Uri.AbsoluteUri.Contains(Uri.EscapeDataString(a.ServiceAccountEmail), StringComparison.Ordinal),
            $"generateAccessToken URL does not target {a.ServiceAccountEmail}: {impersonation.Uri}");
        Assert.Equal($"Bearer {FakeFederationBackend.StsToken}", impersonation.Authorization);

        // 4) Storage is called with the short-lived service account token, never with a key.
        var storage = Assert.Single(_backend.To("storage.googleapis.com"));
        Assert.Equal($"Bearer {FakeFederationBackend.ServiceAccountToken}", storage.Authorization);
    }

    [Fact]
    public void Supplied_token_skips_the_identity_provider_entirely()
    {
        _sut.Bucket_Exists(SuppliedToken("header.payload.signature"), "my-bucket", out var exists);
        Assert.True(exists);
        Assert.Empty(_backend.To("idp.example.test"));
        Assert.Equal("header.payload.signature", Assert.Single(_backend.To("sts.googleapis.com")).Form["subject_token"]);
    }

    [Fact]
    public void Falls_back_to_http_basic_client_auth_when_the_provider_requires_it()
    {
        _backend.IdpRequiresBasicAuth = true;
        _sut.Bucket_Exists(ClientCredentials(), "my-bucket", out var exists);
        Assert.True(exists);

        var attempts = _backend.To("idp.example.test").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Null(attempts[0].Authorization);                    // client_secret_post
        Assert.StartsWith("Basic ", attempts[1].Authorization);    // client_secret_basic
        Assert.False(attempts[1].Form.ContainsKey("client_secret")); // never both methods at once
    }

    [Fact]
    public void Rejected_client_credentials_give_an_actionable_error()
    {
        _backend.IdpRejectsClient = true;
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "my-bucket", out _));
        var root = Innermost(ex, "rejected the client-credentials");
        Assert.Contains("invalid_client", root.Message);
        Assert.Contains("ClientSecret", root.Message);
    }

    [Fact]
    public void A_non_jwt_token_from_the_provider_is_explained()
    {
        _backend.IdpToken = "opaque-reference-token";
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "my-bucket", out _));
        Assert.Contains("not a JWT", Innermost(ex, "not a JWT").Message);
        Assert.Empty(_backend.To("sts.googleapis.com")); // never sent to Google
    }

    [Fact]
    public void A_token_rejected_by_google_sts_is_explained()
    {
        _backend.StsError = """{"error":"unauthorized_client","error_description":"The given credential is rejected by the attribute condition."}""";
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "my-bucket", out _));
        var msg = AllMessages(ex);
        Assert.Contains("attribute condition", msg);
        Assert.Contains("Workload Identity Federation failed", msg);
    }

    // ---- signed URLs via signBlob ----------------------------------------------------------

    [Fact]
    public void Signed_urls_are_signed_by_the_service_account_through_signBlob()
    {
        var a = ClientCredentials();
        _sut.Object_GetSignedUrl(a, "my-bucket", "docs/report.pdf", 15, out var url, "Download");

        var signBlob = Assert.Single(_backend.To(":signBlob"));
        Assert.StartsWith("Bearer ", signBlob.Authorization);
        Assert.Contains(Uri.EscapeDataString(a.ServiceAccountEmail), url);                          // X-Goog-Credential names the SA
        Assert.Contains("X-Goog-Signature=" + Convert.ToHexString(FakeFederationBackend.SignatureBytes).ToLowerInvariant(), url);
    }

    [Fact]
    public void Missing_token_creator_role_on_signBlob_is_explained()
    {
        _backend.SignBlobFailure = HttpStatusCode.Forbidden;
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_GetSignedUrl(ClientCredentials(), "b", "o", 15, out _, "Download"));
        Assert.Contains("Service Account Token Creator", AllMessages(ex));
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static string AllMessages(Exception e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x != null; x = x.InnerException) parts.Add(x.Message);
        if (e is AggregateException agg) parts.AddRange(agg.Flatten().InnerExceptions.Select(i => i.Message));
        return string.Join(" | ", parts);
    }

    private static Exception Innermost(Exception e, string containing)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
            if (x.Message.Contains(containing, StringComparison.Ordinal)) return x;
        throw new Xunit.Sdk.XunitException($"No exception in the chain contains '{containing}'. Chain: {AllMessages(e)}");
    }
}
