using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;
using Loc = Noctis.Localization.Loc;
using Router = Noctis.Tests.LrcRedModelSyncServiceTests.Router;

namespace Noctis.Tests;

/// <summary>
/// Live 10-19: the first Search Lyrics for "Die With A Smile (Acoustic)" said lrc.red
/// "Couldn't connect"; a minute later it worked. lrc.red's nginx answers a second uncached
/// /api/v1 query right behind another with an HTML 503 (curl: 200 in 1.38 s, then 503 in
/// 0.16 s). One song asked about by the lyrics page, Search Lyrics and the Studio at once must
/// cost one lookup, a busy answer gets one retry after a pause, and a still-busy lrc.red says so
/// instead of blaming the connection. The model-sync POST is never retried. No network.
/// </summary>
public class LrcRedBusyLookupTests
{
    private const string Artist = "Lady Gaga & Bruno Mars";
    private const string Title = "Die With A Smile (Acoustic)";
    private const double Duration = 251.7;
    private const string Isrc = LrcRedModelSyncServiceTests.AcousticIsrc;

    // What lrc.red's nginx sent the curl repro.
    private const string NginxBusy =
        "<html>\r\n<head><title>503 Service Temporarily Unavailable</title></head>\r\n<body>\r\n" +
        "<center><h1>503 Service Temporarily Unavailable</h1></center>\r\n<hr><center>nginx</center>\r\n</body>\r\n</html>\r\n";

    private static HttpResponseMessage Busy(HttpStatusCode code = HttpStatusCode.ServiceUnavailable) =>
        new(code) { Content = new StringContent(NginxBusy, System.Text.Encoding.UTF8, "text/html") };

    /// <summary>lrc.red's acoustic cut: line-synced in the lookup and its file.</summary>
    private static HttpResponseMessage Acoustic(HttpRequestMessage req) => req.RequestUri!.AbsolutePath switch
    {
        "/api/v1" => LrcRedModelSyncServiceTests.Json(HttpStatusCode.OK, LrcRedModelSyncServiceTests.AcousticLookup),
        "/s/" + Isrc + ".ttml" => LrcRedModelSyncServiceTests.Json(HttpStatusCode.OK, LrcRedModelSyncServiceTests.AcousticLineTtml),
        _ => LrcRedModelSyncServiceTests.Json(HttpStatusCode.NotFound, "{\"error\":\"Not found\"}"),
    };

    private static bool IsLookup(string url) => new Uri(url).AbsolutePath == "/api/v1";

    private static int Lookups(Router router)
    {
        lock (router.Requests) return router.Requests.Count(r => IsLookup(r.Url));
    }

    /// <summary>A router whose lookups wait for <paramref name="gate"/> (honouring the request's
    /// cancellation, as a real transfer does); everything else answers at once.</summary>
    private static Router Gated(TaskCompletionSource gate, Func<int, HttpResponseMessage?>? lookup = null)
    {
        var n = 0;
        return new Router(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath != "/api/v1") return Acoustic(req);
            var nth = Interlocked.Increment(ref n);
            await gate.Task.WaitAsync(ct);
            return lookup?.Invoke(nth) ?? Acoustic(req);
        });
    }

    private static async Task Until(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done()) await Task.Delay(5);
        Assert.True(done(), "timed out");
    }

    // ── One lookup per song ──

    [Fact]
    public async Task ThePageSearchDialogAndStudio_AskingAtOnce_SendOneLookup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = Gated(gate);
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var page = svc.SearchLyricsAsync(Artist, Title, Duration);
        var studio = svc.FindMatchAsync(Artist, Title, Duration);
        var dialog = svc.SearchLyricsAsync(Artist, Title, Duration);
        await Until(() => Lookups(router) >= 1);
        await Task.Delay(150); // room for a duplicate to go out
        gate.SetResult();

        Assert.True((await page)?.HasLyrics);
        Assert.True((await dialog)?.HasLyrics);
        Assert.Equal(Isrc, (await studio)?.Isrc);
        Assert.Equal(1, Lookups(router));
    }

    [Fact]
    public async Task ALookupItsCallerGaveUpOn_StillAnswersTheNextAsk()
    {
        // The lyrics page stops waiting for lrc.red after its grace when LRCLIB already answered;
        // the request was on the wire, and Search Lyrics opened right after asked again.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = Gated(gate);
        var svc = new LrcRedLyricsService(new HttpClient(router));

        using var cts = new CancellationTokenSource();
        var page = svc.SearchLyricsAsync(Artist, Title, Duration, cts.Token);
        await Until(() => Lookups(router) == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page);
        gate.SetResult();

        var dialog = await svc.SearchLyricsAsync(Artist, Title, Duration);
        Assert.True(dialog?.HasLyrics);
        Assert.Equal(1, Lookups(router));
    }

    [AvaloniaFact]
    public async Task APageOpen_AndASearchLyricsPress_EachSendOneLookup()
    {
        // The lyrics page opening on a song with no lyrics: one lookup, one lyrics file.
        var router = new Router(Acoustic);
        var m = Mount(new LrcRedLyricsService(new HttpClient(router)));
        try
        {
            m.Vm.SearchLyricsForTrack(m.Track);
            await PumpUntilAsync(() => m.Vm.LyricsSourceName == OnlineLyricsSource.LrcRed && !m.Vm.IsSearching);
            Assert.Equal(OnlineLyricsSource.LrcRed, m.Vm.LyricsSourceName);
            Assert.Equal(1, Lookups(router));
            Assert.Equal(2, router.Requests.Count);
        }
        finally { Cleanup(m); }

        // One Search Lyrics press (Auto) on a fresh session: the same.
        router = new Router(Acoustic);
        m = Mount(new LrcRedLyricsService(new HttpClient(router)));
        try
        {
            var search = m.Vm.CreateLyricsSearchViewModel(m.Track);
            await search.SearchAsync();
            Assert.True(search.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed).HasLyrics);
            Assert.Equal(1, Lookups(router));
            Assert.Equal(2, router.Requests.Count);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task SearchLyricsPressed_WhileThePagesSearchIsOut_SharesItsLookup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = Gated(gate);
        var svc = new LrcRedLyricsService(new HttpClient(router));
        var m = Mount(svc);
        try
        {
            // The page opens on the song: its automatic search asks lrc.red.
            m.Vm.SearchLyricsForTrack(m.Track);
            await PumpUntilAsync(() => Lookups(router) == 1);

            // Search Lyrics while that lookup is still out.
            var search = m.Vm.CreateLyricsSearchViewModel(m.Track);
            var pressed = search.SearchAsync();
            await PumpUntilAsync(() => false, 150);
            gate.SetResult();
            await PumpUntilAsync(() => pressed.IsCompleted && !m.Vm.IsSearching);
            await pressed;

            var row = search.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);
            Assert.True(row.HasLyrics, row.FormatLabel);
            Assert.Equal(1, Lookups(router));

            // Pressed again: answered from the first lookup.
            await search.SearchAsync();
            Assert.Equal(1, Lookups(router));
        }
        finally { Cleanup(m); }
    }

    // ── Busy ──

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task BusyLookup_IsRetriedOnce_AfterAPause(HttpStatusCode busy)
    {
        var n = 0;
        var router = new Router(req => IsLookup(req.RequestUri!.AbsoluteUri) && Interlocked.Increment(ref n) == 1 ? Busy(busy) : Acoustic(req));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.FromMilliseconds(250) };

        var sw = Stopwatch.StartNew();
        var result = await svc.SearchLyricsAsync(Artist, Title, Duration);
        Assert.True(result?.HasLyrics);
        Assert.Equal(2, Lookups(router));
        Assert.True(sw.ElapsedMilliseconds >= 200, $"retried after {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void BusyRetryDelay_IsAShortPause()
    {
        var delay = new LrcRedLyricsService(new HttpClient()).BusyRetryDelay;
        Assert.InRange(delay, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StillBusyAfterTheRetry_IsAProviderErrorThatSaysBusy_AndNotRemembered()
    {
        var router = new Router(req => IsLookup(req.RequestUri!.AbsoluteUri) ? Busy() : Acoustic(req));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.FromMilliseconds(10) };

        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync(Artist, Title, Duration));
        Assert.Equal("lrc.red", ex.Provider);
        Assert.True(ex.IsBusy);
        Assert.Equal(2, Lookups(router)); // one retry, no more

        var again = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.FindMatchAsync(Artist, Title, Duration));
        Assert.True(again.IsBusy);
        Assert.Equal(4, Lookups(router)); // a later ask tries again
    }

    [Fact]
    public async Task OtherFailures_AreNotRetried_NorBusy()
    {
        var router = new Router(_ => LrcRedModelSyncServiceTests.Json(HttpStatusCode.BadGateway, "bad gateway"));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.FromMilliseconds(10) };
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync(Artist, Title, Duration));
        Assert.False(ex.IsBusy);
        Assert.Equal(1, Lookups(router));
    }

    [Fact]
    public async Task TheRetryPause_IsCancellable_AndNothingIsSentOnceNobodyWaits()
    {
        var router = new Router(req => IsLookup(req.RequestUri!.AbsoluteUri) ? Busy() : Acoustic(req));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.FromSeconds(30) };

        using var cts = new CancellationTokenSource();
        var search = svc.SearchLyricsAsync(Artist, Title, Duration, cts.Token);
        await Until(() => Lookups(router) == 1);
        await Task.Delay(50); // into the pause
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"cancel took {sw.ElapsedMilliseconds} ms");

        await Task.Delay(200);
        Assert.Equal(1, Lookups(router));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, LrcRedSyncFailure.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, LrcRedSyncFailure.RateLimited)]
    public async Task ModelSync_Busy_IsNeverRetried(HttpStatusCode busy, LrcRedSyncFailure failure)
    {
        var router = new Router(req => req.Method == HttpMethod.Post ? Busy(busy) : Acoustic(req));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.Zero };
        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(Isrc));
        Assert.Equal(failure, ex.Failure);
        await Task.Delay(100);
        Assert.Equal(1, router.Count(HttpMethod.Post));
    }

    [AvaloniaFact]
    public async Task SearchLyrics_LrcRedStillBusy_SaysBusy_NotCouldntConnect()
    {
        var router = new Router(req => IsLookup(req.RequestUri!.AbsoluteUri) ? Busy() : Acoustic(req));
        var svc = new LrcRedLyricsService(new HttpClient(router)) { BusyRetryDelay = TimeSpan.FromMilliseconds(10) };
        var m = Mount(svc);
        try
        {
            // Auto: LRCLIB answered (no match), lrc.red busy.
            var search = m.Vm.CreateLyricsSearchViewModel(m.Track);
            await search.SearchAsync();
            var row = search.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);
            Assert.Equal(Loc.T("LyricsSearch.Busy"), row.FormatLabel);
            Assert.NotEqual(Loc.T("LyricsSearch.CouldNotConnect"), row.FormatLabel);
            Assert.Equal("LRCLIB", search.Results[0].Source);
            Assert.Equal(Loc.T("LyricsSearch.NoMatch"), search.Results[0].FormatLabel);

            // lrc.red alone: the status says it is busy, not "check your internet connection".
            search.SelectedSource = OnlineLyricsSource.LrcRed;
            await PumpUntilAsync(() => !search.IsSearching && search.Results.Count == 1);
            Assert.Equal(Loc.T("LyricsSearch.Busy"), search.Results[0].FormatLabel);
            Assert.Equal(Loc.T("LyricsSearch.BusyStatus", OnlineLyricsSource.LrcRed), search.StatusText);
            Assert.NotEqual(Loc.T("LyricsSearch.Failed"), search.StatusText);
        }
        finally { Cleanup(m); }
    }

    // ── Harness (the lyrics page with lrc.red on, LRCLIB empty, NetEase off) ──

    private sealed class EmptyLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false, LrcRedEnabled = true };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    private sealed record Mounted(LyricsViewModel Vm, Track Track, string Dir);

    private static Mounted Mount(LrcRedLyricsService lrcRed)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new EmptyLrcLib(), new StubNetEase(), new StubMetadata(), new SettingsPersistence(), new FakeLibraryService())
        {
            ExtraLyricsSources = new[] { new OnlineLyricsSource(OnlineLyricsSource.LrcRed, s => s.LrcRedEnabled, lrcRed.SearchLyricsAsync) },
            LrcRed = lrcRed,
            TrashSidecarFile = path => { File.Delete(path); return true; },
        };
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lrcred-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var track = new Track { Title = Title, Artist = Artist, Duration = TimeSpan.FromSeconds(Duration), FilePath = Path.Combine(dir, "acoustic.mp3") };
        player.CurrentTrack = track;
        return new Mounted(vm, track, dir);
    }

    private static void Cleanup(Mounted m)
    {
        foreach (var ext in new[] { ".lrc", ".elrc" })
            AppWrittenSidecarRegistry.Default.Remove(Path.Combine(m.Dir, "acoustic" + ext));
        try { Directory.Delete(m.Dir, recursive: true); } catch { }
        try { File.Delete(Path.Combine(LyricsViewModel.LyricsCacheDir, m.Track.Id + ".lrc")); } catch { }
    }

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
