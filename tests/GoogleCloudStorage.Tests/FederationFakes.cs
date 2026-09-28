using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// A single in-process HTTP backend that impersonates every party in the Workload Identity
/// Federation chain: the identity provider's token endpoint, Google STS, IAM Credentials
/// (generateAccessToken + signBlob) and the Storage JSON API. Every request is recorded so tests
/// can assert the exact protocol the connector speaks. No network is used.
/// </summary>
internal sealed class FakeFederationBackend : HttpMessageHandler
{
    public const string IdpTokenEndpoint = "https://idp.example.test/oauth2/token";
    public const string StsToken = "sts-federated-token";
    public const string ServiceAccountToken = "sa-impersonated-token";
    public static readonly byte[] SignatureBytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, string Body)
    {
        public Dictionary<string, string> Form => Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "");
    }

    public List<Recorded> Requests { get; } = new();

    /// <summary>JWT the fake identity provider issues.</summary>
    public string IdpToken { get; set; } = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ3b3JrbG9hZCJ9.c2ln";

    /// <summary>When true, the identity provider rejects client_secret_post (only HTTP Basic works).</summary>
    public bool IdpRequiresBasicAuth { get; set; }

    /// <summary>When true, the identity provider rejects every request with invalid_client.</summary>
    public bool IdpRejectsClient { get; set; }

    /// <summary>Optional STS failure body (returned with 400).</summary>
    public string? StsError { get; set; }

    /// <summary>Optional signBlob failure status.</summary>
    public HttpStatusCode? SignBlobFailure { get; set; }

    public IEnumerable<Recorded> To(string hostOrPathFragment) =>
        Requests.Where(r => r.Uri.ToString().Contains(hostOrPathFragment, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        string? auth = request.Headers.Authorization?.ToString();
        lock (Requests) Requests.Add(new Recorded(request.Method, request.RequestUri!, auth, body));

        string url = request.RequestUri!.ToString();

        if (url.StartsWith(IdpTokenEndpoint, StringComparison.Ordinal))
        {
            bool usedBasic = auth?.StartsWith("Basic ", StringComparison.Ordinal) == true;
            if (IdpRejectsClient || (IdpRequiresBasicAuth && !usedBasic))
                return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "Client authentication failed." });
            return Json(HttpStatusCode.OK, new { access_token = IdpToken, token_type = "Bearer", expires_in = 3600 });
        }

        if (url.StartsWith("https://sts.googleapis.com/v1/token", StringComparison.Ordinal))
        {
            if (StsError != null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(StsError, Encoding.UTF8, "application/json") };
            return Json(HttpStatusCode.OK, new
            {
                access_token = StsToken,
                issued_token_type = "urn:ietf:params:oauth:token-type:access_token",
                token_type = "Bearer",
                expires_in = 3600
            });
        }

        if (url.StartsWith("https://iamcredentials.googleapis.com/", StringComparison.Ordinal) && url.EndsWith(":generateAccessToken", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, new
            {
                accessToken = ServiceAccountToken,
                expireTime = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
        }

        if (url.StartsWith("https://iamcredentials.googleapis.com/", StringComparison.Ordinal) && url.EndsWith(":signBlob", StringComparison.Ordinal))
        {
            if (SignBlobFailure is { } status)
                return Json(status, new { error = new { code = (int)status, message = "Permission 'iam.serviceAccounts.signBlob' denied.", status = "PERMISSION_DENIED" } });
            return Json(HttpStatusCode.OK, new { keyId = "fake-key", signedBlob = Convert.ToBase64String(SignatureBytes) });
        }

        if (url.StartsWith("https://storage.googleapis.com/storage/v1/b/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            var bucket = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            return Json(HttpStatusCode.OK, new { kind = "storage#bucket", name = bucket, id = bucket });
        }

        return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"FakeFederationBackend has no route for {request.Method} {url}" } });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
}

/// <summary>Google HttpClientFactory whose innermost handler is the fake backend.</summary>
internal sealed class FakeHttpClientFactory : HttpClientFactory
{
    private readonly FakeFederationBackend _backend;
    public FakeHttpClientFactory(FakeFederationBackend backend) => _backend = backend;
    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new NonDisposingHandler(_backend);

    /// <summary>ConfigurableHttpClient disposes its handler chain; keep the shared fake alive.</summary>
    private sealed class NonDisposingHandler : DelegatingHandler
    {
        public NonDisposingHandler(HttpMessageHandler inner) : base(inner) { }
        protected override void Dispose(bool disposing) { /* the fake backend outlives any one client */ }
    }
}
