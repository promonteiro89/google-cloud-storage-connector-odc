using System.Reflection;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Everything that needs no server: V4 signed URLs (local RSA), input validation, and the
/// client/signer caches. Runs anywhere, no credentials and no emulator required — deliberately
/// not part of the emulator collection so an offline-only run never touches the network.
/// </summary>
public class OfflineTests
{
    private readonly IGoogleCloudStorage _sut = new Connector();

    // ---- signed URLs -------------------------------------------------------------------

    [Fact]
    public void SignedUrl_Download_returns_a_v4_url()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/report.pdf", 15, out var url, "Download");
        Assert.Contains("test-bucket", url);
        Assert.Contains("X-Goog-Signature", url);
    }

    [Fact]
    public void SignedUrl_operation_is_case_insensitive()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/report.pdf", 60, out var url, "upload");
        Assert.False(string.IsNullOrEmpty(url));
    }

    [Fact]
    public void SignedUrl_without_ContentType_does_not_sign_content_type_header()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/report.pdf", 60, out var url, "Upload");
        Assert.DoesNotContain("content-type", url.ToLowerInvariant());
    }

    [Fact]
    public void SignedUrl_with_ContentType_binds_it_into_the_signature()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "test-bucket", "docs/new.pdf", 30, out var url, "Upload", "application/pdf");
        Assert.Contains("content-type", url.ToLowerInvariant());
    }

    [Fact]
    public void SignedUrl_invalid_operation_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(TestSupport.Auth(), "b", "o", 5, out _, "Banana"));
    }

    [Fact]
    public void SignedUrl_expiration_zero_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(TestSupport.Auth(), "b", "o", 0, out _, "Download"));
    }

    [Fact]
    public void SignedUrl_expiration_over_seven_days_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(TestSupport.Auth(), "b", "o", 20000, out _, "Download"));
    }

    [Fact]
    public void SignedUrl_exactly_seven_days_is_accepted()
    {
        _sut.Object_GetSignedUrl(TestSupport.Auth(), "b", "o", 10080, out var url, "Download");
        Assert.False(string.IsNullOrEmpty(url));
    }

    [Fact]
    public void SignedUrl_accepts_json_escaped_private_key()
    {
        var escaped = TestSupport.Auth(privateKey: TestSupport.PrivateKeyPem.Replace("\n", "\\n"));
        _sut.Object_GetSignedUrl(escaped, "b", "x.txt", 5, out var url, "Download");
        Assert.False(string.IsNullOrEmpty(url));
    }

    [Fact]
    public void Garbage_private_key_gives_a_friendly_parse_error()
    {
        var bad = TestSupport.Auth(privateKey: "not-a-key");
        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_GetSignedUrl(bad, "b", "o", 5, out _, "Download"));
        Assert.Contains("PrivateKey could not be parsed", ex.Message);
    }

    // ---- validation --------------------------------------------------------------------

    [Fact]
    public void Object_List_rejects_negative_MaxResults()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Object_List(TestSupport.Auth(), "b", "", -1, "", "", out _, out _, out _));
    }

    // ---- caching -----------------------------------------------------------------------
    // The clients/signers are private statics; exercise the same cache the actions use via
    // the internal factory methods.

    private static readonly MethodInfo GetStorageClientMethod =
        typeof(Connector).GetMethod("GetStorageClient", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetUrlSignerMethod =
        typeof(Connector).GetMethod("GetUrlSigner", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void StorageClient_is_cached_for_the_same_credentials()
    {
        var auth = TestSupport.Auth();
        var c1 = GetStorageClientMethod.Invoke(null, [auth]);
        var c2 = GetStorageClientMethod.Invoke(null, [auth]);
        Assert.Same(c1, c2);
    }

    [Fact]
    public void UrlSigner_is_cached_for_the_same_credentials()
    {
        var auth = TestSupport.Auth();
        var s1 = GetUrlSignerMethod.Invoke(null, [auth]);
        var s2 = GetUrlSignerMethod.Invoke(null, [auth]);
        Assert.Same(s1, s2);
    }

    [Fact]
    public void Distinct_credentials_get_distinct_signers()
    {
        // UrlSigner is always keyed by credentials (never short-circuited by the emulator hook),
        // so this holds whether or not an emulator is configured for the run.
        var a = TestSupport.Auth(clientEmail: "one@fake-project.iam.gserviceaccount.com");
        var b = TestSupport.Auth(clientEmail: "two@fake-project.iam.gserviceaccount.com");
        var sa = GetUrlSignerMethod.Invoke(null, [a]);
        var sb = GetUrlSignerMethod.Invoke(null, [b]);
        Assert.NotSame(sa, sb);
    }
}
