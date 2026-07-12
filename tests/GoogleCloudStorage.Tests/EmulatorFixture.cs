using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Xunit;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Boots a local <c>fake-gcs-server</c> (an in-memory Google Cloud Storage emulator) and points
/// the connector at it via the <c>GCSCONNECTOR_EMULATOR_HOST</c> environment variable — the same
/// hook the connector honors, which is never set on a real ODC server. No Google account, no
/// credentials, and no Docker required.
///
/// Resolution order for the emulator:
///   1. If <c>GCSCONNECTOR_EMULATOR_HOST</c> is already set, use that running server as-is.
///   2. If <c>FAKE_GCS_EXE</c> points at a binary, run it.
///   3. Otherwise download the binary for this OS/arch from the official GitHub releases and run it.
///
/// If none of these succeed (e.g. no network), <see cref="Available"/> is false and the
/// integration tests skip — the offline tests still run.
/// </summary>
public sealed class EmulatorFixture : IAsyncLifetime
{
    private const string Version = "1.54.0";
    private const string EnvVar = "GCSCONNECTOR_EMULATOR_HOST";

    private Process? _process;
    private bool _ownsEnvVar;

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "Emulator not started.";

    public async Task InitializeAsync()
    {
        try
        {
            // 1. Respect an externally provided emulator.
            var external = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(external))
            {
                Available = true;
                return;
            }

            var exe = ResolveExecutable();
            var port = FreeTcpPort();

            var psi = new ProcessStartInfo(exe)
            {
                ArgumentList = { "-scheme", "http", "-host", "127.0.0.1", "-port", port.ToString(), "-backend", "memory" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for fake-gcs-server.");

            if (!await WaitForPortAsync("127.0.0.1", port, TimeSpan.FromSeconds(15)))
                throw new TimeoutException($"fake-gcs-server did not start listening on port {port}.");

            // Must be set before the connector creates its first StorageClient.
            Environment.SetEnvironmentVariable(EnvVar, $"127.0.0.1:{port}");
            _ownsEnvVar = true;
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"fake-gcs-server unavailable ({ex.GetType().Name}: {ex.Message}). " +
                         "Integration tests skipped; run with network access, or set GCSCONNECTOR_EMULATOR_HOST / FAKE_GCS_EXE.";
            SafeKill();
        }
    }

    public Task DisposeAsync()
    {
        if (_ownsEnvVar)
            Environment.SetEnvironmentVariable(EnvVar, null);
        SafeKill();
        return Task.CompletedTask;
    }

    // ---- executable resolution ---------------------------------------------------------

    private static string ResolveExecutable()
    {
        var preset = Environment.GetEnvironmentVariable("FAKE_GCS_EXE");
        if (!string.IsNullOrEmpty(preset) && File.Exists(preset))
            return preset;

        var (os, arch, exeName) = PlatformTriple();
        var toolsDir = Environment.GetEnvironmentVariable("FAKE_GCS_DIR")
                       ?? Path.Combine(Path.GetTempPath(), $"fake-gcs-server-{Version}");
        Directory.CreateDirectory(toolsDir);
        var exePath = Path.Combine(toolsDir, exeName);
        if (File.Exists(exePath))
            return exePath;

        var asset = $"fake-gcs-server_{Version}_{os}_{arch}.tar.gz";
        var url = $"https://github.com/fsouza/fake-gcs-server/releases/download/v{Version}/{asset}";
        var tgz = Path.Combine(toolsDir, asset);

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        using (var response = http.GetAsync(url).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var fs = File.Create(tgz);
            response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }

        // `tar` ships with macOS and Linux; on Windows 10+ it is available in System32.
        Run("tar", ["-xzf", tgz, "-C", toolsDir, exeName]);
        File.Delete(tgz);

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Extracted archive did not contain '{exeName}'.", exePath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Run("chmod", ["+x", exePath]);

        return exePath;
    }

    private static (string os, string arch, string exeName) PlatformTriple()
    {
        string os =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Darwin" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
            throw new PlatformNotSupportedException("Unsupported OS for fake-gcs-server.");

        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture {RuntimeInformation.OSArchitecture}.")
        };

        string exeName = os == "Windows" ? "fake-gcs-server.exe" : "fake-gcs-server";
        return (os, arch, exeName);
    }

    // ---- process / port helpers --------------------------------------------------------

    private static void Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{file}'.");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"'{file}' exited with code {p.ExitCode}: {p.StandardError.ReadToEnd()}");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return true;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
        return false;
    }

    private void SafeKill()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }
}

/// <summary>
/// One shared emulator for the whole assembly. Both test classes join this collection, so the
/// emulator (and its environment variable) are set up once and torn down once, and tests within
/// it do not run in parallel — the connector's static client cache is process-wide.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GcsCollection : ICollectionFixture<EmulatorFixture>
{
    public const string Name = "gcs";
}
