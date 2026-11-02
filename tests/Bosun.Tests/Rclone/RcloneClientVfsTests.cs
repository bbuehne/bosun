using System.Net;
using System.Text.Json;
using Bosun.Rclone;
using Bosun.Tests.Rclone.Fakes;

namespace Bosun.Tests.Rclone;

/// <summary>
/// bs-obf: <see cref="RcloneClient"/>'s <c>vfs/list</c>, <c>vfs/stats</c> and <c>vfs/queue</c>. The
/// response bodies are the shapes in rclone's documentation (https://rclone.org/rc/#vfs-stats and
/// vfs/rc.go). No real HTTP: <see cref="FakeHttpMessageHandler"/> backs the client.
/// </summary>
public sealed class RcloneClientVfsTests
{
    private const string Fs = "bosun-example-nas:/volume1/share";

    private const string FullStats = """
        {
          "diskCache": {"bytesUsed": 1024, "erroredFiles": 2, "files": 3, "hashType": 1, "outOfSpace": false,
                        "path": "C:\\cache\\vfs", "pathMeta": "C:\\cache\\vfsMeta", "uploadsInProgress": 4, "uploadsQueued": 5},
          "fs": "bosun-example-nas:/volume1/share",
          "inUse": 7,
          "metadataCache": {"dirs": 6, "files": 8},
          "opt": {"CacheMode": 2}
        }
        """;

    [Fact]
    public async Task ListVfsAsync_posts_no_parameters_and_returns_the_vfses_array()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"vfses":["bosun-a:/x","bosun-b:/y[0]"]}""");

        var result = await client.ListVfsAsync(CancellationToken.None);

        Assert.Equal(["bosun-a:/x", "bosun-b:/y[0]"], result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("vfs/list", request.Endpoint);
        Assert.Equal("{}", request.Body);
    }

    [Theory]
    [InlineData("""{"vfses":null}""")]
    [InlineData("""{"vfses":[]}""")]
    [InlineData("{}")]
    public async Task ListVfsAsync_with_no_active_VFS_returns_an_empty_list(string json)
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, json);

        Assert.Empty(await client.ListVfsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetVfsStatsAsync_sends_fs_and_reads_the_documented_counters()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, FullStats);

        var stats = await client.GetVfsStatsAsync(Fs, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("vfs/stats", request.Endpoint);
        Assert.Equal(Fs, JsonDocument.Parse(request.Body).RootElement.GetProperty("fs").GetString());

        var disk = Assert.IsType<RcloneVfsDiskCache>(stats.DiskCache);
        Assert.Equal(1024, disk.BytesUsed);
        Assert.Equal(3, disk.Files);
        Assert.Equal(4, disk.UploadsInProgress);
        Assert.Equal(5, disk.UploadsQueued);
        Assert.Equal(2, disk.ErroredFiles);
        var metadata = Assert.IsType<RcloneVfsMetadataCache>(stats.MetadataCache);
        Assert.Equal(6, metadata.Dirs);
        Assert.Equal(8, metadata.Files);
    }

    [Fact]
    public async Task GetVfsStatsAsync_without_a_diskCache_section_returns_null_not_zeros()
    {
        // Cache mode off: rcd omits diskCache. Reading that as all-zero would read as "idle".
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, """{"fs":"x","inUse":1,"metadataCache":{"dirs":1,"files":2}}""");

        var stats = await client.GetVfsStatsAsync(Fs, CancellationToken.None);

        Assert.Null(stats.DiskCache);
        Assert.NotNull(stats.MetadataCache);
    }

    [Fact]
    public async Task GetVfsStatsAsync_with_a_counter_missing_throws_rather_than_reading_zero()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{"diskCache":{"bytesUsed":1,"files":1,"uploadsInProgress":0,"uploadsQueued":0},"metadataCache":{"dirs":1,"files":1}}""");

        var ex = await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVfsStatsAsync(Fs, CancellationToken.None));

        Assert.Contains("erroredFiles", ex.Message);
    }

    [Fact]
    public async Task GetVfsStatsAsync_does_not_read_inUse()
    {
        // inUse is a refcount on the VFS, not open files. Two responses identical except for inUse
        // must parse identically.
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, FullStats);
        handler.EnqueueJson(HttpStatusCode.OK, FullStats.Replace("\"inUse\": 7", "\"inUse\": 0"));

        var first = await client.GetVfsStatsAsync(Fs, CancellationToken.None);
        var second = await client.GetVfsStatsAsync(Fs, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetVfsStatsAsync_surfaces_an_rc_error_for_an_unknown_VFS()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, """{"error":"no VFS found with name \"x\"","status":500}""");

        var ex = await Assert.ThrowsAsync<RcloneRcException>(() => client.GetVfsStatsAsync("x", CancellationToken.None));

        Assert.Equal(500, ex.HttpStatusCode);
    }

    [Fact]
    public async Task GetVfsQueueAsync_sends_fs_and_returns_the_queue_items()
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{"queue":[{"name":"a.txt","id":1,"size":10,"expiry":-5.0,"tries":1,"delay":1.0,"uploading":true},{"name":"b.txt","id":2,"size":20,"expiry":3.5,"tries":0,"delay":1.0,"uploading":false}]}""");

        var queue = await client.GetVfsQueueAsync(Fs, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("vfs/queue", request.Endpoint);
        Assert.Equal(Fs, JsonDocument.Parse(request.Body).RootElement.GetProperty("fs").GetString());
        Assert.Equal(2, queue.Count);
        Assert.Equal("a.txt", queue[0].Name);
        Assert.True(queue[0].Uploading);
        Assert.False(queue[1].Uploading);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"queue":null}""")]
    [InlineData("""{"queue":[]}""")]
    public async Task GetVfsQueueAsync_with_nothing_queued_returns_an_empty_list(string json)
    {
        var (client, handler) = CreateClient();
        handler.EnqueueJson(HttpStatusCode.OK, json);

        Assert.Empty(await client.GetVfsQueueAsync(Fs, CancellationToken.None));
    }

    [Theory]
    [InlineData("vfs/list")]
    [InlineData("vfs/stats")]
    [InlineData("vfs/queue")]
    public async Task Every_VFS_endpoint_sends_the_Basic_auth_header(string endpoint)
    {
        var credential = new RcloneRcCredential("bosun-test-user", "bosun-test-pass");
        var (client, handler) = CreateClient(credential);
        handler.EnqueueJson(HttpStatusCode.OK, FullStats);

        switch (endpoint)
        {
            case "vfs/list":
                await client.ListVfsAsync(CancellationToken.None);
                break;
            case "vfs/stats":
                await client.GetVfsStatsAsync(Fs, CancellationToken.None);
                break;
            default:
                await client.GetVfsQueueAsync(Fs, CancellationToken.None);
                break;
        }

        Assert.Equal(endpoint, handler.Requests[0].Endpoint);
        Assert.NotNull(handler.Requests[0].Authorization);
        Assert.Equal(credential.ToBasicAuthHeaderValue(), handler.Requests[0].Authorization!.Parameter);
    }

    private static (RcloneClient Client, FakeHttpMessageHandler Handler) CreateClient(RcloneRcCredential? credential = null)
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5572/") };
        return (new RcloneClient(httpClient, credential ?? new RcloneRcCredential("bosun-test-user", "bosun-test-pass")), handler);
    }
}
