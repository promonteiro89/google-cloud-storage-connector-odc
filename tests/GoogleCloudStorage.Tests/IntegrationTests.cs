using System.Text;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;
using Xunit;
using Connector = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.GoogleCloudStorage;
using GcsFile = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;

namespace GoogleCloudStorage.Tests;

/// <summary>
/// Full action lifecycle against a local fake-gcs-server. Every test skips (rather than fails)
/// when the emulator could not be started, so the suite is still green on a machine with no
/// network or Docker — the offline tests carry the load there.
/// </summary>
[Collection(GcsCollection.Name)]
public class IntegrationTests
{
    private readonly EmulatorFixture _emu;
    private readonly IGoogleCloudStorage _sut = new Connector();

    public IntegrationTests(EmulatorFixture emu) => _emu = emu;

    private void RequireEmulator() => Skip.IfNot(_emu.Available, _emu.SkipReason);

    private string NewBucket(string location = "US")
    {
        var name = "itest-" + Guid.NewGuid().ToString("N")[..12];
        _sut.Bucket_Create(TestSupport.Auth(), name, location);
        return name;
    }

    private void Upload(string bucket, string name, byte[] content, string contentType) =>
        _sut.Object_Upload(TestSupport.Auth(), bucket, name, new GcsFile { Content = content, ContentType = contentType });

    private void Cleanup(string bucket)
    {
        try
        {
            _sut.Object_List(TestSupport.Auth(), bucket, "", 0, "", "", out var objects, out _, out _);
            foreach (var o in objects)
                _sut.Object_Delete(TestSupport.Auth(), bucket, o.Name);
            _sut.Bucket_Delete(TestSupport.Auth(), bucket);
        }
        catch { /* best-effort teardown */ }
    }

    // ---- buckets -----------------------------------------------------------------------

    [SkippableFact]
    public void Bucket_lifecycle_create_exists_list_delete()
    {
        RequireEmulator();
        var b1 = NewBucket("US");
        var b2 = NewBucket("EU");
        try
        {
            _sut.Bucket_Exists(TestSupport.Auth(), b1, out var exists);
            Assert.True(exists);

            _sut.Bucket_Exists(TestSupport.Auth(), "itest-missing-" + Guid.NewGuid().ToString("N")[..8], out var missing);
            Assert.False(missing);

            Assert.ThrowsAny<Exception>(() => _sut.Bucket_Create(TestSupport.Auth(), b1, "US"));

            _sut.Bucket_List(TestSupport.Auth(), out var buckets);
            var list = buckets.ToList();
            var names = list.Select(b => b.Name).ToList();
            Assert.Contains(b1, names);
            Assert.Contains(b2, names);
            Assert.All(list.Where(b => b.Name == b1 || b.Name == b2), b => Assert.True(b.Created.Year > 2000));
        }
        finally
        {
            Cleanup(b1);
            Cleanup(b2);
        }

        _sut.Bucket_Exists(TestSupport.Auth(), b1, out var goneAfterDelete);
        Assert.False(goneAfterDelete);
    }

    // ---- upload / download round-trips -------------------------------------------------

    [SkippableFact]
    public void Upload_download_round_trips_all_shapes()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            var rnd = new Random(42);
            var big = new byte[5 * 1024 * 1024];
            rnd.NextBytes(big);
            var photo = new byte[8 * 1024];
            rnd.NextBytes(photo);
            const string unicodeName = "docs/relatório seção.pdf";

            Upload(b, "root.txt", Encoding.UTF8.GetBytes("root content"), "text/plain");
            Upload(b, "empty.bin", [], "application/octet-stream");
            Upload(b, "big.bin", big, "application/octet-stream");
            Upload(b, "img/photo.png", photo, "image/png");
            Upload(b, unicodeName, Encoding.UTF8.GetBytes("pdf-ish"), "application/pdf");

            _sut.Object_Download(TestSupport.Auth(), b, "root.txt", out var text);
            Assert.Equal("root content", Encoding.UTF8.GetString(text.Content));
            Assert.Equal("text/plain", text.ContentType);

            _sut.Object_Download(TestSupport.Auth(), b, "img/photo.png", out var bin);
            Assert.Equal(photo, bin.Content);

            _sut.Object_Download(TestSupport.Auth(), b, "empty.bin", out var empty);
            Assert.Empty(empty.Content);

            _sut.Object_Download(TestSupport.Auth(), b, "big.bin", out var large);
            Assert.Equal(big, large.Content);

            _sut.Object_Download(TestSupport.Auth(), b, unicodeName, out var uni);
            Assert.Equal("pdf-ish", Encoding.UTF8.GetString(uni.Content));
        }
        finally { Cleanup(b); }
    }

    // ---- exists / metadata -------------------------------------------------------------

    [SkippableFact]
    public void Object_Exists_reports_presence()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            Upload(b, "there.txt", Encoding.UTF8.GetBytes("x"), "text/plain");

            _sut.Object_Exists(TestSupport.Auth(), b, "there.txt", out var present);
            Assert.True(present);

            _sut.Object_Exists(TestSupport.Auth(), b, "nope.txt", out var absent);
            Assert.False(absent);
        }
        finally { Cleanup(b); }
    }

    [SkippableFact]
    public void GetMetadata_populates_fields_and_reports_missing()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            Upload(b, "docs/a.txt", Encoding.UTF8.GetBytes("content A"), "text/plain");

            _sut.Object_GetMetadata(TestSupport.Auth(), b, "docs/a.txt", out var exists, out var md);
            Assert.True(exists);
            Assert.Equal("docs/a.txt", md.Name);
            Assert.Equal(b, md.Bucket);
            Assert.Equal(9, md.Size);
            Assert.Equal("text/plain", md.ContentType);
            Assert.True(md.Generation > 0);
            Assert.True(md.TimeCreated.Year > 2000);
            Assert.True(md.Updated.Year > 2000);
            Assert.False(string.IsNullOrEmpty(md.MD5Hash));
            Assert.False(string.IsNullOrEmpty(md.Crc32c));

            _sut.Object_GetMetadata(TestSupport.Auth(), b, "nope.txt", out var missing, out _);
            Assert.False(missing);
        }
        finally { Cleanup(b); }
    }

    // ---- listing: flat, prefix, delimiter, pagination ----------------------------------

    [SkippableFact]
    public void List_flat_prefix_and_delimiter_folders()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            Upload(b, "root.txt", Encoding.UTF8.GetBytes("r"), "text/plain");
            Upload(b, "empty.bin", [], "application/octet-stream");
            Upload(b, "docs/a.txt", Encoding.UTF8.GetBytes("aaa"), "text/plain");
            Upload(b, "docs/b.txt", Encoding.UTF8.GetBytes("bbb"), "text/plain");
            Upload(b, "docs/sub/c.txt", Encoding.UTF8.GetBytes("ccc"), "text/plain");
            Upload(b, "img/photo.png", [1, 2, 3], "image/png");

            // Flat: everything.
            _sut.Object_List(TestSupport.Auth(), b, "", 0, "", "", out var all, out var next, out _);
            var allNames = all.Select(o => o.Name).ToList();
            Assert.Equal(6, allNames.Count);
            Assert.Equal("", next); // full mode -> no continuation token

            // Prefix filter.
            _sut.Object_List(TestSupport.Auth(), b, "docs/", 0, "", "", out var docs, out _, out _);
            Assert.Equal(3, docs.Count());

            // Delimiter at root: only root-level objects, folders come back as prefixes.
            _sut.Object_List(TestSupport.Auth(), b, "", 0, "", "/", out var top, out _, out var topPrefixes);
            var topNames = top.Select(o => o.Name).ToList();
            var folders = topPrefixes.Select(p => p.Value).ToList();
            Assert.Equal(2, topNames.Count);
            Assert.Contains("root.txt", topNames);
            Assert.Contains("empty.bin", topNames);
            Assert.Equal(2, folders.Count);
            Assert.Contains("docs/", folders);
            Assert.Contains("img/", folders);

            // Prefix + delimiter: direct children of docs/ only.
            _sut.Object_List(TestSupport.Auth(), b, "docs/", 0, "", "/", out var docTop, out _, out var docPrefixes);
            Assert.Equal(2, docTop.Count()); // docs/a.txt, docs/b.txt
            var docFolders = docPrefixes.Select(p => p.Value).ToList();
            Assert.Single(docFolders);
            Assert.Equal("docs/sub/", docFolders[0]);
        }
        finally { Cleanup(b); }
    }

    [SkippableFact]
    public void List_pagination_walks_every_object_via_NextPageToken()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            const int total = 7;
            for (int i = 0; i < total; i++)
                Upload(b, $"page/{i:D2}.txt", Encoding.UTF8.GetBytes(i.ToString()), "text/plain");

            var seen = new HashSet<string>();
            var token = "";
            var pages = 0;
            do
            {
                _sut.Object_List(TestSupport.Auth(), b, "page/", 3, token, "", out var objs, out var next, out _);
                foreach (var o in objs) seen.Add(o.Name);
                token = next;
                pages++;
            }
            while (token != "" && pages <= 10);

            Assert.Equal(total, seen.Count);
            Assert.Equal(3, pages); // ceil(7 / 3)
        }
        finally { Cleanup(b); }
    }

    // ---- copy / move / delete ----------------------------------------------------------

    [SkippableFact]
    public void Copy_keeps_source_move_removes_it_delete_removes_object()
    {
        RequireEmulator();
        var src = NewBucket();
        var dst = NewBucket();
        try
        {
            Upload(src, "docs/a.txt", Encoding.UTF8.GetBytes("content A"), "text/plain");
            Upload(src, "docs/b.txt", Encoding.UTF8.GetBytes("content B"), "text/plain");

            _sut.Object_Copy(TestSupport.Auth(), src, "docs/a.txt", dst, "copied/a.txt");
            _sut.Object_Exists(TestSupport.Auth(), src, "docs/a.txt", out var srcKept);
            _sut.Object_Exists(TestSupport.Auth(), dst, "copied/a.txt", out var copyThere);
            _sut.Object_Download(TestSupport.Auth(), dst, "copied/a.txt", out var copied);
            Assert.True(srcKept);
            Assert.True(copyThere);
            Assert.Equal("content A", Encoding.UTF8.GetString(copied.Content));

            _sut.Object_Move(TestSupport.Auth(), src, "docs/b.txt", dst, "moved/b.txt");
            _sut.Object_Exists(TestSupport.Auth(), src, "docs/b.txt", out var srcGone);
            _sut.Object_Exists(TestSupport.Auth(), dst, "moved/b.txt", out var moveThere);
            Assert.False(srcGone);
            Assert.True(moveThere);

            _sut.Object_Delete(TestSupport.Auth(), dst, "moved/b.txt");
            _sut.Object_Exists(TestSupport.Auth(), dst, "moved/b.txt", out var deleted);
            Assert.False(deleted);
        }
        finally
        {
            Cleanup(src);
            Cleanup(dst);
        }
    }

    // ---- error paths -------------------------------------------------------------------

    [SkippableFact]
    public void Errors_surface_for_missing_object_missing_bucket_and_nonempty_delete()
    {
        RequireEmulator();
        var b = NewBucket();
        try
        {
            Upload(b, "keep.txt", Encoding.UTF8.GetBytes("x"), "text/plain");

            Assert.ThrowsAny<Exception>(() =>
                _sut.Object_Download(TestSupport.Auth(), b, "does-not-exist.txt", out _));

            Assert.ThrowsAny<Exception>(() =>
                _sut.Object_Upload(TestSupport.Auth(), "no-such-bucket-" + Guid.NewGuid().ToString("N")[..8], "f.txt", new GcsFile { Content = [1], ContentType = "text/plain" }));

            Assert.ThrowsAny<Exception>(() =>
                _sut.Bucket_Delete(TestSupport.Auth(), b)); // non-empty
        }
        finally { Cleanup(b); }
    }
}
