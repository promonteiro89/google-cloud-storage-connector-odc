using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

/// <summary>
/// A bounded cache with a sliding lifetime: an entry is dropped after 60 minutes without a hit, or
/// at its absolute expiry when it has one (a supplied token's exp), whichever comes first. Values
/// are never disposed when they leave the cache: another request may still be using them.
/// </summary>
internal sealed class ExpiringCache<T> where T : class
{
    private readonly int _maxEntries;
    private readonly Func<DateTime> _utcNow;
    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private sealed class Entry
    {
        public Entry(T value, DateTime absoluteExpiryUtc, DateTime lastUsedUtc)
        {
            Value = value;
            AbsoluteExpiryUtc = absoluteExpiryUtc;
            LastUsedUtc = lastUsedUtc;
        }

        public T Value { get; }
        public DateTime AbsoluteExpiryUtc { get; }
        public DateTime LastUsedUtc { get; set; }

        public bool IsExpired(DateTime utcNow) =>
            utcNow >= AbsoluteExpiryUtc || utcNow - LastUsedUtc >= SlidingLifetime;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly object _missLock = new();
    private readonly object _sweepLock = new();
    private DateTime _nextSweepUtc;

    internal ExpiringCache(int maxEntries = 500, Func<DateTime>? utcNow = null)
    {
        _maxEntries = maxEntries;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Number of entries held, including ones not swept yet (test seam).</summary>
    internal int Count => _entries.Count;

    /// <summary>
    /// Returns the live value for the key, or creates one with the factory and the given absolute
    /// expiry (DateTime.MaxValue for none). A factory that throws leaves no entry behind.
    /// </summary>
    public T GetOrAdd(string key, Func<string, T> factory, DateTime absoluteExpiryUtc)
    {
        SweepIfDue();

        var now = _utcNow();
        var entry = FindLive(key, now);
        if (entry != null) return entry.Value;

        // Misses are rare (once per credential per lifetime), so one lock is enough to stop two
        // callers from building the same value or installing a stale entry over a live one.
        lock (_missLock)
        {
            now = _utcNow();
            entry = FindLive(key, now);
            if (entry != null) return entry.Value;

            T value = factory(key); // may throw: nothing has been cached yet
            _entries[key] = new Entry(value, absoluteExpiryUtc, now);
            return value;
        }
    }

    /// <summary>Returns the live entry for the key and marks it used; drops the entry when it has expired.</summary>
    private Entry? FindLive(string key, DateTime utcNow)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;

        if (entry.IsExpired(utcNow))
        {
            Remove(key, entry);
            return null;
        }

        entry.LastUsedUtc = utcNow;
        return entry;
    }

    /// <summary>At most once a minute, from any access: drop the expired entries, then the least recently used surplus.</summary>
    private void SweepIfDue()
    {
        DateTime now = _utcNow();
        lock (_sweepLock)
        {
            if (now < _nextSweepUtc) return;
            _nextSweepUtc = now + SweepInterval;
        }
        Sweep(now);
    }

    private void Sweep(DateTime utcNow)
    {
        var live = new List<KeyValuePair<string, Entry>>();
        foreach (var pair in _entries)
        {
            if (!pair.Value.IsExpired(utcNow))
            {
                live.Add(pair);
                continue;
            }

            // Values are not disposed: another request may still be using an evicted client.
            Remove(pair.Key, pair.Value);
        }

        if (live.Count <= _maxEntries) return;
        foreach (var pair in live.OrderBy(p => p.Value.LastUsedUtc).Take(live.Count - _maxEntries))
            Remove(pair.Key, pair.Value);
    }

    /// <summary>
    /// Removes the key only while it still maps to this entry (an atomic compare-and-remove), so a
    /// fresh entry another thread has just installed under the same key is never dropped.
    /// </summary>
    private void Remove(string key, Entry entry)
    {
        ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(key, entry));
    }
}

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
    /// distinct key prefixes, so they can never share a cached client. Entries expire (see
    /// ExpiringCache), so the caches stay bounded however many credentials a process sees.
    /// </summary>
    private static readonly ExpiringCache<StorageClient> StorageClientCache = new();
    private static readonly ExpiringCache<UrlSigner> UrlSignerCache = new();
    private static readonly ExpiringCache<FederatedIdentity> FederatedIdentityCache = new();
    internal static readonly ExpiringCache<ServiceAccountCredential> ServiceAccountCredentialCache = new();

    /// <summary>
    /// Timeout of each request to Google Cloud Storage, below ODC's 95-second limit for one action so
    /// a stalled request fails with the connector's own message instead of the platform's timeout.
    /// </summary>
    internal static readonly TimeSpan StorageRequestTimeout = TimeSpan.FromSeconds(80);

    /// <summary>
    /// Production identity-provider client: never follows redirects (a 307/308 would re-send the
    /// client secret to the redirect target and silently swap the token endpoint), renews its
    /// connections every 5 minutes so DNS changes are picked up, and gives up after 30 seconds.
    /// </summary>
    private static readonly Lazy<HttpClient> IdentityProviderHttp = new(() =>
        new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        });

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
            if (string.IsNullOrWhiteSpace(a.ClientEmail)) missing.Add("ClientEmail");
            if (string.IsNullOrWhiteSpace(a.PrivateKey)) missing.Add("PrivateKey");
            if (missing.Count > 0)
                throw new ArgumentException($"{MethodServiceAccountKey} authentication requires ClientEmail and PrivateKey (missing: {string.Join(", ", missing)}).");
            return resolved;
        }

        if (string.IsNullOrWhiteSpace(a.WorkloadIdentityProvider)) missing.Add("WorkloadIdentityProvider");
        if (string.IsNullOrWhiteSpace(a.ServiceAccountEmail)) missing.Add("ServiceAccountEmail");
        if (string.IsNullOrWhiteSpace(a.SubjectToken))
        {
            if (string.IsNullOrWhiteSpace(a.TokenEndpoint)) missing.Add("TokenEndpoint");
            if (string.IsNullOrWhiteSpace(a.ClientId)) missing.Add("ClientId");
            if (string.IsNullOrWhiteSpace(a.ClientSecret)) missing.Add("ClientSecret");
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

    /// <summary>
    /// Requires an absolute https:// token endpoint. Plain http is accepted only for loopback hosts
    /// and only while the test override is set (never in production). URLs carrying user information
    /// (https://user:password@host) are always rejected, and the error never echoes that part.
    /// Intranet hosts are allowed: on-premises Keycloak/ADFS is supported.
    /// </summary>
    internal static void ValidateTokenEndpoint(string? tokenEndpoint)
    {
        string trimmed = (tokenEndpoint ?? string.Empty).Trim();
        bool absolute = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri);
        string display = absolute && uri != null ? uri.GetLeftPart(UriPartial.Path) : trimmed;

        if (absolute && uri != null && !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("TokenEndpoint must not contain user information (a 'user:password@' part). Remove the credentials from the URL and let ClientId/ClientSecret authenticate the request.");

        bool ok = absolute && uri != null
            && (uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && HttpClientFactoryOverride != null));
        if (!ok)
            throw new ArgumentException($"TokenEndpoint '{display}' must be an absolute https:// URL (for example 'https://login.microsoftonline.com/TENANT_ID/oauth2/v2.0/token').");
    }

    /// <summary>
    /// A cache key that never exposes the secret. Every part is length-prefixed before hashing, so a
    /// part that contains the separator (a PEM private key contains newlines) cannot shift the
    /// boundary and make two different credentials produce the same key.
    /// </summary>
    internal static string CacheKey(string prefix, params string?[] parts)
    {
        string canonical = string.Join("\n", parts.Select(p =>
        {
            string part = p ?? string.Empty;
            return part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part;
        }));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return prefix + "|" + Convert.ToBase64String(hash);
    }

    /// <summary>
    /// The cache key for a federated identity. With a supplied token the token itself is hashed into
    /// the key (and TokenEndpoint, ClientId, ClientSecret, Scope and Audience are left out, since they
    /// are not used): a caller that supplies a different token must never get another caller's cached
    /// exchange or impersonated token. Client-credentials entries are keyed by the whole configuration.
    /// </summary>
    private static string FederatedCacheKey(Authentication a)
    {
        string? supplied = SuppliedToken(a);
        string provider = NormalizeWorkloadIdentityProvider(a.WorkloadIdentityProvider);
        string sa = a.ServiceAccountEmail.Trim();
        if (supplied != null)
            return CacheKey("wif", provider, sa, "supplied-token", supplied);

        return CacheKey("wif",
            provider,
            sa,
            "client-credentials",
            a.TokenEndpoint?.Trim(),
            a.ClientId,
            a.ClientSecret,
            a.Scope,
            a.Audience);
    }

    // ---- Client factories ------------------------------------------------------------

    /// <summary>
    /// Returns a cached StorageClient for the given credentials, creating it on first use. Every
    /// client gets the StorageRequestTimeout. Honors the GCSCONNECTOR_EMULATOR_HOST environment
    /// variable (never set on a real ODC server): when present, connects unauthenticated to a local
    /// GCS emulator such as fake-gcs-server, enabling integration tests without Google credentials.
    /// The name is deliberately extension-specific (not Google's STORAGE_EMULATOR_HOST) so a
    /// machine-wide variable set for other tooling can never silently redirect this connector.
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
                _ =>
                {
                    var client = new StorageClientBuilder { BaseUri = baseUri, UnauthenticatedAccess = true }.Build();
                    client.Service.HttpClient.Timeout = StorageRequestTimeout;
                    return client;
                },
                DateTime.MaxValue);
        }

        if (method == AuthMethod.WorkloadIdentityFederation)
        {
            var identity = GetFederatedIdentity(authentication);
            return StorageClientCache.GetOrAdd(FederatedCacheKey(authentication), _ =>
            {
                var builder = new StorageClientBuilder { GoogleCredential = identity.Credential };
                if (HttpClientFactoryOverride != null) builder.HttpClientFactory = HttpClientFactoryOverride;
                var client = builder.Build();
                client.Service.HttpClient.Timeout = StorageRequestTimeout;
                return client;
            }, identity.ExpiresUtc);
        }

        return StorageClientCache.GetOrAdd(
            CacheKey("key", authentication.ClientEmail, authentication.PrivateKey),
            _ =>
            {
                var client = StorageClient.Create(GetServiceAccountCredential(authentication).ToGoogleCredential());
                client.Service.HttpClient.Timeout = StorageRequestTimeout;
                return client;
            },
            DateTime.MaxValue);
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
            return UrlSignerCache.GetOrAdd(FederatedCacheKey(authentication), _ => UrlSigner.FromCredential(identity.Credential), identity.ExpiresUtc);
        }

        return UrlSignerCache.GetOrAdd(
            CacheKey("key", authentication.ClientEmail, authentication.PrivateKey),
            _ => UrlSigner.FromCredential(GetServiceAccountCredential(authentication)),
            DateTime.MaxValue);
    }

    /// <summary>
    /// The one ServiceAccountCredential for these key credentials, parsed once and shared by the
    /// StorageClient and the UrlSigner: each only reads the key, so they cannot interfere, and the
    /// PEM is not parsed again for every action.
    /// </summary>
    private static ServiceAccountCredential GetServiceAccountCredential(Authentication authentication) =>
        ServiceAccountCredentialCache.GetOrAdd(
            CacheKey("key", authentication.ClientEmail, authentication.PrivateKey),
            _ => CreateServiceAccountCredential(authentication),
            DateTime.MaxValue);

    private static ServiceAccountCredential CreateServiceAccountCredential(Authentication authentication)
    {
        try
        {
            var initializer = new ServiceAccountCredential.Initializer(authentication.ClientEmail)
            {
                Scopes = [StorageService.Scope.CloudPlatform]
            }.FromPrivateKey(authentication.PrivateKey.Replace("\\n", "\n"));

            return new ServiceAccountCredential(initializer);
        }
        catch (Exception e)
        {
            throw new ArgumentException("The PrivateKey could not be parsed. Provide the full 'private_key' value from the service account JSON key, including the -----BEGIN PRIVATE KEY----- and -----END PRIVATE KEY----- lines.", e);
        }
    }

    // ---- Workload Identity Federation ------------------------------------------------

    /// <summary>The caller-supplied JWT, trimmed, or null when the client-credentials grant is used.</summary>
    internal static string? SuppliedToken(Authentication a)
    {
        string token = (a.SubjectToken ?? string.Empty).Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// The absolute expiry of the cached federated entries: the supplied token's exp, so nothing is
    /// reused past the identity it was built from, or five minutes from now when the token has no
    /// readable exp. Client-credentials entries expire on the sliding window only.
    /// </summary>
    internal static DateTime FederatedExpiry(Authentication a)
    {
        string? supplied = SuppliedToken(a);
        if (supplied == null) return DateTime.MaxValue;

        return TryReadJwtExpiry(supplied, out var expiry) ? expiry : DateTime.UtcNow.AddMinutes(5);
    }

    /// <summary>
    /// Rejects a supplied token whose exp has already passed (allowing 30 seconds of clock skew),
    /// before it reaches Google STS. A token without a readable exp is left for Google to judge.
    /// </summary>
    private static void ValidateSubjectTokenExpiry(string token)
    {
        if (TryReadJwtExpiry(token, out var expiry) && expiry < DateTime.UtcNow.AddSeconds(-30))
            throw new ArgumentException($"The SubjectToken has expired (exp {expiry.ToString("u", CultureInfo.InvariantCulture)}). Supply a fresh token.");
    }

    /// <summary>
    /// Reads the exp claim of a JWT payload (Unix seconds, integer or fraction) without validating the
    /// signature. Returns false, never throws, when the token is not a readable three-part JWT with a
    /// JSON object payload and a numeric exp.
    /// </summary>
    internal static bool TryReadJwtExpiry(string? jwt, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (string.IsNullOrEmpty(jwt)) return false;

        var parts = jwt.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            int padding = payload.Length % 4;
            if (padding == 1) return false;
            if (padding == 2) payload += "==";
            else if (padding == 3) payload += "=";

            byte[] bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (!doc.RootElement.TryGetProperty("exp", out var expElement)) return false;
            if (expElement.ValueKind != JsonValueKind.Number) return false;

            if (!expElement.TryGetDouble(out double seconds)) return false;
            if (double.IsNaN(seconds) || seconds < 0 || seconds > 253402300799d) return false;

            expiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A federated credential plus the source of its identity-provider token. The credential chain is
    /// external account (subject token -> Google STS) -> impersonated service account (IAM Credentials).
    /// The SDK refreshes both automatically, calling back into the token source only when needed.
    /// ExpiresUtc is the absolute expiry shared with the cached StorageClient and UrlSigner.
    /// </summary>
    private sealed class FederatedIdentity
    {
        public FederatedIdentity(GoogleCredential credential, SubjectTokenSource tokenSource, DateTime expiresUtc)
        {
            Credential = credential;
            TokenSource = tokenSource;
            ExpiresUtc = expiresUtc;
        }

        public GoogleCredential Credential { get; }
        public SubjectTokenSource TokenSource { get; }
        public DateTime ExpiresUtc { get; }
    }

    private static FederatedIdentity GetFederatedIdentity(Authentication a)
    {
        // The expiry check runs before the cache lookup and before any network call: an expired
        // token must never be exchanged, and a cached identity built from it must not be reused.
        string? supplied = SuppliedToken(a);
        if (supplied != null)
            ValidateSubjectTokenExpiry(supplied);

        return FederatedIdentityCache.GetOrAdd(
            FederatedCacheKey(a),
            _ => CreateFederatedIdentity(a),
            FederatedExpiry(a));
    }

    private static FederatedIdentity CreateFederatedIdentity(Authentication a)
    {
        var tokenSource = new SubjectTokenSource(SuppliedToken(a), a.TokenEndpoint?.Trim(), a.ClientId, a.ClientSecret, a.Scope, a.Audience);

        var externalInitializer = new ProgrammaticExternalAccountCredential.Initializer(
            StsTokenUrl, NormalizeWorkloadIdentityProvider(a.WorkloadIdentityProvider), JwtSubjectTokenType, tokenSource)
        {
            Scopes = [StorageService.Scope.CloudPlatform]
        };
        if (HttpClientFactoryOverride != null) externalInitializer.HttpClientFactory = HttpClientFactoryOverride;
        var federated = GoogleCredential.FromProgrammaticExternalAccountCredential(new ProgrammaticExternalAccountCredential(externalInitializer));

        var impersonationInitializer = new ImpersonatedCredential.Initializer(a.ServiceAccountEmail.Trim())
        {
            Scopes = [StorageService.Scope.CloudPlatform]
        };
        if (HttpClientFactoryOverride != null) impersonationInitializer.HttpClientFactory = HttpClientFactoryOverride;

        return new FederatedIdentity(federated.Impersonate(impersonationInitializer), tokenSource, FederatedExpiry(a));
    }

    /// <summary>
    /// Supplies the identity-provider JWT to Google's STS exchange: either the token the caller passed
    /// in (SubjectToken), fixed when this source is created and never replaced, so one caller's token
    /// is never exchanged under another caller's identity; or one obtained with the standard OAuth 2.0
    /// client-credentials grant (RFC 6749 section 4.4), which is the same call on Entra ID, Okta, Auth0,
    /// Keycloak and others, cached until shortly before it expires.
    /// </summary>
    private sealed class SubjectTokenSource : ProgrammaticExternalAccountCredential.ISubjectTokenProvider
    {
        private readonly string? _suppliedToken;
        private readonly string? _tokenEndpoint;
        private readonly string? _clientId;
        private readonly string? _clientSecret;
        private readonly string? _scope;
        private readonly string? _audience;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _cachedToken;
        private DateTimeOffset _cachedUntil;
        private bool _useBasicClientAuth;

        /// <summary>When the cached client-credentials token is refreshed (test seam).</summary>
        internal DateTimeOffset CachedUntil => _cachedUntil;

        /// <summary>The token endpoint for messages: scheme, host and path only, never query or user info.</summary>
        private string EndpointDisplay =>
            Uri.TryCreate(_tokenEndpoint, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Path)
                : (_tokenEndpoint ?? string.Empty);

        public SubjectTokenSource(string? suppliedToken, string? tokenEndpoint, string? clientId, string? clientSecret, string? scope, string? audience)
        {
            _suppliedToken = suppliedToken;
            _tokenEndpoint = tokenEndpoint;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _scope = scope;
            _audience = audience;
        }

        public async Task<string> GetSubjectTokenAsync(ProgrammaticExternalAccountCredential caller, CancellationToken taskCancellationToken)
        {
            if (!string.IsNullOrEmpty(_suppliedToken))
                return _suppliedToken;

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
            HttpClient http;
            if (HttpClientFactoryOverride != null)
            {
                // Test seam: the factory returns a Google client whose message handler follows
                // redirects by default; mirror the production client, which never follows them.
                var client = (ConfigurableHttpClient)HttpClientFactoryOverride.CreateHttpClient(new CreateHttpClientArgs());
                client.MessageHandler.FollowRedirect = false;
                http = client;
            }
            else
            {
                http = IdentityProviderHttp.Value;
            }

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
                    // A cancelled caller token is not a timeout: propagate it unchanged.
                    if (ct.IsCancellationRequested)
                        throw;
                    if (e is TaskCanceledException)
                        throw new IdentityProviderException($"The identity provider token endpoint '{EndpointDisplay}' did not respond within 30 seconds.", e);
                    throw new IdentityProviderException($"Could not reach the identity provider token endpoint '{EndpointDisplay}': {e.Message}", e);
                }

                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    int statusCode = (int)response.StatusCode;
                    if (statusCode >= 300 && statusCode < 400)
                        throw new IdentityProviderException($"The identity provider token endpoint redirected ({statusCode}). Configure the final token URL in TokenEndpoint.");

                    if (response.IsSuccessStatusCode)
                    {
                        var (token, expiresIn) = ParseTokenResponse(body);
                        if (!LooksLikeJwt(token))
                            throw new IdentityProviderException("The identity provider returned a token that is not a JWT, but Workload Identity Federation requires a signed JWT. For Entra ID, request a token for your own app registration (Scope 'api://YOUR_APP_ID/.default'), not Microsoft Graph; for Okta, use a custom authorization server.");
                        if (basic) _useBasicClientAuth = true;

                        // expires_in can outlive the JWT itself, and STS rejects a token that is about
                        // to expire: use the earlier of the two, with a 30-second floor for the retry.
                        TimeSpan lifetime = TimeSpan.FromSeconds(expiresIn);
                        if (TryReadJwtExpiry(token, out var tokenExpiry))
                        {
                            TimeSpan untilExpiry = tokenExpiry - DateTime.UtcNow;
                            if (untilExpiry < lifetime) lifetime = untilExpiry;
                            if (lifetime < TimeSpan.FromSeconds(30)) lifetime = TimeSpan.FromSeconds(30);
                        }
                        return (token, lifetime);
                    }

                    // Detail is the OAuth error fields when the body is JSON; otherwise only
                    // metadata. The raw body is never echoed (it may contain secrets or HTML).
                    string detail;
                    if (ParseOAuthError(body, out var error, out var description))
                    {
                        if (description != null && description.Length > 300) description = description[..300] + "...";
                        detail = error == null && description == null
                            ? "(the response body contained no 'error' or 'error_description')"
                            : error == null ? description! : description == null ? error : $"{error}: {description}";
                    }
                    else
                    {
                        string contentType = response.Content.Headers.ContentType != null
                            ? response.Content.Headers.ContentType.ToString()
                            : "none";
                        detail = $"(non-JSON response, {Encoding.UTF8.GetByteCount(body)} bytes, Content-Type {contentType})";
                    }

                    bool invalidClient = response.StatusCode == HttpStatusCode.Unauthorized || error == "invalid_client";
                    if (!basic && invalidClient && attempt == 0)
                        continue;

                    string statusText = error ?? response.StatusCode.ToString();
                    throw new IdentityProviderException($"The identity provider rejected the client-credentials token request ({(int)response.StatusCode} {statusText}): {detail}. Check TokenEndpoint, ClientId, ClientSecret, and Scope/Audience.");
                }
            }
        }

        private static (string token, int expiresIn) ParseTokenResponse(string body)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new IdentityProviderException("The identity provider's token response was not valid JSON.", e);
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new IdentityProviderException("The identity provider's token response was not valid JSON.");

                var root = doc.RootElement;
                string? token = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    token = root.TryGetProperty("id_token", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    throw new IdentityProviderException("The identity provider's token response did not contain an 'access_token'.");

                int expiresIn = 3600;
                if (root.TryGetProperty("expires_in", out var ei))
                {
                    if (ei.ValueKind == JsonValueKind.Number)
                    {
                        if (ei.TryGetInt32(out var n)) expiresIn = n;
                        else if (ei.TryGetDouble(out var d)) expiresIn = (int)d;
                    }
                    else if (ei.ValueKind == JsonValueKind.String && int.TryParse(ei.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                    {
                        expiresIn = s;
                    }
                }
                return (token!, Math.Max(expiresIn, 30));
            }
        }

        private static bool ParseOAuthError(string body, out string? error, out string? description)
        {
            error = null;
            description = null;
            if (string.IsNullOrWhiteSpace(body)) return false;

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                var root = doc.RootElement;
                error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                return true;
            }
            catch (JsonException)
            {
                return false;
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

    /// <summary>
    /// Translates a failure while signing a URL (federation: the signBlob call) into an actionable
    /// message. The Token Creator hint is only right for the 403 that means the role is missing;
    /// other statuses get the status code and Google's error message instead.
    /// </summary>
    private static Exception FriendlySigningException(Google.GoogleApiException e, Authentication a)
    {
        string details = e.Error != null && !string.IsNullOrEmpty(e.Error.Message) ? e.Error.Message : e.Message;
        if (ResolveAuthMethodSafe(a) == AuthMethod.WorkloadIdentityFederation)
        {
            if (e.HttpStatusCode == HttpStatusCode.Forbidden)
                return new Exception($"Signing the URL failed: service account '{a.ServiceAccountEmail}' could not sign through the IAM Credentials API. Grant the federated identity 'Service Account Token Creator' on the service account. Details: {details}", e);
            return new Exception($"Signing the URL failed for service account '{a.ServiceAccountEmail}' ({(int)e.HttpStatusCode}): {details}", e);
        }
        return new Exception($"Signing the URL failed for service account '{a.ClientEmail}'. Details: {details}", e);
    }
}
