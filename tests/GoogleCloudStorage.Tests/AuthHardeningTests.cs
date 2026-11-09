using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Offline tests of the v1.7.2 authentication hardening: token endpoint messages that never echo a
/// secret, sanitized identity provider errors, the 1 MB response bound, the cache cap on insert,
/// the 80-second timeout of the credentials' own HTTP clients, and ClientEmail trimming.
/// </summary>
public sealed class AuthHardeningTests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _previousFactory;
    private readonly string? _savedEmulatorHost;

    public AuthHardeningTests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        Connector.EmulatorBaseUri = null;
        _previousFactory = Connector.HttpClientFactoryOverride;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = _previousFactory;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
    }

    // A unique service account per call keeps the process-wide federated identity cache from
    // serving an identity built by another test.
    private static Authentication ClientCredentials() => new()
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

    private static HttpResponseMessage Raw(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage OAuthError(string? error, string? description) =>
        Raw(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description
        }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value)));

    private string ActionFailure()
    {
        var e = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(ClientCredentials(), "test-bucket-alpha", "a.txt", out _));
        return e.Message;
    }

    // ---- A1: the token endpoint is never echoed unless it is a real http(s) URL ----------

    [Fact]
    public void V1_SchemelessEndpointWithSecretIsNotEchoed()
    {
        var e = Assert.Throws<ArgumentException>(() => Connector.ValidateTokenEndpoint("idp.example.test/token?client_secret=S3CR3T"));
        Assert.DoesNotContain("S3CR3T", e.Message);
        Assert.DoesNotContain("idp.example.test", e.Message);
        Assert.Contains("absolute https://", e.Message);
    }

    [Fact]
    public void V2_UserInfoWithoutSchemeIsNotEchoed()
    {
        var e = Assert.Throws<ArgumentException>(() => Connector.ValidateTokenEndpoint("user:pa55word@idp.example.test/token"));
        Assert.DoesNotContain("pa55word", e.Message);
    }

    [Fact]
    public void V3_HttpsEndpointIsAccepted()
    {
        Connector.ValidateTokenEndpoint("https://idp.example.test/oauth2/token");
    }

    [Fact]
    public void V4_HttpEndpointEchoesOnlySchemeHostAndPath()
    {
        var e = Assert.Throws<ArgumentException>(() => Connector.ValidateTokenEndpoint("http://idp.example.test/token?x=S3CR3T"));
        Assert.Contains("http://idp.example.test/token", e.Message);
        Assert.DoesNotContain("S3CR3T", e.Message);
    }

    // ---- A2: identity provider error fields are sanitized -----------------------------------

    [Fact]
    public void E1_ClientSecretEchoedByProviderIsMasked()
    {
        _backend.IdpResponseOverride = _ => OAuthError("invalid_request", "bad client_secret=my-secret here");
        string message = ActionFailure();
        Assert.DoesNotContain("my-secret", message);
        Assert.Contains("***", message);
    }

    [Fact]
    public void E2_LongErrorIsCut()
    {
        _backend.IdpResponseOverride = _ => OAuthError(new string('x', 5000), null);
        string message = ActionFailure();
        Assert.True(message.Length < 1000, $"message length {message.Length}");
        Assert.Contains("...", message);
    }

    [Fact]
    public void E3_ControlCharactersAreReplaced()
    {
        _backend.IdpResponseOverride = _ => OAuthError("invalid_request", "line1\r\nline2\tend");
        string message = ActionFailure();
        Assert.DoesNotContain("\r", message);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\t", message);
    }

    [Fact]
    public void E4_SecretIsMaskedBeforeTheCut()
    {
        _backend.IdpResponseOverride = _ => OAuthError("invalid_request", new string('a', 295) + "my-secret");
        Assert.DoesNotContain("my-sec", ActionFailure());
    }

    [Fact]
    public void E5_InvalidClientStillTriggersTheBasicRetry()
    {
        _backend.IdpRequiresBasicAuth = true;
        _sut.Object_Exists(ClientCredentials(), "test-bucket-alpha", "a.txt", out bool exists);
        Assert.False(exists); // the object is absent in the fake; what matters is that the token call succeeded
        Assert.Contains(_backend.To(FakeFederationBackend.IdpTokenEndpoint), r => r.Authorization?.StartsWith("Basic ", StringComparison.Ordinal) == true);
    }

    // ---- A3: identity provider response is bounded to 1 MB ----------------------------------

    [Fact]
    public void R1_OversizedResponseIsRejected()
    {
        _backend.IdpResponseOverride = _ => Raw(HttpStatusCode.OK, new string('a', 2 * 1024 * 1024));
        string message = ActionFailure();
        Assert.Contains("larger than 1 MB", message);
        Assert.DoesNotContain("aaaa", message);
    }

    [Fact]
    public void R2_ResponseJustUnderTheLimitSucceeds()
    {
        string head = $"{{\"access_token\":\"{_backend.IdpToken}\",\"expires_in\":3600,\"token_type\":\"";
        const string tail = "\"}";
        int pad = 1_048_576 - 100 - head.Length - tail.Length;
        string body = head + new string('B', pad) + tail;
        Assert.Equal(1_048_576 - 100, Encoding.UTF8.GetByteCount(body));
        _backend.IdpResponseOverride = _ => Raw(HttpStatusCode.OK, body);

        _sut.Object_Exists(ClientCredentials(), "test-bucket-alpha", "a.txt", out bool exists);
        Assert.False(exists); // the object is absent in the fake; what matters is that the token call succeeded
    }

    [Fact]
    public void R3_TokenResponseWithUtf8ByteOrderMarkIsStillValidJson()
    {
        // Reading the body ourselves (for the 1 MB cap) must decode like ReadAsStringAsync: a BOM is not JSON.
        byte[] json = Encoding.UTF8.GetBytes($"{{\"access_token\":\"{_backend.IdpToken}\",\"expires_in\":3600}}");
        byte[] body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        _backend.IdpResponseOverride = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        };

        _sut.Object_Exists(ClientCredentials(), "test-bucket-alpha", "a.txt", out bool exists);
        Assert.False(exists); // the object is absent in the fake; what matters is that the token response parsed
    }

    // ---- A5: the cache cap holds on insert --------------------------------------------------

    [Fact]
    public void C1_CacheNeverExceedsItsCapOnInsert()
    {
        var fixedTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ExpiringCache<object>(maxEntries: 3, utcNow: () => fixedTime);
        object? tenth = null;
        for (int i = 0; i < 10; i++)
        {
            tenth = cache.GetOrAdd($"key-{i}", _ => new object(), DateTime.MaxValue);
        }

        Assert.True(cache.Count <= 3, $"Count was {cache.Count}");
        Assert.Same(tenth, cache.GetOrAdd("key-9", _ => throw new InvalidOperationException("must be cached"), DateTime.MaxValue));
    }

    // ---- A4 / A6: credential clients and ClientEmail ----------------------------------------

    private static object Invoke(string method, Authentication auth) =>
        // Reflection: both builders are private to the connector.
        typeof(Connector).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [auth])!;

    private static Authentication UniqueKeyAuth(string? email = null) =>
        TestSupport.Auth(clientEmail: email ?? $"t-{Guid.NewGuid():N}@fake-project.iam.gserviceaccount.com");

    [Fact]
    public void T1_KeyCredentialHttpClientHasTheStorageTimeout()
    {
        Connector.HttpClientFactoryOverride = null;
        // A unique email so the process-wide credential cache cannot hand back one built with a fake factory.
        var credential = (Google.Apis.Auth.OAuth2.ServiceAccountCredential)Invoke("GetServiceAccountCredential", UniqueKeyAuth());
        Assert.Equal(TimeSpan.FromSeconds(80), credential.HttpClient.Timeout);
    }

    [Fact]
    public void T2_FederatedCredentialHttpClientsHaveTheStorageTimeout()
    {
        Connector.HttpClientFactoryOverride = null;
        var identity = Invoke("CreateFederatedIdentity", ClientCredentials());
        var google = (GoogleCredential)identity.GetType().GetProperty("Credential")!.GetValue(identity)!;

        // Path: FederatedIdentity.Credential (private nested type, reflection) ->
        // GoogleCredential.UnderlyingCredential (ImpersonatedCredential) -> .HttpClient, and
        // ImpersonatedCredential.SourceCredential (the external account credential) -> .HttpClient.
        var impersonated = Assert.IsType<ImpersonatedCredential>(google.UnderlyingCredential);
        Assert.Equal(TimeSpan.FromSeconds(80), impersonated.HttpClient.Timeout);
        var source = Assert.IsAssignableFrom<ServiceCredential>(((GoogleCredential)impersonated.SourceCredential).UnderlyingCredential);
        Assert.Equal(TimeSpan.FromSeconds(80), source.HttpClient.Timeout);
    }

    [Fact]
    public void K1_ClientEmailIsTrimmedForTheKeyMethod()
    {
        Connector.HttpClientFactoryOverride = null;
        var credential = (ServiceAccountCredential)Invoke("GetServiceAccountCredential", UniqueKeyAuth(" test-sa@fake-project.iam.gserviceaccount.com\n"));
        Assert.Equal("test-sa@fake-project.iam.gserviceaccount.com", credential.Id);
    }

    // ---- 2.0.0: findings from the O11 connector's 1.7.4 reviews ---------------------------

    [Theory]
    [InlineData("p@ss w/rd+1", "p%40ss%20w%2Frd%2B1")]   // as Uri.EscapeDataString encodes it (HTTP Basic)
    [InlineData("p@ss w/rd+1", "p%40ss+w%2Frd%2B1")]     // as a form body encodes it (client_secret_post)
    public void E6_A_url_encoded_client_secret_echoed_by_the_provider_is_masked(string secret, string echoed)
    {
        var auth = ClientCredentials();
        auth.ClientSecret = secret;
        _backend.IdpResponseOverride = _ => OAuthError("invalid_request", $"bad client_secret={echoed} here");

        var e = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(auth, "test-bucket-alpha", "a.txt", out _));

        Assert.DoesNotContain(echoed, e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("w%2Frd", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", e.Message);
    }

    [Fact]
    public void H1_The_production_identity_provider_client_keeps_no_cookies_and_follows_no_redirects()
    {
        // Private member, read by reflection: the production client is never used by the fakes.
        var lazy = (Lazy<HttpClient>)typeof(Connector).GetField("IdentityProviderHttp", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var handler = (SocketsHttpHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lazy.Value)!;

        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(Timeout.InfiniteTimeSpan, lazy.Value.Timeout);
    }

    [Fact]
    public void T3_One_deadline_covers_both_client_authentication_attempts()
    {
        var saved = Connector.IdentityProviderDeadline;
        Connector.IdentityProviderDeadline = TimeSpan.FromMilliseconds(1000);
        try
        {
            int calls = 0;
            _backend.IdpResponseOverride = _ =>
            {
                // The first attempt is rejected after 800 ms; the second never sends its body. One shared
                // deadline ends the exchange at about 1 s; a fresh one per attempt would let it run to 1.8 s.
                if (++calls == 1)
                {
                    Thread.Sleep(800);
                    return Raw(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}");
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var e = Assert.ThrowsAny<Exception>(() => _sut.Object_Exists(ClientCredentials(), "test-bucket-alpha", "a.txt", out _));

            Assert.Contains("did not respond within", e.Message);
            Assert.Equal(2, calls);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"Took {watch.Elapsed}; the deadline must cover both attempts.");
        }
        finally
        {
            Connector.IdentityProviderDeadline = saved;
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
