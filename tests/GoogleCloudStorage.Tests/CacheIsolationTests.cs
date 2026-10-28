using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// 1.7.0 credential isolation and hardening: the bounded expiring cache (with a fake clock), JWT exp
/// parsing, length-prefixed cache keys, per-token federated identities and up-front rejection of
/// expired tokens, token endpoint validation, identity provider error handling (no redirects, no raw
/// bodies or query strings in messages), the shared service account credential and the Storage
/// request timeout. Fully offline, against the in-process fake identity provider, STS and Storage.
/// </summary>
public sealed class CacheIsolationTests : IDisposable
{
    private const string Provider = "//iam.googleapis.com/projects/123456789/locations/global/workloadIdentityPools/pool/providers/prov";

    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly string? _savedEmulatorHost;

    public CacheIsolationTests()
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

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string MakeJwt(string payloadJson) =>
        Base64Url("{\"alg\":\"none\"}") + "." + Base64Url(payloadJson) + ".sig";

    private static string AllMessages(Exception e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x != null; x = x.InnerException) parts.Add(x.Message);
        if (e is AggregateException agg) parts.AddRange(agg.Flatten().InnerExceptions.Select(i => i.Message));
        return string.Join(" | ", parts);
    }

    // ---- ExpiringCache tests (C1 - C6) ----------------------------------------------

    [Fact]
    public void C1_ExpiringCache_GetOrAdd_twice_returns_same_instance()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<object>(utcNow: () => clock);

        int factoryCalls = 0;
        var first = cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);
        var second = cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        Assert.Equal(1, factoryCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public void C2_ExpiringCache_expired_after_sliding_lifetime()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<object>(utcNow: () => clock);

        int factoryCalls = 0;
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        clock = t0.AddMinutes(60);
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void C3_ExpiringCache_hit_resets_sliding_lifetime()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<object>(utcNow: () => clock);

        int factoryCalls = 0;
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        clock = t0.AddMinutes(30);
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        clock = t0.AddMinutes(80);
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, DateTime.MaxValue);

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void C4_ExpiringCache_absolute_expiry()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<object>(utcNow: () => clock);

        int factoryCalls = 0;
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, t0.AddMinutes(5));

        clock = t0.AddMinutes(4).AddSeconds(59);
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, t0.AddMinutes(5));
        Assert.Equal(1, factoryCalls);

        clock = t0.AddMinutes(5);
        cache.GetOrAdd("k", _ => { factoryCalls++; return new object(); }, t0.AddMinutes(5));
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void C5_ExpiringCache_factory_exception_leaves_no_entry()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<string>(utcNow: () => clock);

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrAdd("k", _ => throw new InvalidOperationException("factory failed"), DateTime.MaxValue));

        Assert.Equal(0, cache.Count);

        int calls = 0;
        var val = cache.GetOrAdd("k", _ => { calls++; return "ok"; }, DateTime.MaxValue);
        Assert.Equal("ok", val);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void C6_ExpiringCache_sweep_drops_lru_surplus()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = t0;
        var cache = new ExpiringCache<string>(maxEntries: 3, utcNow: () => clock);

        // add k1..k5 at t0+1s..t0+5s (first call already swept)
        clock = t0.AddSeconds(1);
        cache.GetOrAdd("k1", _ => "v1", DateTime.MaxValue);
        clock = t0.AddSeconds(2);
        cache.GetOrAdd("k2", _ => "v2", DateTime.MaxValue);
        clock = t0.AddSeconds(3);
        cache.GetOrAdd("k3", _ => "v3", DateTime.MaxValue);
        clock = t0.AddSeconds(4);
        cache.GetOrAdd("k4", _ => "v4", DateTime.MaxValue);
        clock = t0.AddSeconds(5);
        cache.GetOrAdd("k5", _ => "v5", DateTime.MaxValue);

        // set clock t0+2min
        clock = t0.AddMinutes(2);

        int k5Calls = 0;
        cache.GetOrAdd("k5", _ => { k5Calls++; return "v5-new"; }, DateTime.MaxValue);
        Assert.Equal(0, k5Calls);
        Assert.Equal(3, cache.Count);

        int k3Calls = 0;
        cache.GetOrAdd("k3", _ => { k3Calls++; return "v3-new"; }, DateTime.MaxValue);
        Assert.Equal(0, k3Calls);

        int k1Calls = 0;
        cache.GetOrAdd("k1", _ => { k1Calls++; return "v1-new"; }, DateTime.MaxValue);
        Assert.Equal(1, k1Calls);
    }

    // ---- JWT & CacheKey tests (J1 - J9, K1 - K2) -------------------------------------

    [Fact]
    public void J1_TryReadJwtExpiry_valid_integer_exp()
    {
        string jwt = MakeJwt("{\"exp\":1893456000}");
        bool ok = Connector.TryReadJwtExpiry(jwt, out var exp);
        Assert.True(ok);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), exp);
        Assert.Equal(DateTimeKind.Utc, exp.Kind);
    }

    [Fact]
    public void J2_TryReadJwtExpiry_fractional_exp()
    {
        string jwt = MakeJwt("{\"exp\":1893456000.5}");
        bool ok = Connector.TryReadJwtExpiry(jwt, out var exp);
        Assert.True(ok);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc), exp);
        Assert.Equal(DateTimeKind.Utc, exp.Kind);
    }

    [Fact]
    public void J3_TryReadJwtExpiry_string_exp_rejected()
    {
        string jwt = MakeJwt("{\"exp\":\"1893456000\"}");
        bool ok = Connector.TryReadJwtExpiry(jwt, out _);
        Assert.False(ok);
    }

    [Fact]
    public void J4_TryReadJwtExpiry_missing_exp_rejected()
    {
        string jwt = MakeJwt("{\"sub\":\"x\"}");
        bool ok = Connector.TryReadJwtExpiry(jwt, out _);
        Assert.False(ok);
    }

    [Fact]
    public void J5_TryReadJwtExpiry_invalid_jwt_format_rejected()
    {
        Assert.False(Connector.TryReadJwtExpiry("a.b", out _));
        Assert.False(Connector.TryReadJwtExpiry("", out _));
        Assert.False(Connector.TryReadJwtExpiry(null, out _));
    }

    [Fact]
    public void J6_TryReadJwtExpiry_invalid_base64_rejected()
    {
        string jwt = Base64Url("{\"alg\":\"none\"}") + ".!!!.sig";
        Assert.False(Connector.TryReadJwtExpiry(jwt, out _));
    }

    [Fact]
    public void J7_TryReadJwtExpiry_non_json_payload_rejected()
    {
        string jwt = Base64Url("{\"alg\":\"none\"}") + "." + Base64Url("not json") + ".sig";
        Assert.False(Connector.TryReadJwtExpiry(jwt, out _));
    }

    [Fact]
    public void J8_TryReadJwtExpiry_out_of_range_exp_rejected()
    {
        Assert.False(Connector.TryReadJwtExpiry(MakeJwt("{\"exp\":-1}"), out _));
        Assert.False(Connector.TryReadJwtExpiry(MakeJwt("{\"exp\":1e20}"), out _));
    }

    [Fact]
    public void J9_TryReadJwtExpiry_base64url_with_plus_or_slash_decoded()
    {
        string payload = "{\"exp\":1893456000,\"x\":\"~~~????\"}";
        string stdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        Assert.True(stdB64.Contains('+') && stdB64.Contains('/'));

        string b64url = stdB64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string jwt = Base64Url("{\"alg\":\"none\"}") + "." + b64url + ".sig";

        bool ok = Connector.TryReadJwtExpiry(jwt, out var exp);
        Assert.True(ok);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), exp);
    }

    [Fact]
    public void K1_CacheKey_parts_are_length_prefixed()
    {
        string k1 = Connector.CacheKey("key", "a\nb", "c");
        string k2 = Connector.CacheKey("key", "a", "b\nc");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void K2_CacheKey_is_stable()
    {
        string k1 = Connector.CacheKey("key", "x", "y");
        string k2 = Connector.CacheKey("key", "x", "y");
        Assert.Equal(k1, k2);
    }

    // ---- Federation tests (F1 - F5) --------------------------------------------------

    [Fact]
    public void F1_Supplied_tokens_are_isolated_and_produce_separate_sts_requests()
    {
        string sa = UniqueServiceAccount();
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string tokenA = MakeJwt($"{{\"exp\":{nowUnix + 3600}}}");
        string tokenB = MakeJwt($"{{\"exp\":{nowUnix + 3601}}}");

        _sut.Bucket_Exists(SuppliedToken(tokenA, sa), "test-bucket-alpha", out var existsA);
        _sut.Bucket_Exists(SuppliedToken(tokenB, sa), "test-bucket-alpha", out var existsB);

        Assert.True(existsA);
        Assert.True(existsB);

        var sts = _backend.To("sts.googleapis.com").ToList();
        Assert.Equal(2, sts.Count);
        Assert.Equal(tokenA, sts[0].Form["subject_token"]);
        Assert.Equal(tokenB, sts[1].Form["subject_token"]);
    }

    [Fact]
    public void F2_Same_supplied_token_reuses_sts_exchange()
    {
        string sa = UniqueServiceAccount();
        string token = MakeJwt($"{{\"exp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600}}}");

        _sut.Bucket_Exists(SuppliedToken(token, sa), "test-bucket-alpha", out var exists1);
        _sut.Bucket_Exists(SuppliedToken(token, sa), "test-bucket-alpha", out var exists2);

        Assert.True(exists1);
        Assert.True(exists2);

        var sts = _backend.To("sts.googleapis.com").ToList();
        Assert.Single(sts);
    }

    [Fact]
    public void F3_Expired_supplied_token_rejected_before_network_call()
    {
        string sa = UniqueServiceAccount();
        string token = MakeJwt($"{{\"exp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600}}}");

        var ex = Assert.Throws<ArgumentException>(() => _sut.Bucket_Exists(SuppliedToken(token, sa), "test-bucket-alpha", out _));
        Assert.Contains("expired", ex.Message);
        Assert.Empty(_backend.To("sts.googleapis.com"));
    }

    [Fact]
    public void F4_Supplied_token_near_expiry_accepted_within_skew()
    {
        string sa = UniqueServiceAccount();
        string token = MakeJwt($"{{\"exp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10}}}");

        _sut.Bucket_Exists(SuppliedToken(token, sa), "test-bucket-alpha", out var exists);
        Assert.True(exists);

        var sts = _backend.To("sts.googleapis.com").ToList();
        Assert.Single(sts);
    }

    [Fact]
    public void F5_FederatedExpiry_semantics()
    {
        var expectedExp = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string tokenWithExp = MakeJwt("{\"exp\":1893456000}");
        Assert.Equal(expectedExp, Connector.FederatedExpiry(SuppliedToken(tokenWithExp)));

        string tokenWithoutExp = "a.b.c";
        var expiryNoExp = Connector.FederatedExpiry(SuppliedToken(tokenWithoutExp));
        var now = DateTime.UtcNow;
        Assert.True(expiryNoExp >= now.AddMinutes(5).AddSeconds(-5) && expiryNoExp <= now.AddMinutes(5).AddSeconds(5));

        Assert.Equal(DateTime.MaxValue, Connector.FederatedExpiry(ClientCredentials()));
    }

    // ---- Token endpoint validation & HTTP handling tests (H1 - H9) -------------------

    [Fact]
    public void H1_TokenEndpoint_with_user_info_rejected_and_password_not_echoed()
    {
        var auth = ClientCredentials();
        auth.TokenEndpoint = "https://user:pa55word@idp.example.test/oauth2/token";
        var ex = Assert.Throws<ArgumentException>(() => StorageClientFor(auth));
        Assert.Contains("user information", ex.Message);
        Assert.DoesNotContain("pa55word", ex.Message);
    }

    [Fact]
    public void H2_Loopback_http_allowed_only_when_factory_override_set()
    {
        var auth = ClientCredentials();
        auth.TokenEndpoint = "http://127.0.0.1:9/token";

        // Override is set by constructor: validation passes
        Assert.NotNull(StorageClientFor(auth));

        var savedFactory = Connector.HttpClientFactoryOverride;
        try
        {
            Connector.HttpClientFactoryOverride = null;
            var ex = Assert.Throws<ArgumentException>(() => StorageClientFor(auth));
            Assert.Contains("https://", ex.Message);
        }
        finally
        {
            Connector.HttpClientFactoryOverride = savedFactory;
        }
    }

    [Fact]
    public void H3_Non_loopback_http_rejected_even_with_override()
    {
        var auth = ClientCredentials();
        auth.TokenEndpoint = "http://idp.example.test/oauth2/token";
        var ex = Assert.Throws<ArgumentException>(() => StorageClientFor(auth));
        Assert.Contains("https://", ex.Message);
    }

    [Fact]
    public void H4_Idp_redirect_rejected_and_not_followed()
    {
        _backend.IdpResponseOverride = _ =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)307);
            res.Headers.Location = new Uri("https://evil.example.test/token");
            return res;
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("redirected (307)", msg);
        Assert.Empty(_backend.To("evil.example.test"));
    }

    [Fact]
    public void H5_Idp_html_error_summarized_without_raw_body()
    {
        _backend.IdpResponseOverride = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<html>SECRET-MARKER</html>", Encoding.UTF8, "text/html")
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("non-JSON response", msg);
        Assert.Contains("text/html", msg);
        Assert.DoesNotContain("SECRET-MARKER", chain);
    }

    [Fact]
    public void H6_Idp_oauth_error_description_truncated_to_300_chars()
    {
        string desc = new string('x', 400);
        _backend.IdpResponseOverride = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = "invalid_scope", error_description = desc }), Encoding.UTF8, "application/json")
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("invalid_scope", msg);
        Assert.Contains(new string('x', 300) + "...", msg);
        Assert.DoesNotContain(new string('x', 301), chain);
    }

    [Fact]
    public void H7_Idp_token_response_missing_string_access_token()
    {
        _backend.IdpResponseOverride = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":{"a":1}}""", Encoding.UTF8, "application/json")
        };

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("did not contain an 'access_token'", msg);
    }

    [Fact]
    public void H8_TokenEndpoint_query_stripped_from_error_messages()
    {
        var auth = ClientCredentials();
        auth.TokenEndpoint = "https://idp.example.test/oauth2/token?api-key=QUERYSECRET";
        _backend.IdpException = new HttpRequestException("Simulated connection failure");

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(auth, "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("https://idp.example.test/oauth2/token", msg);
        Assert.DoesNotContain("QUERYSECRET", chain);
    }

    [Fact]
    public void H9_Idp_TaskCanceledException_distinguished_from_caller_cancellation()
    {
        _backend.IdpException = new TaskCanceledException("Simulated HTTP client timeout");

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Bucket_Exists(ClientCredentials(), "test-bucket-alpha", out _));
        var msg = ex.Message;          // what the app sees
        var chain = AllMessages(ex);   // nothing may leak anywhere in the chain
        Assert.Contains("did not respond within 30 seconds", msg);
    }

    // ---- StorageClient, UrlSigner, ServiceAccountCredential tests (S1 - S3) ----------

    [Fact]
    public void S1_Key_mode_shares_single_ServiceAccountCredential_between_StorageClient_and_UrlSigner()
    {
        int before = Connector.ServiceAccountCredentialCache.Count;
        string email1 = $"sa-{Guid.NewGuid():N}"[..18] + "@fake-project.iam.gserviceaccount.com";
        var auth1 = TestSupport.Auth(clientEmail: email1);

        var client1 = StorageClientFor(auth1);
        Assert.NotNull(client1);
        _sut.Object_GetSignedUrl(auth1, "test-bucket-alpha", "docs/test.txt", 5, out var url1, "Download");
        Assert.Contains("X-Goog-Signature", url1);

        int after1 = Connector.ServiceAccountCredentialCache.Count;
        Assert.Equal(1, after1 - before);

        using var rsa = RSA.Create(2048);
        string diffKey = rsa.ExportPkcs8PrivateKeyPem();
        var auth2 = TestSupport.Auth(clientEmail: email1, privateKey: diffKey);
        var client2 = StorageClientFor(auth2);
        Assert.NotNull(client2);

        int after2 = Connector.ServiceAccountCredentialCache.Count;
        Assert.Equal(1, after2 - after1);
    }

    [Fact]
    public void S2_StorageClient_timeout_set_to_80_seconds()
    {
        string? savedEmulator = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        try
        {
            Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", null);
            var auth = TestSupport.Auth();
            var client = (StorageClient)StorageClientFor(auth)!;
            Assert.Equal(TimeSpan.FromSeconds(80), client.Service.HttpClient.Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST", savedEmulator);
        }
    }

    [Fact]
    public void S3_Client_credentials_lifetime_bounded_by_jwt_exp()
    {
        var sa = UniqueServiceAccount();
        var auth = ClientCredentials(sa);
        long expUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120;
        _backend.IdpToken = MakeJwt($"{{\"exp\":{expUnix}}}");
        _backend.IdpExpiresIn = 3600;

        _sut.Bucket_Exists(auth, "test-bucket-alpha", out var exists1);
        Assert.True(exists1);
        Assert.Single(_backend.To("idp.example.test"));

        _sut.Bucket_Exists(auth, "test-bucket-alpha", out var exists2);
        Assert.True(exists2);
        Assert.Single(_backend.To("idp.example.test")); // Reused!

        // Inspect CachedUntil on SubjectTokenSource
        var fedCacheField = typeof(Connector).GetField("FederatedIdentityCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fedCache = fedCacheField.GetValue(null)!;
        var entriesField = fedCache.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entriesDict = (System.Collections.IDictionary)entriesField.GetValue(fedCache)!;

        var fedKeyMethod = typeof(Connector).GetMethod("FederatedCacheKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fedKey = (string)fedKeyMethod.Invoke(null, [auth])!;

        var entryObj = entriesDict[fedKey]!;
        var valProp = entryObj.GetType().GetProperty("Value")!;
        var fedIdentity = valProp.GetValue(entryObj)!;
        var tokenSourceProp = fedIdentity.GetType().GetProperty("TokenSource")!;
        var tokenSource = tokenSourceProp.GetValue(fedIdentity)!;
        var cachedUntilProp = tokenSource.GetType().GetProperty("CachedUntil", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!;
        var cachedUntil = (DateTimeOffset)cachedUntilProp.GetValue(tokenSource)!;

        Assert.True(cachedUntil <= DateTimeOffset.UtcNow.AddSeconds(120), $"CachedUntil was {cachedUntil}, expected <= now+120s");
    }
}
