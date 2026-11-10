using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using System.Net;
using System.Reflection;
using System.Text;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

public sealed class Auth202Tests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";
    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly IHttpClientFactory? _priorFactory;
    private readonly string? _priorEmulator;
    private readonly TimeSpan _priorDeadline;
    private readonly DeadlineHttpClientFactory _factory;

    public Auth202Tests()
    {
        _priorFactory = Connector.HttpClientFactoryOverride;
        _priorEmulator = Connector.EmulatorBaseUri;
        _priorDeadline = Connector.IdentityProviderDeadline;
        Connector.EmulatorBaseUri = null;
        _factory = new DeadlineHttpClientFactory(_backend);
        Connector.HttpClientFactoryOverride = _factory;
    }

    public void Dispose()
    {
        Connector.HttpClientFactoryOverride = _priorFactory;
        Connector.EmulatorBaseUri = _priorEmulator;
        Connector.IdentityProviderDeadline = _priorDeadline;
    }

    private static Authentication ClientCredentials() => new()
    {
        ProjectId = "proj", AuthenticationMethod = "WorkloadIdentityFederation", WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        TokenEndpoint = FakeFederationBackend.IdpTokenEndpoint, ClientId = "my-client", ClientSecret = "my-secret",
        Scope = "api://gcs-connector/.default"
    };

    private static Authentication Supplied(string token) => new()
    {
        ProjectId = "proj", AuthenticationMethod = "WorkloadIdentityFederation", WorkloadIdentityProvider = Provider,
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com", SubjectToken = token
    };

    private void Call(Authentication a) => _sut.Object_Exists(a, "test-bucket-alpha", "a.txt", out _);
    private Exception Fail(Authentication a) => Assert.ThrowsAny<Exception>(() => Call(a));
    private int Requests => _backend.Requests.Count;
    private static string ValidJwt() => "eyJhbGciOiJSUzI1NiJ9." + Base64Url(Encoding.UTF8.GetBytes("{\"exp\":" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() + "}")) + ".c2ln";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void A1_provider_request_outlives_google_first_refresh_timeout()
    {
        var refreshType = typeof(GoogleCredential).Assembly.GetType("Google.Apis.Auth.OAuth2.TokenRefreshManager", throwOnError: true)!;
        var timeouts = (TimeSpan[])refreshType.GetField("RefreshTimeouts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var saved = timeouts[0];
        try
        {
            timeouts[0] = TimeSpan.FromSeconds(1);
            _factory.IdpDelay = TimeSpan.FromMilliseconds(2500);
            Connector.IdentityProviderDeadline = TimeSpan.FromSeconds(30);
            Call(ClientCredentials());
            Assert.Contains(_backend.Requests, r => r.Uri.ToString().StartsWith("https://storage.googleapis.com/"));
        }
        finally { timeouts[0] = saved; }
    }

    [Fact]
    public void A2_provider_timeout_has_connector_message()
    {
        Connector.IdentityProviderDeadline = TimeSpan.FromSeconds(1);
        _factory.IdpDelay = TimeSpan.FromSeconds(20);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Fail(ClientCredentials());
        Assert.Contains("did not respond within 1 seconds", ex.Message);
        Assert.DoesNotContain("canceled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A3_concurrent_provider_requests_obey_one_deadline()
    {
        Connector.IdentityProviderDeadline = TimeSpan.FromSeconds(1);
        _factory.IdpDelay = TimeSpan.FromSeconds(20);
        var a = ClientCredentials();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = Task.Run(() => Record.Exception(() => Call(a)));
        await Task.Delay(100);
        var second = Task.Run(() => Record.Exception(() => Call(a)));
        var results = await Task.WhenAll(first, second);
        Assert.All(results, ex => Assert.Contains("did not respond within 1 seconds", ex!.Message));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }


    [Fact]
    public void A4_form_wire_secret_is_masked()
    {
        var a = ClientCredentials(); a.ClientSecret = "p@ss word!*";
        _backend.IdpResponseOverride = request => request.RequestUri!.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_request\",\"error_description\":\"bad body client_secret=p%40ss+word%21%2A end\"}", Encoding.UTF8, "application/json") } : null;
        var ex = Fail(a);
        Assert.DoesNotContain("p%40ss+word%21%2A", ex.ToString());
        Assert.Contains("***", ex.ToString());
    }

    [Theory]
    [InlineData("p@ss word!*")]
    [InlineData("p%40ss%20word%21%2A")]
    [InlineData("p%40ss+word!*")]
    public void A5_plain_and_encoded_secret_forms_are_masked(string echoed)
    {
        var a = ClientCredentials(); a.ClientSecret = "p@ss word!*";
        _backend.IdpResponseOverride = request => request.RequestUri!.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error_description\":\"" + echoed + "\"}", Encoding.UTF8, "application/json") } : null;
        Assert.DoesNotContain(echoed, Fail(a).ToString());
    }

    [Fact]
    public void A6_garbage_subject_is_rejected_before_http()
    {
        var ex = Assert.Throws<ArgumentException>(() => Call(Supplied("garbage")));
        Assert.Equal("The SubjectToken is not a JWT: expected three base64url parts separated by dots (header.payload.signature). The value is not shown.", ex.Message);
        Assert.Equal(0, Requests);
    }

    [Theory]
    [InlineData("aaa.bbb")]
    [InlineData("a..c")]
    [InlineData("aaa.b b.ccc")]
    [InlineData("aaa.bbb.ccc.ddd")]
    [InlineData("aaa.b\nb.ccc")]
    public void A7_malformed_subjects_are_rejected_before_http(string token)
    {
        var ex = Assert.Throws<ArgumentException>(() => Call(Supplied(token)));
        Assert.DoesNotContain(token, ex.Message);
        Assert.Contains("expected three base64url parts", ex.Message);
        Assert.Equal(0, Requests);
    }

    [Fact]
    public void A8_oversized_subject_is_rejected_before_http()
    {
        var token = new string('a', 39996) + ".a.a";
        var ex = Assert.Throws<ArgumentException>(() => Call(Supplied(token)));
        Assert.Contains("40000", ex.Message);
        Assert.Equal(0, Requests);
    }

    [Fact]
    public void A8b_the_length_limit_is_exactly_32768_characters()
    {
        // 32,769 characters: one over. Rejected for its length, with the length in the message.
        var over = new string('a', 32765) + ".a.a";
        var ex = Assert.Throws<ArgumentException>(() => Call(Supplied(over)));
        Assert.Contains("32769", ex.Message);
        Assert.Equal(0, Requests);

        // 32,768 characters: at the limit. Not rejected for its length (Google judges the token itself).
        var atLimit = new string('a', 32764) + ".a.a";
        var outcome = Record.Exception(() => Call(Supplied(atLimit)));
        Assert.True(outcome == null || !outcome.Message.Contains("characters long"), outcome?.Message);
        Assert.True(Requests > 0);
    }

    [Fact]
    public void A9_many_invalid_subjects_do_not_change_service_credential_cache()
    {
        // The caches a supplied token would enter: federated identities, Storage clients, URL signers.
        var before = Connector.CachedCounts;
        for (int i = 0; i < 200; i++) Assert.Throws<ArgumentException>(() => Call(Supplied("invalid-" + i)));
        Assert.Equal(before, Connector.CachedCounts);
        Assert.Equal(0, Requests);
    }

    [Fact]
    public void A9b_made_up_jwt_shaped_tokens_that_google_rejects_leave_nothing_cached()
    {
        // A token only has to look like a JWT to get this far, so made-up ones must not keep a cache
        // slot once Google's token exchange has refused them: 500 of them would push out every real identity.
        _backend.StsError = "{\"error\":\"invalid_grant\",\"error_description\":\"The token is not valid.\"}";
        var before = Connector.CachedCounts;

        for (int i = 0; i < 50; i++)
        {
            string token = "eyJhbGciOiJub25lIn0." + Base64Url(Encoding.UTF8.GetBytes("{\"exp\":253402300000,\"jti\":" + i + "}")) + ".AAAA";
            var ex = Assert.ThrowsAny<Exception>(() => Call(Supplied(token)));
            Assert.Contains("Workload Identity Federation failed", ex.Message);
        }

        Assert.Equal(before, Connector.CachedCounts);
    }

    [Fact]
    public void A9c_an_accepted_supplied_token_stays_cached()
    {
        var before = Connector.CachedCounts;
        var a = Supplied(ValidJwt());

        Call(a);
        Call(a);

        Assert.Equal(before.Identities + 1, Connector.CachedCounts.Identities);
        Assert.Equal(before.StorageClients + 1, Connector.CachedCounts.StorageClients);
        // One exchange for the two calls: the second came from the cache.
        Assert.Single(_backend.Requests, r => r.Uri.ToString().StartsWith("https://sts.googleapis.com/"));
    }

    [Theory]
    [InlineData("Authorization: Basic {0}")]
    [InlineData("credentials={0} rejected")]
    public void A5b_the_basic_form_of_the_client_credential_is_masked(string echo)
    {
        var a = ClientCredentials(); a.ClientId = "my-client"; a.ClientSecret = "fake-secret-XYZ";
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("my-client:fake-secret-XYZ"));
        _backend.IdpResponseOverride = request => request.RequestUri!.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_request\",\"error_description\":\"" + string.Format(echo, basic) + "\"}", Encoding.UTF8, "application/json") } : null;

        var ex = Fail(a);

        Assert.DoesNotContain(basic, ex.ToString());
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public void A5c_a_secret_echoed_in_the_content_type_of_a_non_json_answer_is_masked()
    {
        var a = ClientCredentials(); a.ClientSecret = "fake-secret-XYZ";
        _backend.IdpResponseOverride = request =>
        {
            if (!request.RequestUri!.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)) return null;
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("not json") };
            response.Content.Headers.Remove("Content-Type");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; echo=fake-secret-XYZ");
            return response;
        };

        var ex = Fail(a);

        Assert.Contains("non-JSON response", ex.Message);
        Assert.DoesNotContain("fake-secret-XYZ", ex.ToString());
    }

    [Fact]
    public void A9d_a_subject_token_repeated_in_googles_refusal_is_masked()
    {
        string token = ValidJwt();
        _backend.StsError = "{\"error\":\"invalid_grant\",\"error_description\":\"The token " + token + " was refused.\"}";

        var ex = Assert.ThrowsAny<Exception>(() => Call(Supplied(token)));

        Assert.Contains("Workload Identity Federation failed", ex.Message);
        Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public void A12b_a_numeric_service_account_unique_id_is_accepted()
    {
        var a = ClientCredentials(); a.ServiceAccountEmail = "112233445566778899001";
        Call(a);
        Assert.Contains(_backend.Requests, r => r.Uri.ToString().Contains("/serviceAccounts/112233445566778899001:"));
    }

    [Fact]
    public void A10_valid_unexpired_jwt_still_works()
    {
        Call(Supplied(ValidJwt()));
        Assert.Contains(_backend.Requests, r => r.Uri.ToString().StartsWith("https://storage.googleapis.com/"));
    }

    [Theory]
    [InlineData("a/../../../../v1/other/x")]
    [InlineData("sa@proj\n.iam.gserviceaccount.com")]
    [InlineData("sa @proj.iam.gserviceaccount.com")]
    [InlineData("@x.com")]
    [InlineData("sa@")] 
    public void A11_invalid_service_account_emails_rejected_without_http(string email)
    {
        var a = ClientCredentials(); a.ServiceAccountEmail = email;
        var ex = Assert.Throws<ArgumentException>(() => Call(a));
        Assert.Contains("ServiceAccountEmail", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
        Assert.Equal(0, Requests);
    }

    [Fact]
    public void A12_trimmed_service_account_email_is_accepted()
    {
        var a = ClientCredentials(); a.ServiceAccountEmail = "  my-sa@my-proj.iam.gserviceaccount.com  ";
        Call(a);
        Assert.Contains(_backend.Requests, r => r.Uri.ToString().Contains("/serviceAccounts/my-sa@my-proj.iam.gserviceaccount.com:"));
    }

    [Fact]
    public void A13_client_id_scope_and_audience_are_trimmed_on_wire()
    {
        var a = ClientCredentials(); a.ClientId = "  my-client \n"; a.Scope = " api://x/.default "; a.Audience = " aud ";
        Call(a);
        string body = _backend.Requests.Single(r => r.Uri.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)).Body;
        Assert.Contains("client_id=my-client", body);
        Assert.Contains("scope=api%3A%2F%2Fx%2F.default", body);
        Assert.Contains("audience=aud", body);
    }

    [Fact]
    public void A14_client_secret_spaces_are_preserved_on_wire()
    {
        var a = ClientCredentials(); a.ClientSecret = " s3cret ";
        Call(a);
        string body = _backend.Requests.Single(r => r.Uri.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint)).Body;
        Assert.Contains("client_secret=+s3cret+", body);
    }

    private sealed class DeadlineHttpClientFactory : HttpClientFactory
    {
        private readonly FakeFederationBackend _backend;
        public DeadlineHttpClientFactory(FakeFederationBackend backend) => _backend = backend;
        public TimeSpan IdpDelay { get; set; }
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new DelayHandler(_backend, this);

        private sealed class DelayHandler : DelegatingHandler
        {
            private readonly DeadlineHttpClientFactory _owner;
            public DelayHandler(HttpMessageHandler backend, DeadlineHttpClientFactory owner) : base(backend) => _owner = owner;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.ToString().StartsWith(FakeFederationBackend.IdpTokenEndpoint, StringComparison.Ordinal) == true && _owner.IdpDelay > TimeSpan.Zero)
                    await Task.Delay(_owner.IdpDelay, cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }
            protected override void Dispose(bool disposing) { }
        }
    }

}
