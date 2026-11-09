using System.Net;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using OutSystems.ExternalLibraries.SDK.FileStorage.Exceptions;
using Xunit;
using Authentication = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Authentication;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using MetadataEntry = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.MetadataEntry;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Object_Upload and Object_Download with ODC's File type (2.0.0): content streams between ODC storage
/// (an in-memory fake installed through FileStorage.Configure) and the offline fake of Google. Offline.
/// </summary>
public sealed class FileTransferTests : IDisposable
{
    private readonly IGoogleCloudStorage _sut = new Connector();
    private readonly FakeFederationBackend _backend = new();
    private readonly FakeFileStorage _files = new();
    private readonly string? _savedEmulatorHost;
    private readonly int _savedChunk;
    private readonly TimeSpan _savedDeadline;
    private readonly TimeSpan[] _savedDelays;

    public FileTransferTests()
    {
        _savedEmulatorHost = Connector.EmulatorBaseUri;
        _savedChunk = Connector.UploadChunkSize;
        _savedDeadline = Connector.FileTransferDeadline;
        _savedDelays = Connector.TransferRetryDelays;
        Connector.EmulatorBaseUri = null;
        Connector.HttpClientFactoryOverride = new FakeHttpClientFactory(_backend);
        Connector.TransferRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
        FileStorage.Configure(() => _files);
    }

    public void Dispose()
    {
        FileStorage.Reset();
        Connector.HttpClientFactoryOverride = null;
        Connector.EmulatorBaseUri = _savedEmulatorHost;
        Connector.UploadChunkSize = _savedChunk;
        Connector.FileTransferDeadline = _savedDeadline;
        Connector.TransferRetryDelays = _savedDelays;
    }

    private static Authentication Federated() => new()
    {
        ProjectId = "proj",
        AuthenticationMethod = "WorkloadIdentityFederation",
        WorkloadIdentityProvider = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/x",
        ServiceAccountEmail = $"sa-{Guid.NewGuid():N}"[..20] + "@proj.iam.gserviceaccount.com",
        SubjectToken = "header.payload.signature"
    };

    private static byte[] Bytes(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private void StoreInGoogle(string key, byte[] data, string contentType)
    {
        _backend.StoredObjects[key] = data;
        _backend.Objects[key] = new FakeFederationBackend.FakeObject(1, 1, contentType, data.Length);
    }

    // ---- Object_Upload ----------------------------------------------------------------

    [Fact]
    public void Upload_streams_the_file_in_chunks_and_stores_the_exact_bytes()
    {
        Connector.UploadChunkSize = 1024 * 1024;
        var data = Bytes(3 * 1024 * 1024 + 500);
        var file = _files.Add(data, "application/pdf");

        _sut.Object_Upload(Federated(), "my-bucket", "docs/report.pdf", file, "", Array.Empty<MetadataEntry>());

        Assert.Equal(data, _backend.StoredObjects["my-bucket/docs/report.pdf"]);
        Assert.Equal(4, _backend.UploadChunkCounts["my-bucket/docs/report.pdf"]);
        Assert.Equal("application/pdf", _backend.UploadedContentTypes["my-bucket/docs/report.pdf"]);
    }

    [Fact]
    public void Upload_uses_the_content_type_input_over_the_files_own()
    {
        var file = _files.Add(Bytes(1000), "application/octet-stream");

        _sut.Object_Upload(Federated(), "my-bucket", "a.csv", file, "text/csv", Array.Empty<MetadataEntry>());

        Assert.Equal("text/csv", _backend.UploadedContentTypes["my-bucket/a.csv"]);
    }

    [Fact]
    public void Upload_of_an_empty_file_reference_is_refused_before_any_request()
    {
        var nullFile = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Upload(Federated(), "my-bucket", "a.bin", null!, "", Array.Empty<MetadataEntry>()));
        var noKey = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Upload(Federated(), "my-bucket", "a.bin", new FakeFile { Key = "", ContentType = "x/y" }, "", Array.Empty<MetadataEntry>()));

        Assert.Contains("File cannot be empty", nullFile.Message);
        Assert.Contains("File cannot be empty", noKey.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void Upload_rejects_a_content_type_with_a_line_break()
    {
        var file = _files.Add(Bytes(10), "text/plain");

        var ex = Assert.Throws<ArgumentException>(() =>
            _sut.Object_Upload(Federated(), "my-bucket", "a.txt", file, "text/plain\r\nX-Evil: 1", Array.Empty<MetadataEntry>()));

        Assert.Contains("line breaks", ex.Message);
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public void Upload_resumes_after_a_transient_failure_of_a_chunk()
    {
        Connector.UploadChunkSize = 256 * 1024;
        var data = Bytes(600_000);
        var file = _files.Add(data, "application/octet-stream");
        _backend.FailUploadPuts = (HttpStatusCode.ServiceUnavailable, 1);

        _sut.Object_Upload(Federated(), "my-bucket", "resume.bin", file, "", Array.Empty<MetadataEntry>());

        Assert.Equal(data, _backend.StoredObjects["my-bucket/resume.bin"]);
    }

    [Fact]
    public void Upload_retries_a_failed_session_start_by_reopening_the_file()
    {
        var data = Bytes(5000);
        var file = _files.Add(data, "application/octet-stream");
        _backend.FailUploadStarts = (HttpStatusCode.ServiceUnavailable, 1);

        _sut.Object_Upload(Federated(), "my-bucket", "retry.bin", file, "", Array.Empty<MetadataEntry>());

        Assert.Equal(data, _backend.StoredObjects["my-bucket/retry.bin"]);
        Assert.Equal(2, _files.Opens);
    }

    [Fact]
    public void Upload_that_passes_the_deadline_stops_with_a_clear_message_and_stores_nothing()
    {
        Connector.FileTransferDeadline = TimeSpan.FromMilliseconds(300);
        var file = _files.Add(Bytes(200_000), "application/octet-stream");
        _files.ReadDelay = TimeSpan.FromMilliseconds(100);

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Upload(Federated(), "my-bucket", "slow.bin", file, "", Array.Empty<MetadataEntry>()));

        Assert.Contains("did not finish within", ex.Message);
        Assert.Contains("nothing was stored", ex.Message);
        Assert.Contains("Object_GetSignedUrl", ex.Message);
        Assert.False(_backend.StoredObjects.ContainsKey("my-bucket/slow.bin"));
    }

    [Fact]
    public void Upload_reports_an_odc_storage_failure_as_such()
    {
        var file = _files.Add(Bytes(10), "text/plain");
        _files.ReadFailure = new ExpiredFileException("The file has expired.");

        var ex = Assert.ThrowsAny<Exception>(() =>
            _sut.Object_Upload(Federated(), "my-bucket", "a.txt", file, "", Array.Empty<MetadataEntry>()));

        Assert.Contains("ODC file storage failed", ex.Message);
        Assert.Contains("The file has expired.", ex.Message);
    }

    // ---- Object_Download --------------------------------------------------------------

    [Fact]
    public void Download_streams_the_object_into_a_new_file_named_after_it()
    {
        var data = Bytes(2 * 1024 * 1024);
        StoreInGoogle("my-bucket/reports/2026/q1.pdf", data, "application/pdf");

        _sut.Object_Download(Federated(), "my-bucket", "reports/2026/q1.pdf", out var file);

        var created = _files.Get(file);
        Assert.Equal(data, created.Data);
        Assert.Equal("q1.pdf", created.Name);
        Assert.Equal("application/pdf", created.ContentType);
    }

    [Fact]
    public void Download_refuses_an_object_larger_than_an_odc_file_can_hold_before_transferring()
    {
        _backend.Objects["my-bucket/huge.bin"] = new FakeFederationBackend.FakeObject(1, 1, "application/octet-stream", Connector.MaxFileBytes + 1);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(Federated(), "my-bucket", "huge.bin", out _));

        Assert.Contains("2 GB", ex.Message);
        Assert.Contains("Object_GetSignedUrl", ex.Message);
        Assert.Equal(0, _files.Created);
        Assert.DoesNotContain(_backend.Requests, r => r.Uri.Query.Contains("alt=media"));
    }

    [Fact]
    public void Download_of_a_missing_object_says_so_and_creates_no_file()
    {
        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(Federated(), "my-bucket", "missing.bin", out _));

        Assert.Contains("missing.bin", ex.Message);
        Assert.Equal(0, _files.Created);
    }

    [Fact]
    public void Download_that_passes_the_deadline_stops_with_a_clear_message()
    {
        Connector.FileTransferDeadline = TimeSpan.FromMilliseconds(300);
        StoreInGoogle("my-bucket/slow.bin", Bytes(2 * 1024 * 1024), "application/octet-stream");
        _files.WriteDelay = TimeSpan.FromMilliseconds(150);

        var ex = Assert.ThrowsAny<Exception>(() => _sut.Object_Download(Federated(), "my-bucket", "slow.bin", out _));

        Assert.Contains("did not finish within", ex.Message);
        Assert.Contains("Object_GetSignedUrl", ex.Message);
    }

    [Theory]
    [InlineData("reports/2026/q1.pdf", "q1.pdf")]
    [InlineData("a.txt", "a.txt")]
    [InlineData("folder/", "download")]
    [InlineData("/", "download")]
    public void Downloaded_file_names_come_from_the_last_part_of_the_object_name(string objectName, string expected)
    {
        Assert.Equal(expected, Connector.FileNameFor(objectName));
    }

    [Fact]
    public void Downloaded_file_names_are_cut_to_200_characters()
    {
        Assert.Equal(new string('n', 200), Connector.FileNameFor("dir/" + new string('n', 300)));
    }

    // ---- Fake ODC storage -------------------------------------------------------------

    internal sealed class FakeFile : IOSFile
    {
        public string Key { get; set; } = "";
        public string Version { get; set; } = "v1";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
    }

    private sealed class ExpiredFileException(string message) : FileStorageException(message);

    internal sealed record Stored(byte[] Data, string Name, string ContentType);

    /// <summary>ODC's file storage, in memory. Counts reads and creations; can slow or fail them.</summary>
    internal sealed class FakeFileStorage : IFileStorage
    {
        private readonly Dictionary<string, Stored> _files = new();
        public int Opens;
        public int Created;
        public TimeSpan ReadDelay { get; set; }
        public TimeSpan WriteDelay { get; set; }
        public Exception? ReadFailure { get; set; }

        public IOSFile Add(byte[] data, string contentType)
        {
            var key = Guid.NewGuid().ToString("N");
            lock (_files) _files[key] = new Stored(data, "upload.bin", contentType);
            return new FakeFile { Key = key, ContentType = contentType, Size = data.Length };
        }

        public Stored Get(IOSFile file) { lock (_files) return _files[file.Key]; }

        public Task<Stream> ReadFileAsStreamAsync(IOSFile file, CancellationToken cancellationToken = default)
        {
            if (ReadFailure != null) throw ReadFailure;
            Interlocked.Increment(ref Opens);
            return Task.FromResult<Stream>(new SlowStream(new MemoryStream(Get(file).Data), ReadDelay, TimeSpan.Zero));
        }

        public async Task<IOSFile> CreateFileFromWriterAsync(string fileName, string contentType, Func<Stream, CancellationToken, Task> writeAsync, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default)
        {
            var buffer = new MemoryStream();
            await writeAsync(new SlowStream(buffer, TimeSpan.Zero, WriteDelay), cancellationToken);
            Interlocked.Increment(ref Created);
            var key = Guid.NewGuid().ToString("N");
            lock (_files) _files[key] = new Stored(buffer.ToArray(), fileName, contentType);
            return new FakeFile { Key = key, ContentType = contentType, Size = buffer.Length };
        }

        public Task<IOSFile> CreateFileAsync(string fileName, string contentType, byte[] content, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IOSFile> CreateFileFromStreamAsync(string fileName, string contentType, Stream contentStream, IDictionary<string, string>? additionalUserData = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadFileAsync(IOSFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> ReadFileRangeAsync(IOSFile file, long startByte, long endByte, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IFileMetadata> GetFileMetadataAsync(IOSFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A stream that waits before every read or write, to simulate a slow transfer.</summary>
    private sealed class SlowStream(Stream inner, TimeSpan readDelay, TimeSpan writeDelay) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readDelay > TimeSpan.Zero) Thread.Sleep(readDelay);
            return inner.Read(buffer, offset, Math.Min(count, 16 * 1024));
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (readDelay > TimeSpan.Zero) await Task.Delay(readDelay, ct);
            return inner.Read(buffer, offset, Math.Min(count, 16 * 1024));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (readDelay > TimeSpan.Zero) await Task.Delay(readDelay, ct);
            return inner.Read(buffer.Span[..Math.Min(buffer.Length, 16 * 1024)]);
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // Like a slow store, take 16 KB at a time, waiting before each piece.
            for (int at = 0; at < buffer.Length; at += 16 * 1024)
            {
                if (writeDelay > TimeSpan.Zero) await Task.Delay(writeDelay, ct);
                inner.Write(buffer.Span.Slice(at, Math.Min(16 * 1024, buffer.Length - at)));
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
