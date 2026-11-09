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

    public sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, string Body, byte[] Content, IReadOnlyDictionary<string, string> Headers)
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

    /// <summary>Optional custom IdP response generator or override.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? IdpResponseOverride { get; set; }

    /// <summary>Optional exception thrown when reaching the IdP endpoint.</summary>
    public Exception? IdpException { get; set; }

    /// <summary>Lifetime in seconds returned by the IdP (default 3600).</summary>
    public int IdpExpiresIn { get; set; } = 3600;

    /// <summary>
    /// When true, the resumable-upload endpoint flips one bit of the received data, as a transit
    /// error would. Like the real server, it then rejects the upload if the client's x-goog-hash
    /// CRC32C no longer matches.
    /// </summary>
    public bool CorruptUploadInTransit { get; set; }

    /// <summary>Objects successfully stored by the fake upload endpoint, by "bucket/name".</summary>
    public Dictionary<string, byte[]> StoredObjects { get; } = new();

    /// <summary>Metadata of one object the fake Storage API serves, as "bucket/name".</summary>
    public sealed record FakeObject(long Generation, long Metageneration, string ContentType = "text/plain", long Size = 5);

    /// <summary>Objects served by `GET /storage/v1/b/{bucket}/o/{object}` (and accepted by PUT).</summary>
    public Dictionary<string, FakeObject> Objects { get; } = new();

    /// <summary>
    /// Keys (as "bucket/name") of objects stored gzip-compressed in <see cref="StoredObjects"/>. Like real
    /// GCS under decompressive transcoding, a media GET serves the decompressed bytes with
    /// `x-goog-stored-content-encoding: gzip` and the CRC32C of the compressed bytes.
    /// </summary>
    public HashSet<string> GzipTranscoded { get; } = new();

    /// <summary>When true, every object update (`PUT`/`PATCH .../o/{object}`) answers 412 Precondition Failed.</summary>
    public bool FailObjectUpdateWithPrecondition { get; set; }

    /// <summary>Optional custom Storage response override evaluated before the built-in storage routes.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? StorageResponseOverride { get; set; }

    private readonly Dictionary<string, (string Bucket, string Name)> _uploadSessions = new();

    public IEnumerable<Recorded> To(string hostOrPathFragment) =>
        Requests.Where(r => r.Uri.ToString().Contains(hostOrPathFragment, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] content = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Content?.Headers.ContentEncoding.Contains("gzip") == true)
        {
            // Google's client gzips JSON request bodies (e.g. the upload session's metadata).
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(content), System.IO.Compression.CompressionMode.Decompress);
            using var plain = new MemoryStream();
            await gzip.CopyToAsync(plain, cancellationToken);
            content = plain.ToArray();
        }
        string body = Encoding.UTF8.GetString(content);
        string? auth = request.Headers.Authorization?.ToString();
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        lock (Requests) Requests.Add(new Recorded(request.Method, request.RequestUri!, auth, body, content, headers));

        string url = request.RequestUri!.ToString();

        if (url.StartsWith(IdpTokenEndpoint, StringComparison.Ordinal))
        {
            if (IdpException != null)
                throw IdpException;
            if (IdpResponseOverride?.Invoke(request) is { } custom)
                return custom;

            bool usedBasic = auth?.StartsWith("Basic ", StringComparison.Ordinal) == true;
            if (IdpRejectsClient || (IdpRequiresBasicAuth && !usedBasic))
                return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "Client authentication failed." });
            return Json(HttpStatusCode.OK, new { access_token = IdpToken, token_type = "Bearer", expires_in = IdpExpiresIn });
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

        if (url.StartsWith("https://storage.googleapis.com/", StringComparison.Ordinal) && StorageResponseOverride is { } storageHook)
        {
            // Run the hook off the request thread and stop waiting when the request is cancelled, as a real
            // slow server would be: a hook that sleeps cannot hold a cancelled request open.
            var hookResult = await Task.Run(() => storageHook(request), CancellationToken.None).WaitAsync(cancellationToken);
            if (hookResult is { } storageOverride)
                return storageOverride;
        }

        if (url.StartsWith("https://storage.googleapis.com/upload/storage/v1/b/", StringComparison.Ordinal))
            return ResumableUpload(request, url, content, headers);

        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Object rewrite (copy): POST /storage/v1/b/{sb}/o/{so}/rewriteTo/b/{db}/o/{do}
        if (url.StartsWith("https://storage.googleapis.com/storage/v1/b/", StringComparison.Ordinal)
            && request.Method == HttpMethod.Post
            && segments.Length == 11 && segments[4] == "o" && segments[6] == "rewriteTo" && segments[7] == "b" && segments[9] == "o")
        {
            string srcBucket = segments[3];
            string srcObject = Uri.UnescapeDataString(segments[5]);
            string dstBucket = segments[8];
            string dstObject = Uri.UnescapeDataString(segments[10]);
            string srcKey = srcBucket + "/" + srcObject;
            string dstKey = dstBucket + "/" + dstObject;

            var rewriteQuery = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (!Objects.TryGetValue(srcKey, out var srcExisting))
                return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"No such object: {srcKey}" } });
            if (rewriteQuery["ifSourceGenerationMatch"] is { } expectedSource
                && expectedSource != srcExisting.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return Json(HttpStatusCode.PreconditionFailed, new { error = new { code = 412, message = "At least one of the pre-conditions you specified did not hold." } });

            // The destination is a new object, so it gets a new generation.
            FakeObject copied = srcExisting with { Generation = srcExisting.Generation + 1000 };
            lock (Objects) Objects[dstKey] = copied;
            return Json(HttpStatusCode.OK, new
            {
                kind = "storage#rewriteResponse",
                done = true,
                resource = ObjectJson(dstBucket, dstObject, copied)
            });
        }

        // Object list: GET /storage/v1/b/{b}/o?prefix=...
        if (url.StartsWith("https://storage.googleapis.com/storage/v1/b/", StringComparison.Ordinal)
            && request.Method == HttpMethod.Get
            && segments.Length == 5 && segments[4] == "o")
        {
            string bucket = segments[3];
            var listQuery = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string prefix = listQuery["prefix"] ?? "";
            string bucketPrefix = bucket + "/" + prefix;
            string? pageToken = listQuery["pageToken"];
            int maxResults = int.TryParse(listQuery["maxResults"], out var parsedMax) && parsedMax > 0 ? parsedMax : int.MaxValue;
            List<(string Name, FakeObject Obj)> matching;
            lock (Objects)
            {
                // Like GCS, a page token is positional: it resumes after the last name returned.
                matching = Objects
                    .Where(kv => kv.Key.StartsWith(bucketPrefix, StringComparison.Ordinal))
                    .Select(kv => (Name: kv.Key[(bucket.Length + 1)..], Obj: kv.Value))
                    .Where(o => pageToken == null || string.CompareOrdinal(o.Name, pageToken) > 0)
                    .OrderBy(o => o.Name, StringComparer.Ordinal)
                    .ToList();
            }
            var page = matching.Take(maxResults).ToList();
            object[] items = page.Select(o => ObjectJson(bucket, o.Name, o.Obj)).ToArray();
            string? nextPageToken = matching.Count > page.Count ? page[^1].Name : null;
            var listing = new Dictionary<string, object> { ["kind"] = "storage#objects", ["items"] = items };
            if (nextPageToken != null) listing["nextPageToken"] = nextPageToken;
            return Json(HttpStatusCode.OK, listing);
        }

        // Media download (the SDK's alt=media GET): serves the stored bytes with the headers the SDK validates.
        if (url.StartsWith("https://storage.googleapis.com/", StringComparison.Ordinal)
            && request.Method == HttpMethod.Get
            && System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["alt"] == "media"
            && Array.IndexOf(segments, "b") is var bIndex and >= 0 && segments.Length > bIndex + 3 && segments[bIndex + 2] == "o")
        {
            string mediaKey = segments[bIndex + 1] + "/" + Uri.UnescapeDataString(string.Join('/', segments.Skip(bIndex + 3)));
            byte[]? stored;
            lock (StoredObjects) StoredObjects.TryGetValue(mediaKey, out stored);
            if (stored == null || !Objects.TryGetValue(mediaKey, out var mediaObject))
                return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"No such object: {mediaKey}" } });

            bool transcoded = GzipTranscoded.Contains(mediaKey);
            byte[] served = stored;
            if (transcoded)
            {
                using var gunzip = new System.IO.Compression.GZipStream(new MemoryStream(stored), System.IO.Compression.CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gunzip.CopyTo(plain);
                served = plain.ToArray();
            }

            var media = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) };
            media.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaObject.ContentType);
            // The hash is always of the stored bytes: for a transcoded object that is the compressed form.
            media.Headers.TryAddWithoutValidation("x-goog-hash", "crc32c=" + Crc32c.Base64(stored));
            media.Headers.TryAddWithoutValidation("x-goog-generation", mediaObject.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (transcoded) media.Headers.TryAddWithoutValidation("x-goog-stored-content-encoding", "gzip");

            // The SDK's CRC32C check is a response-stream interceptor that Google's real HTTP handler chain
            // applies; this fake replaces that chain, so feed the served bytes to the interceptor here.
            if (request.Options.TryGetValue(new HttpRequestOptionsKey<Func<HttpResponseMessage, StreamInterceptor>>(ConfigurableMessageHandler.ResponseStreamInterceptorProviderKey), out var interceptorProvider)
                && interceptorProvider(media) is { } interceptor)
                interceptor(served, 0, served.Length);
            return media;
        }

        // Object metadata & delete: GET, PUT, PATCH, DELETE /storage/v1/b/{b}/o/{o}
        if (url.StartsWith("https://storage.googleapis.com/storage/v1/b/", StringComparison.Ordinal)
            && segments.Length > 5 && segments[4] == "o")
        {
            string bucket = segments[3];
            string objectName = Uri.UnescapeDataString(string.Join('/', segments.Skip(5)));
            string key = bucket + "/" + objectName;

            if (request.Method == HttpMethod.Delete)
            {
                lock (Objects)
                {
                    if (!Objects.TryGetValue(key, out var doomed))
                        return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"No such object: {key}" } });
                    if (System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["ifGenerationMatch"] is { } expectedGeneration
                        && expectedGeneration != doomed.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        return Json(HttpStatusCode.PreconditionFailed, new { error = new { code = 412, message = "At least one of the pre-conditions you specified did not hold." } });
                    Objects.Remove(key);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get)
            {
                if (!Objects.TryGetValue(key, out var existing))
                    return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"No such object: {key}" } });
                return Json(HttpStatusCode.OK, ObjectJson(bucket, objectName, existing));
            }

            if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch)
            {
                if (FailObjectUpdateWithPrecondition)
                    return Json(HttpStatusCode.PreconditionFailed, new { error = new { code = 412, message = "At least one of the pre-conditions you specified did not hold." } });
                if (!Objects.TryGetValue(key, out var existing))
                    return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"No such object: {key}" } });

                string contentType = existing.ContentType;
                if (content.Length > 0)
                {
                    using var update = JsonDocument.Parse(content);
                    if (update.RootElement.TryGetProperty("contentType", out var ct) && ct.GetString() is { Length: > 0 } updated)
                        contentType = updated;
                }
                var stored = existing with { Metageneration = existing.Metageneration + 1, ContentType = contentType };
                lock (Objects) Objects[key] = stored;
                return Json(HttpStatusCode.OK, ObjectJson(bucket, objectName, stored));
            }
        }

        if (url.StartsWith("https://storage.googleapis.com/storage/v1/b/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            var bucket = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            return Json(HttpStatusCode.OK, new { kind = "storage#bucket", name = bucket, id = bucket });
        }

        return Json(HttpStatusCode.NotFound, new { error = new { code = 404, message = $"FakeFederationBackend has no route for {request.Method} {url}" } });
    }

    /// <summary>
    /// The object resource. Like Google, it carries the CRC32C of the stored bytes (the compressed form for
    /// a gzip-stored object) when the fake holds them; the SDK validates a download against it.
    /// </summary>
    private object ObjectJson(string bucket, string name, FakeObject obj)
    {
        var resource = new Dictionary<string, object>
        {
            ["kind"] = "storage#object",
            ["bucket"] = bucket,
            ["name"] = name,
            ["size"] = obj.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["contentType"] = obj.ContentType,
            ["generation"] = obj.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["metageneration"] = obj.Metageneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        byte[]? stored;
        lock (StoredObjects) StoredObjects.TryGetValue(bucket + "/" + name, out stored);
        if (stored != null) resource["crc32c"] = Crc32c.Base64(stored);
        return resource;
    }

    /// <summary>
    /// Google's resumable upload: POST starts a session (metadata JSON, Location header back), then
    /// a PUT carries the bytes. The client sends x-goog-hash on the final request; a mismatch is
    /// rejected with 400 and the same message shape the real service uses, and nothing is stored.
    /// </summary>
    private HttpResponseMessage ResumableUpload(HttpRequestMessage request, string url, byte[] content, Dictionary<string, string> headers)
    {
        if (request.Method == HttpMethod.Post)
        {
            var bucket = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[4];
            using var meta = JsonDocument.Parse(content);
            var name = meta.RootElement.GetProperty("name").GetString()!;
            var session = Guid.NewGuid().ToString("N");
            lock (_uploadSessions) _uploadSessions[session] = (bucket, name);
            var started = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            started.Headers.Location = new Uri($"https://storage.googleapis.com/upload/storage/v1/b/{bucket}/o?uploadType=resumable&upload_id={session}");
            return started;
        }

        var uploadId = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["upload_id"]!;
        (string Bucket, string Name) target;
        lock (_uploadSessions) target = _uploadSessions[uploadId];

        byte[] received = (byte[])content.Clone();
        if (CorruptUploadInTransit && received.Length > 0) received[received.Length / 2] ^= 0x01;
        string calculated = Crc32c.Base64(received);

        if (headers.TryGetValue("x-goog-hash", out var hash))
        {
            var provided = hash.Split(',').Select(h => h.Trim()).FirstOrDefault(h => h.StartsWith("crc32c=", StringComparison.Ordinal))?["crc32c=".Length..];
            if (provided != null && provided != calculated)
            {
                var message = $"Provided CRC32C \"{provided}\" doesn't match calculated CRC32C \"{calculated}\".";
                return Json(HttpStatusCode.BadRequest, new { error = new { code = 400, message, errors = new[] { new { message, domain = "global", reason = "invalid" } } } });
            }
        }

        lock (StoredObjects) StoredObjects[$"{target.Bucket}/{target.Name}"] = received;
        return Json(HttpStatusCode.OK, new { kind = "storage#object", bucket = target.Bucket, name = target.Name, size = received.Length.ToString(), crc32c = calculated });
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

/// <summary>
/// Reference CRC32C (Castagnoli), as Google Cloud Storage computes it: base64 of the big-endian
/// 32-bit value. Independent of the SDK's implementation so the tests check the real wire value.
/// </summary>
internal static class Crc32c
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static string Base64(byte[] data)
    {
        uint v = Compute(data);
        return Convert.ToBase64String([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
    }
}
