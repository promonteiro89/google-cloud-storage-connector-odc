using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

/// <summary>
/// Authentication for the connector. Two methods are supported:
/// <list type="bullet">
/// <item><b>WorkloadIdentityFederation</b> (recommended): no Google key exists anywhere. The connector
/// obtains a JWT from any OIDC identity provider (OAuth 2.0 client-credentials grant, or a token the
/// caller supplies), exchanges it with Google's Security Token Service, and impersonates a service
/// account for a short-lived access token. Signed URLs are signed via the IAM Credentials signBlob API.</item>
/// <item><b>ServiceAccountKey</b> (legacy, Google's last resort): ClientEmail + PrivateKey from a service
/// account JSON key. Signed URLs are signed locally. This remains the default for backward compatibility.</item>
/// </list>
/// </summary>
public partial class GoogleCloudStorage
{
    internal const string MethodServiceAccountKey = "ServiceAccountKey";
    internal const string MethodWorkloadIdentityFederation = "WorkloadIdentityFederation";

    private const string StsTokenUrl = "https://sts.googleapis.com/v1/token";
    private const string JwtSubjectTokenType = "urn:ietf:params:oauth:token-type:jwt";

    /// <summary>
    /// Caches of StorageClient/UrlSigner instances per credential. Actions run on every request, and
    /// creating a client per call re-parses keys (or re-runs the token exchange) and allocates a new
    /// HttpClient each time (latency + socket exhaustion under load). Statics survive across requests
    /// in the ODC runtime, and StorageClient, UrlSigner and the Google credentials are thread-safe.
    /// Keys are SHA-256 hashes, so raw secrets are never retained as cache keys. The two methods use
    /// distinct key prefixes, so they can never share a cached client.
    /// </summary>
    private static readonly ConcurrentDictionary<string, StorageClient> StorageClientCache = new();
    private static readonly ConcurrentDictionary<string, UrlSigner> UrlSignerCache = new();
    private static readonly ConcurrentDictionary<string, FederatedIdentity> FederatedIdentityCache = new();

    private static readonly Lazy<HttpClient> IdentityProviderHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// <summary>
    /// Test seam: when set, all Google auth/Storage HTTP traffic and identity provider token requests
    /// in WorkloadIdentityFederation mode go through this factory. Never set in production.
    /// </summary>
    internal static IHttpClientFactory? HttpClientFactoryOverride { get; set; }

    private enum AuthMethod { ServiceAccountKey, WorkloadIdentityFederation }

    // ---- Method resolution and validation --------------------------------------------

    private static AuthMethod ResolveAuthMethod(Authentication a)
    {
        string method = (a.AuthenticationMethod ?? string.Empty).Trim();
        AuthMethod resolved;
        if (method.Length == 0 || method.Equals(MethodServiceAccountKey, StringComparison.OrdinalIgnoreCase))
            resolved = AuthMethod.ServiceAccountKey;
        else if (method.Equals(MethodWorkloadIdentityFederation, StringComparison.OrdinalIgnoreCase))
            resolved = AuthMethod.WorkloadIdentityFederation;
        else
            throw new ArgumentException($"Invalid AuthenticationMethod '{a.AuthenticationMethod}'. Use '{MethodWorkloadIdentityFederation}' or '{MethodServiceAccountKey}' (or leave it empty for {MethodServiceAccountKey}).");

        var missing = new List<string>();
        if (resolved == AuthMethod.ServiceAccountKey)
        {
            if (string.IsNullOrWhiteSpace(a.ClientEmail)) missing.Add(nameof(Authentication.ClientEmail));
            if (string.IsNullOrWhiteSpace(a.PrivateKey)) missing.Add(nameof(Authentication.PrivateKey));
            if (missing.Count > 0)
                throw new ArgumentException($"{MethodServiceAccountKey} authentication requires ClientEmail and PrivateKey (missing: {string.Join(", ", missing)}).");
            return resolved;
        }

        if (string.IsNullOrWhiteSpace(a.WorkloadIdentityProvider)) missing.Add(nameof(Authentication.WorkloadIdentityProvider));
        if (string.IsNullOrWhiteSpace(a.ServiceAccountEmail)) missing.Add(nameof(Authentication.ServiceAccountEmail));
        if (string.IsNullOrWhiteSpace(a.SubjectToken))
        {
            if (string.IsNullOrWhiteSpace(a.TokenEndpoint)) missing.Add(nameof(Authentication.TokenEndpoint));
            if (string.IsNullOrWhiteSpace(a.ClientId)) missing.Add(nameof(Authentication.ClientId));
            if (string.IsNullOrWhiteSpace(a.ClientSecret)) missing.Add(nameof(Authentication.ClientSecret));
        }
        if (missing.Count > 0)
            throw new ArgumentException($"{MethodWorkloadIdentityFederation} authentication requires WorkloadIdentityProvider, ServiceAccountEmail, and either SubjectToken or TokenEndpoint + ClientId + ClientSecret (missing: {string.Join(", ", missing)}).");

        NormalizeWorkloadIdentityProvider(a.WorkloadIdentityProvider); // throws on a malformed value
        if (string.IsNullOrWhiteSpace(a.SubjectToken))
            ValidateTokenEndpoint(a.TokenEndpoint);
        return resolved;
    }

    /// <summary>The identity whose permissions apply, for error messages.</summary>
    private static string IdentityOf(Authentication a) =>
        ResolveAuthMethodSafe(a) == AuthMethod.WorkloadIdentityFederation ? a.ServiceAccountEmail : a.ClientEmail;

    private static AuthMethod ResolveAuthMethodSafe(Authentication a) =>
        (a.AuthenticationMethod ?? string.Empty).Trim().Equals(MethodWorkloadIdentityFederation, StringComparison.OrdinalIgnoreCase)
            ? AuthMethod.WorkloadIdentityFederation
            : AuthMethod.ServiceAccountKey;

    /// <summary>
    /// Accepts the provider as '//iam.googleapis.com/projects/..', 'https://iam.googleapis.com/projects/..'
    /// or 'projects/..' and returns the STS audience form '//iam.googleapis.com/projects/..'.
    /// </summary>
    internal static string NormalizeWorkloadIdentityProvider(string provider)
    {
        string p = provider.Trim();
        if (p.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) p = p.Substring("https:".Length);
        if (p.StartsWith("projects/", StringComparison.Ordinal)) p = "//iam.googleapis.com/" + p;

        bool valid = p.StartsWith("//iam.googleapis.com/projects/", StringComparison.Ordinal)
            && p.Contains("/locations/", StringComparison.Ordinal)
            && p.Contains("/workloadIdentityPools/", StringComparison.Ordinal)
            && p.Contains("/providers/", StringComparison.Ordinal);
        if (!valid)
            throw new ArgumentException($"WorkloadIdentityProvider '{provider}' is not a workload identity provider resource name. Expected '//iam.googleapis.com/projects/PROJECT_NUMBER/locations/global/workloadIdentityPools/POOL_ID/providers/PROVIDER_ID'.");
        return p;
    }

    private static void ValidateTokenEndpoint(string tokenEndpoint)
    {
        bool ok = Uri.TryCreate(tokenEndpoint.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
        if (!ok)
            throw new ArgumentException($"TokenEndpoint '{tokenEndpoint}' must be an absolute https:// URL (for example 'https://login.microsoftonline.com/TENANT_ID/oauth2/v2.0/token').");
    }

    private static string CacheKey(string prefix, params string?[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts.Select(p => p ?? string.Empty))));
        return prefix + "|" + Convert.ToBase64String(hash);
    }

    private static string FederatedCacheKey(Authentication a) => CacheKey("wif",
        NormalizeWorkloadIdentityProvider(a.WorkloadIdentityProvider),
        a.ServiceAccountEmail.Trim(),
        string.IsNullOrWhiteSpace(a.SubjectToken) ? "client-credentials" : "supplied-token",
        a.TokenEndpoint?.Trim(), a.ClientId, a.ClientSecret, a.Scope, a.Audience);

    // ---- Client factories ------------------------------------------------------------

    /// <summary>
    /// Returns a cached StorageClient for the given credentials, creating it on first use.
    /// Honors the GCSCONNECTOR_EMULATOR_HOST environment variable (never set on a real ODC
    /// server): when present, connects unauthenticated to a local GCS emulator such as
    /// fake-gcs-server, enabling integration tests without Google credentials. The name is
    /// deliberately extension-specific (not Google's STORAGE_EMULATOR_HOST) so a machine-wide
    /// variable set for other tooling can never silently redirect this connector.
    /// </summary>
    private static StorageClient GetStorageClient(Authentication authentication)
    {
        var method = ResolveAuthMethod(authentication);

        string? emulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        if (!string.IsNullOrEmpty(emulatorHost))
        {
            string baseUri = (emulatorHost.Contains("://") ? emulatorHost : "http://" + emulatorHost).TrimEnd('/') + "/storage/v1/";
            return StorageClientCache.GetOrAdd(
                "emulator|" + baseUri,
                _ => new StorageClientBuilder { BaseUri = baseUri, UnauthenticatedAccess = true }.Build());
        }

        if (method == AuthMethod.WorkloadIdentityFederation)
        {
            var identity = GetFederatedIdentity(authentication);
            return StorageClientCache.GetOrAdd(FederatedCacheKey(authentication), _ =>
            {
                var builder = new StorageClientBuilder { GoogleCredential = identity.Credential };
                if (HttpClientFactoryOverride != null) builder.HttpClientFactory = HttpClientFactoryOverride;
                return builder.Build();
            });
        }

        return StorageClientCache.GetOrAdd(
            CacheKey("key", authentication.ClientEmail, authentication.PrivateKey),
            _ => StorageClient.Create(GetServiceAccountCredential(authentication).ToGoogleCredential()));
    }

    /// <summary>
    /// Returns a cached UrlSigner for the given credentials, creating it on first use. With a
    /// service account key the signature is computed locally; with Workload Identity Federation it
    /// is produced by the IAM Credentials signBlob API as the impersonated service account.
    /// </summary>
    private static UrlSigner GetUrlSigner(Authentication authentication)
    {
        if (ResolveAuthMethod(authentication) == AuthMethod.WorkloadIdentityFederation)
        {
            var identity = GetFederatedIdentity(authentication);
            return UrlSignerCache.GetOrAdd(FederatedCacheKey(authentication), _ => UrlSigner.FromCredential(identity.Credential));
        }

        return UrlSignerCache.GetOrAdd(
            CacheKey("key", authentication.ClientEmail, authentication.PrivateKey),
            _ => UrlSigner.FromCredential(GetServiceAccountCredential(authentication)));
    }

    private static ServiceAccountCredential GetServiceAccountCredential(Authentication authentication)
    {
        try
        {
            var initializer = new ServiceAccountCredential.Initializer(authentication.ClientEmail)
            {
                Scopes = new[] { StorageService.Scope.CloudPlatform }
            }.FromPrivateKey(authentication.PrivateKey.Replace("\\n", "\n"));

            return new ServiceAccountCredential(initializer);
        }
        catch (Exception e)
        {
            throw new ArgumentException("The PrivateKey could not be parsed. Provide the full 'private_key' value from the service account JSON key, including the -----BEGIN PRIVATE KEY----- and -----END PRIVATE KEY----- lines.", e);
        }
    }

    // ---- Workload Identity Federation ------------------------------------------------

    /// <summary>
    /// A federated credential plus the source of its identity-provider token. The credential chain is
    /// external account (subject token -> Google STS) -> impersonated service account (IAM Credentials).
    /// The SDK refreshes both automatically, calling back into the token source only when needed.
    /// </summary>
    private sealed class FederatedIdentity
    {
        public FederatedIdentity(GoogleCredential credential, SubjectTokenSource tokenSource)
        {
            Credential = credential;
            TokenSource = tokenSource;
        }

        public GoogleCredential Credential { get; }
        public SubjectTokenSource TokenSource { get; }
    }

    private static FederatedIdentity GetFederatedIdentity(Authentication a)
    {
        var identity = FederatedIdentityCache.GetOrAdd(FederatedCacheKey(a), _ => CreateFederatedIdentity(a));
        if (!string.IsNullOrWhiteSpace(a.SubjectToken))
            identity.TokenSource.Supply(a.SubjectToken.Trim()); // latest caller-supplied token wins
        return identity;
    }

    private static FederatedIdentity CreateFederatedIdentity(Authentication a)
    {
        var tokenSource = new SubjectTokenSource(a.TokenEndpoint?.Trim(), a.ClientId, a.ClientSecret, a.Scope, a.Audience);

        var externalInitializer = new ProgrammaticExternalAccountCredential.Initializer(
            StsTokenUrl, NormalizeWorkloadIdentityProvider(a.WorkloadIdentityProvider), JwtSubjectTokenType, tokenSource)
        {
            Scopes = new[] { StorageService.Scope.CloudPlatform }
        };
        if (HttpClientFactoryOverride != null) externalInitializer.HttpClientFactory = HttpClientFactoryOverride;
        var federated = GoogleCredential.FromProgrammaticExternalAccountCredential(new ProgrammaticExternalAccountCredential(externalInitializer));

        var impersonationInitializer = new ImpersonatedCredential.Initializer(a.ServiceAccountEmail.Trim())
        {
            Scopes = new[] { StorageService.Scope.CloudPlatform }
        };
        if (HttpClientFactoryOverride != null) impersonationInitializer.HttpClientFactory = HttpClientFactoryOverride;

        return new FederatedIdentity(federated.Impersonate(impersonationInitializer), tokenSource);
    }

    /// <summary>
    /// Supplies the identity-provider JWT to Google's STS exchange: either the token the caller passed
    /// in (SubjectToken), or one obtained with the standard OAuth 2.0 client-credentials grant
    /// (RFC 6749 section 4.4), which is the same call on Entra ID, Okta, Auth0, Keycloak and others. Tokens are
    /// cached until shortly before they expire.
    /// </summary>
    private sealed class SubjectTokenSource : ProgrammaticExternalAccountCredential.ISubjectTokenProvider
    {
        private readonly string? _tokenEndpoint, _clientId, _clientSecret, _scope, _audience;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private volatile string? _suppliedToken;
        private string? _cachedToken;
        private DateTimeOffset _cachedUntil;
        private bool _useBasicClientAuth;

        public SubjectTokenSource(string? tokenEndpoint, string? clientId, string? clientSecret, string? scope, string? audience)
        {
            _tokenEndpoint = tokenEndpoint;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _scope = scope;
            _audience = audience;
        }

        public void Supply(string token) => _suppliedToken = token;

        public async Task<string> GetSubjectTokenAsync(ProgrammaticExternalAccountCredential caller, CancellationToken taskCancellationToken)
        {
            if (_suppliedToken is { Length: > 0 } supplied)
                return supplied;

            await _lock.WaitAsync(taskCancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedToken != null && DateTimeOffset.UtcNow < _cachedUntil)
                    return _cachedToken;

                var (token, lifetime) = await RequestClientCredentialsTokenAsync(taskCancellationToken).ConfigureAwait(false);
                _cachedToken = token;
                // Refresh a minute early (or halfway through a very short lifetime).
                var margin = lifetime > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(lifetime.Ticks / 2);
                _cachedUntil = DateTimeOffset.UtcNow + lifetime - margin;
                return token;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<(string token, TimeSpan lifetime)> RequestClientCredentialsTokenAsync(CancellationToken ct)
        {
            HttpClient http = HttpClientFactoryOverride != null
                ? HttpClientFactoryOverride.CreateHttpClient(new CreateHttpClientArgs())
                : IdentityProviderHttp.Value;

            // client_secret_post first (Entra ID, Auth0, Keycloak); if the provider rejects the client,
            // retry once with HTTP Basic client authentication (Okta's default).
            for (int attempt = 0; ; attempt++)
            {
                bool basic = _useBasicClientAuth || attempt > 0;
                using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint);
                var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
                if (basic)
                {
                    var raw = Uri.EscapeDataString(_clientId ?? string.Empty) + ":" + Uri.EscapeDataString(_clientSecret ?? string.Empty);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
                }
                else
                {
                    form.Add(new("client_id", _clientId ?? string.Empty));
                    form.Add(new("client_secret", _clientSecret ?? string.Empty));
                }
                if (!string.IsNullOrWhiteSpace(_scope)) form.Add(new("scope", _scope!));
                if (!string.IsNullOrWhiteSpace(_audience)) form.Add(new("audience", _audience!));
                request.Content = new FormUrlEncodedContent(form);

                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
                {
                    throw new IdentityProviderException($"Could not reach the identity provider token endpoint '{_tokenEndpoint}': {e.Message}", e);
                }

                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var (token, expiresIn) = ParseTokenResponse(body);
                        if (!LooksLikeJwt(token))
                            throw new IdentityProviderException("The identity provider returned a token that is not a JWT, but Workload Identity Federation requires a signed JWT. For Entra ID, request a token for your own app registration (Scope 'api://YOUR_APP_ID/.default'), not Microsoft Graph; for Okta, use a custom authorization server.");
                        if (basic) _useBasicClientAuth = true;
                        return (token, TimeSpan.FromSeconds(expiresIn));
                    }

                    var (error, description) = ParseOAuthError(body);
                    bool invalidClient = response.StatusCode == HttpStatusCode.Unauthorized || error == "invalid_client";
                    if (!basic && invalidClient && attempt == 0)
                        continue;

                    string detail = description ?? (body.Length > 300 ? body.Substring(0, 300) + "..." : body);
                    throw new IdentityProviderException($"The identity provider rejected the client-credentials token request ({(int)response.StatusCode} {error ?? response.StatusCode.ToString()}): {detail}. Check TokenEndpoint, ClientId, ClientSecret, and Scope/Audience.");
                }
            }
        }

        private static (string token, int expiresIn) ParseTokenResponse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? token = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    token = root.TryGetProperty("id_token", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    throw new IdentityProviderException("The identity provider's token response did not contain an 'access_token'.");

                int expiresIn = 3600;
                if (root.TryGetProperty("expires_in", out var ei))
                {
                    if (ei.ValueKind == JsonValueKind.Number && ei.TryGetInt32(out var n)) expiresIn = n;
                    else if (ei.ValueKind == JsonValueKind.String && int.TryParse(ei.GetString(), out var s)) expiresIn = s;
                }
                return (token!, Math.Max(expiresIn, 30));
            }
            catch (JsonException e)
            {
                throw new IdentityProviderException("The identity provider's token response was not valid JSON.", e);
            }
        }

        private static (string? error, string? description) ParseOAuthError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                string? description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                return (error, description);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static bool LooksLikeJwt(string token)
        {
            var parts = token.Split('.');
            return parts.Length == 3 && parts.All(p => p.Length > 0);
        }
    }

    /// <summary>A failure obtaining a token from the identity provider (surfaced unchanged to the caller).</summary>
    internal sealed class IdentityProviderException : Exception
    {
        public IdentityProviderException(string message, Exception? inner = null) : base(message, inner) { }
    }

    // ---- Auth-aware error translation ------------------------------------------------

    private static string UnauthenticatedHint(Authentication a) =>
        ResolveAuthMethodSafe(a) == AuthMethod.WorkloadIdentityFederation
            ? $"Check the Workload Identity Federation setup (provider, attribute condition) and that the federated identity has 'Workload Identity User' on '{a.ServiceAccountEmail}'."
            : "Check that ClientEmail and PrivateKey belong to the same service account and that the key has not been revoked.";

    /// <summary>
    /// Translates a token endpoint failure (key: typically 'invalid_grant'; federation: the STS
    /// exchange or the impersonation was rejected) into an actionable message.
    /// </summary>
    private static Exception FriendlyAuthException(TokenResponseException e, Authentication a)
    {
        if (ResolveAuthMethodSafe(a) == AuthMethod.WorkloadIdentityFederation)
            return new Exception($"Workload Identity Federation failed for service account '{a.ServiceAccountEmail}': Google rejected the token exchange or impersonation. Common causes: a wrong WorkloadIdentityProvider value, a token whose issuer or audience does not match the provider, a token rejected by the attribute condition, or a federated identity without 'Workload Identity User' on the service account. Details: {e.Message}", e);

        return new Exception($"Google rejected the service account credentials for '{a.ClientEmail}' (ClientEmail/PrivateKey mismatch, deleted service account, revoked key, or server clock skew). Details: {e.Message}", e);
    }

    /// <summary>Translates a failure while signing a URL (federation: the signBlob call) into an actionable message.</summary>
    private static Exception FriendlySigningException(Exception e, Authentication a)
    {
        if (ResolveAuthMethodSafe(a) == AuthMethod.WorkloadIdentityFederation)
            return new Exception($"Signing the URL failed: service account '{a.ServiceAccountEmail}' could not sign through the IAM Credentials API. Grant the federated identity 'Service Account Token Creator' on the service account. Details: {e.Message}", e);
        return new Exception($"Signing the URL failed for service account '{a.ClientEmail}'. Details: {e.Message}", e);
    }
}
