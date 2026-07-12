using System.Security.Cryptography;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Shared helpers. The private key is a throwaway RSA key generated in-memory on first use
/// (V4 URL signing is local cryptography), so the offline tests need no Google account,
/// no credentials, and no external tooling such as openssl.
/// </summary>
internal static class TestSupport
{
    public const string ProjectId = "fake-project";
    public const string ClientEmail = "test-sa@fake-project.iam.gserviceaccount.com";

    /// <summary>A valid, parseable PKCS#8 RSA private key that belongs to no real service account.</summary>
    public static string PrivateKeyPem { get; } = GenerateKey();

    private static string GenerateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    public static Authentication Auth(string? clientEmail = null, string? privateKey = null) => new()
    {
        ProjectId = ProjectId,
        ClientEmail = clientEmail ?? ClientEmail,
        PrivateKey = privateKey ?? PrivateKeyPem
    };
}
