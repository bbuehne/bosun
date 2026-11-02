using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// The supervisor driven through the REAL <see cref="RcloneClient"/>, over an in-memory
/// <see cref="HttpMessageHandler"/> that answers rc endpoints with rclone's documented JSON shapes
/// (https://rclone.org/rc/#vfs-stats). This is the only level at which "<c>inUse</c> is never read"
/// can be tested: the typed <see cref="RcloneVfsStats"/> has no such field, so a regression that
/// started reading it would have to add it, and only a JSON body that carries it can catch that.
/// </summary>
/// <remarks>
/// No socket is opened: <see cref="HttpClient"/> is constructed over the handler. No real
/// <c>rclone rcd</c>, WinFsp or drive letter is reachable.
/// </remarks>
public sealed class IdleOverRealRcClientTests
{
    private const int Idle = 290;

    /// <summary>
    /// Every field rule 7 does NOT name changes on every call -- <c>inUse</c> (a refcount on the VFS
    /// object), <c>diskCache.outOfSpace</c>, <c>diskCache.hashType</c>, <c>opt</c> -- and the six it
    /// does name stay constant. The drive is idle by rule 7's definition and must unmount on time.
    /// Bug: <c>inUse</c> (or any non-listed field) treated as activity, so a drive whose VFS is
    /// merely referenced never unmounts.
    /// </summary>
    [Fact]
    public async Task Changes_in_inUse_and_other_unlisted_fields_are_not_activity()
    {
        var rc = new RcHttpDouble();
        rc.StatsBody = n => new JsonObject
        {
            ["diskCache"] = new JsonObject
            {
                ["bytesUsed"] = 2048, ["erroredFiles"] = 0, ["files"] = 2, ["hashType"] = n % 3,
                ["outOfSpace"] = n % 2 == 0, ["path"] = $"C:\\cache\\vfs\\{n}", ["pathMeta"] = "C:\\cache\\vfsMeta",
                ["uploadsInProgress"] = 0, ["uploadsQueued"] = 0,
            },
            ["fs"] = "ignored",
            ["inUse"] = n,
            ["metadataCache"] = new JsonObject { ["dirs"] = 4, ["files"] = 17 },
            ["opt"] = new JsonObject { ["CacheMode"] = 2, ["Counter"] = n },
        };
        var (supervisor, time) = Build(rc);

        await RunAsync(supervisor, () => supervisor.StartAsync());
        await RunAsync(supervisor, () => supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, State(supervisor));

        await StepAsync(supervisor, time, Idle + 5);

        Assert.True(rc.StatsCalls >= 3, $"vfs/stats was called {rc.StatsCalls} time(s); the scenario did not exercise sampling");
        var drain = supervisor.GetTransitionHistory().Reverse()
            .FirstOrDefault(t => t.HostKey == "archive" && t.To == MountState.Draining);
        Assert.True(
            drain is { DrainCause: DrainCause.IdleTimeout },
            $"expected the idle drain by +{Idle + 5}s; inUse or another unlisted field was treated as activity. " +
            $"State {State(supervisor)}, {rc.StatsCalls} vfs/stats calls.");
        Assert.InRange((drain!.TimestampUtc - DateTimeOffset.UnixEpoch).TotalSeconds, Idle, Idle + 1);
    }

    /// <summary>
    /// A <c>vfs/stats</c> body without <c>diskCache</c> -- what rcd returns when the cache mode is
    /// off. Rule 7: that counts as "cannot read the VFS" and the drive stays up. Bug: the absent
    /// section parsed (or defaulted) as all zeros, which reads as "nothing pending" and unmounts.
    /// </summary>
    [Fact]
    public async Task A_stats_body_with_no_diskCache_section_keeps_the_drive_up()
    {
        var rc = new RcHttpDouble();
        rc.StatsBody = n => new JsonObject
        {
            ["fs"] = "ignored",
            ["inUse"] = 1,
            ["metadataCache"] = new JsonObject { ["dirs"] = 4, ["files"] = 17 },
            ["opt"] = new JsonObject { ["CacheMode"] = 0 },
        };
        var (supervisor, time) = Build(rc);

        await RunAsync(supervisor, () => supervisor.StartAsync());
        await RunAsync(supervisor, () => supervisor.RequestMountAsync("archive"));

        await StepAsync(supervisor, time, 4 * Idle);

        Assert.Equal(MountState.Mounted, State(supervisor));
        Assert.Equal(0, rc.UnmountCalls);
    }

    private static (MountSupervisor Supervisor, FakeTimeProvider Time) Build(RcHttpDouble rc)
    {
        var time = new FakeTimeProvider();
        var http = new HttpClient(rc) { BaseAddress = new Uri("http://127.0.0.1:5572/") };
        var client = new RcloneClient(http, new RcloneRcCredential("bosun-test-user", "bosun-test-pass"), time);
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, idleUnmountSeconds: Idle, drive: "Q:");
        var config = HostFixtures.Build(HostFixtures.Global(mountedProbeIntervalSeconds: 60), host);
        var supervisor = new MountSupervisor(
            new ReloadableConfigStoreDouble(config), client, new ProbeDouble(new SupervisorCallLog()),
            new Bosun.Tests.Supervisor.Fakes.FakeDriveTypeReader(), time, NullLogger<MountSupervisor>.Instance);
        return (supervisor, time);
    }

    private static MountState State(MountSupervisor supervisor) =>
        supervisor.GetSnapshot().Single(h => h.HostKey == "archive").State;

    private static async Task RunAsync(MountSupervisor supervisor, Func<Task> operation)
    {
        var task = operation();
        await supervisor.DrainAsync();
        await task;
    }

    private static async Task StepAsync(MountSupervisor supervisor, FakeTimeProvider time, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await supervisor.DrainAsync();
        }
    }

    /// <summary>Answers the rc endpoints the supervisor uses, with rclone's JSON shapes, from an
    /// in-memory mount table. Responds synchronously, so the client's own timeouts never fire.</summary>
    private sealed class RcHttpDouble : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<(string Fs, string MountPoint)> mounts = [];
        private int statsCalls;
        private int unmountCalls;

        public Func<int, JsonObject> StatsBody { get; set; } = _ => new JsonObject();

        public int StatsCalls
        {
            get
            {
                lock (gate)
                {
                    return statsCalls;
                }
            }
        }

        public int UnmountCalls
        {
            get
            {
                lock (gate)
                {
                    return unmountCalls;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath.TrimStart('/');
            var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? new JsonObject();

            JsonObject reply;
            lock (gate)
            {
                reply = endpoint switch
                {
                    "core/version" => new JsonObject { ["version"] = "v1.75.0" },
                    "mount/mount" => Mount(body),
                    "mount/unmount" => Unmount(body),
                    "mount/listmounts" => new JsonObject
                    {
                        ["mountPoints"] = new JsonArray(mounts.Select(m => (JsonNode)new JsonObject
                        {
                            ["Fs"] = m.Fs, ["MountPoint"] = m.MountPoint, ["MountedOn"] = "2026-11-02T00:00:00Z",
                        }).ToArray()),
                    },
                    "vfs/list" => new JsonObject { ["vfses"] = new JsonArray(mounts.Select(m => (JsonNode)JsonValue.Create(m.Fs)!).ToArray()) },
                    "vfs/stats" => StatsBody(++statsCalls),
                    "vfs/queue" => new JsonObject { ["queue"] = new JsonArray() },
                    "operations/list" => new JsonObject { ["list"] = new JsonArray() },
                    _ => new JsonObject(),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }

        private JsonObject Mount(JsonObject body)
        {
            var fs = body["fs"]!.GetValue<string>();
            var mountPoint = body["mountPoint"]!.GetValue<string>();
            mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
            mounts.Add((fs, mountPoint));
            return new JsonObject { ["mountPoint"] = mountPoint };
        }

        private JsonObject Unmount(JsonObject body)
        {
            unmountCalls++;
            var mountPoint = body["mountPoint"]!.GetValue<string>();
            mounts.RemoveAll(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
            return new JsonObject();
        }
    }
}
