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

namespace Noctis.Tests;

/// <summary>
/// lrc.red's translations and romaji, end to end: its TTML (Apple's layers under lrc.red's own
/// &lt;sourceMetadata&gt;) through <see cref="LrcRedLyricsService"/> and <see cref="OnlineTtml"/>
/// to the lyrics page, then back from the .elrc sidecar and the lyrics cache it saved — the
/// files the next play reads with <see cref="LrcParser"/>. No network.
/// </summary>
public class LrcRedLayersEndToEndTests
{
    // lrc.red/s/JPP302200613.ttml (YOASOBI 「祝福」, fetched 2026-10-19), trimmed to three of its
    // 51 lines with their English translation and their word-timed romaji.
    internal const string YoasobiTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:lrc=\"http://lrc.red/lyric-ttml-internal\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" lrc:timing=\"Word\" xml:lang=\"ja\">" +
        "<head><metadata><ttm:agent type=\"person\" xml:id=\"voice1\"><ttm:name type=\"full\">YOASOBI</ttm:name></ttm:agent>" +
        "<sourceMetadata xmlns=\"http://lrc.red/lyric-ttml-internal\" leadingSilence=\"0.040\">" +
        "<translations><translation type=\"subtitle\" xml:lang=\"en-US\">" +
        "<text for=\"L1\">You think of the stars floating</text><text for=\"L2\">Far, far away as you fall asleep</text><text for=\"L3\">The future you choose, the path you desire</text>" +
        "</translation></translations><songwriters><songwriter>Ayase</songwriter></songwriters>" +
        "<transliterations><transliteration xml:lang=\"ja-Latn\">" +
        "<text for=\"L1\"><span begin=\"0.564\" end=\"0.863\" xmlns=\"http://www.w3.org/ns/ttml\">haru</span><span begin=\"0.863\" end=\"1.028\" xmlns=\"http://www.w3.org/ns/ttml\">ka</span> <span begin=\"1.028\" end=\"1.507\" xmlns=\"http://www.w3.org/ns/ttml\">tooku</span> <span begin=\"1.507\" end=\"1.852\" xmlns=\"http://www.w3.org/ns/ttml\">ni</span> <span begin=\"1.936\" end=\"2.124\" xmlns=\"http://www.w3.org/ns/ttml\">u</span><span begin=\"2.124\" end=\"2.514\" xmlns=\"http://www.w3.org/ns/ttml\">kabu</span> <span begin=\"2.612\" end=\"2.904\" xmlns=\"http://www.w3.org/ns/ttml\">hoshi</span> <span begin=\"2.904\" end=\"3.294\" xmlns=\"http://www.w3.org/ns/ttml\">o</span></text>" +
        "<text for=\"L2\"><span begin=\"3.294\" end=\"3.687\" xmlns=\"http://www.w3.org/ns/ttml\">omo</span><span begin=\"3.687\" end=\"4.017\" xmlns=\"http://www.w3.org/ns/ttml\">i</span> <span begin=\"4.080\" end=\"4.374\" xmlns=\"http://www.w3.org/ns/ttml\">nemu</span><span begin=\"4.374\" end=\"4.613\" xmlns=\"http://www.w3.org/ns/ttml\">ri</span> <span begin=\"4.666\" end=\"4.866\" xmlns=\"http://www.w3.org/ns/ttml\">ni</span> <span begin=\"4.866\" end=\"5.341\" xmlns=\"http://www.w3.org/ns/ttml\">tsuku</span> <span begin=\"5.411\" end=\"5.652\" xmlns=\"http://www.w3.org/ns/ttml\">kimi</span> <span begin=\"5.652\" end=\"6.045\" xmlns=\"http://www.w3.org/ns/ttml\">no</span></text>" +
        "<text for=\"L3\"><span begin=\"6.132\" end=\"6.477\" xmlns=\"http://www.w3.org/ns/ttml\">era</span><span begin=\"6.477\" end=\"6.761\" xmlns=\"http://www.w3.org/ns/ttml\">bu</span> <span begin=\"6.761\" end=\"7.021\" xmlns=\"http://www.w3.org/ns/ttml\">mi</span><span begin=\"7.021\" end=\"7.259\" xmlns=\"http://www.w3.org/ns/ttml\">rai</span> <span begin=\"7.259\" end=\"7.469\" xmlns=\"http://www.w3.org/ns/ttml\">ga</span> <span begin=\"7.599\" end=\"7.979\" xmlns=\"http://www.w3.org/ns/ttml\">nozo</span><span begin=\"7.979\" end=\"8.201\" xmlns=\"http://www.w3.org/ns/ttml\">mu</span> <span begin=\"8.257\" end=\"8.545\" xmlns=\"http://www.w3.org/ns/ttml\">michi</span> <span begin=\"8.545\" end=\"8.890\" xmlns=\"http://www.w3.org/ns/ttml\">ga</span></text>" +
        "</transliteration></transliterations></sourceMetadata></metadata></head>" +
        "<body dur=\"3:12.400\"><div begin=\"0.564\" end=\"12.711\" lrc:songPart=\"Intro\">" +
        "<p begin=\"0.564\" end=\"3.294\" lrc:key=\"L1\" ttm:agent=\"voice1\"><span begin=\"0.564\" end=\"0.863\">遥</span><span begin=\"0.863\" end=\"1.028\">か</span><span begin=\"1.028\" end=\"1.507\">遠く</span><span begin=\"1.507\" end=\"1.852\">に</span><span begin=\"1.936\" end=\"2.124\">浮</span><span begin=\"2.124\" end=\"2.514\">かぶ</span><span begin=\"2.612\" end=\"2.904\">星</span><span begin=\"2.904\" end=\"3.294\">を</span></p>" +
        "<p begin=\"3.294\" end=\"6.045\" lrc:key=\"L2\" ttm:agent=\"voice1\"><span begin=\"3.294\" end=\"3.687\">想</span><span begin=\"3.687\" end=\"4.017\">い</span><span begin=\"4.080\" end=\"4.374\">眠</span><span begin=\"4.374\" end=\"4.613\">り</span><span begin=\"4.666\" end=\"4.866\">に</span><span begin=\"4.866\" end=\"5.341\">つく</span><span begin=\"5.411\" end=\"5.652\">君</span><span begin=\"5.652\" end=\"6.045\">の</span></p>" +
        "<p begin=\"6.132\" end=\"8.890\" lrc:key=\"L3\" ttm:agent=\"voice1\"><span begin=\"6.132\" end=\"6.477\">選</span><span begin=\"6.477\" end=\"6.761\">ぶ</span><span begin=\"6.761\" end=\"7.021\">未</span><span begin=\"7.021\" end=\"7.259\">来</span><span begin=\"7.259\" end=\"7.469\">が</span><span begin=\"7.599\" end=\"7.979\">望</span><span begin=\"7.979\" end=\"8.201\">む</span><span begin=\"8.257\" end=\"8.545\">道</span><span begin=\"8.545\" end=\"8.890\">が</span></p>" +
        "</div></body></tt>";

    internal const string YoasobiLookup =
        "{\"results\":[{\"album_name\":\"祝福 - Single\",\"artist_name\":\"YOASOBI\",\"duration\":192,\"id\":\"JPP302200613\",\"isrc\":\"JPP302200613\",\"timing_type\":\"word\",\"track_name\":\"祝福\"}],\"source\":\"HIT-LRC-RED\",\"total\":1}";

    private sealed class Router(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private static LrcRedLyricsService YoasobiService() => new(new HttpClient(new Router(req =>
        req.RequestUri!.AbsolutePath switch
        {
            "/api/v1" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(YoasobiLookup) },
            "/s/JPP302200613.ttml" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(YoasobiTtml) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
        })));

    private static void AssertLayers(IReadOnlyList<LyricLine> lines)
    {
        var sung = lines.Where(l => l.IsSynced && !l.IsIntroPlaceholder).ToList();
        Assert.Equal(3, sung.Count);
        Assert.Equal("遥か遠くに浮かぶ星を", sung[0].Text);
        Assert.Equal("You think of the stars floating", sung[0].Translation);
        Assert.Equal("haruka tooku ni ukabu hoshi o", sung[0].Transliteration);
        Assert.Equal("omoi nemuri ni tsuku kimi no", sung[1].Transliteration);
        Assert.Equal("Far, far away as you fall asleep", sung[1].Translation);
        Assert.Equal("The future you choose, the path you desire", sung[2].Translation);
        Assert.Equal(8, sung[0].Words!.Count); // the sung line keeps its word timing
    }

    [Fact]
    public async Task Service_KeepsTheTranslationAndRomaji_InItsElrc()
    {
        var result = await YoasobiService().SearchLyricsAsync("YOASOBI", "祝福", 192.4);

        Assert.NotNull(result);
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(result));
        AssertLayers(LrcParser.Parse(result!.SyncedLyrics!));
    }

    private sealed class StubLrcLib : ILrcLibService
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

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false, LrcRedEnabled = true };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
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

    [AvaloniaFact]
    public async Task LyricsPage_ShowsThem_AndTheSavedSidecarAndCacheReadThemBack()
    {
        var svc = YoasobiService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new LyricsSearchMetadataStub(), new SettingsPersistence(), new FakeLibraryService())
        {
            ExtraLyricsSources = new[] { new OnlineLyricsSource(OnlineLyricsSource.LrcRed, s => s.LrcRedEnabled, svc.SearchLyricsAsync) },
            TrashSidecarFile = path => { File.Delete(path); return true; },
        };
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lrcred-layers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var track = new Track { Title = "祝福", Artist = "YOASOBI", Duration = TimeSpan.FromSeconds(192.4), FilePath = Path.Combine(dir, "song.mp3") };
        player.CurrentTrack = track;
        var elrc = Path.ChangeExtension(track.FilePath, ".elrc");
        var cache = Path.Combine(LyricsViewModel.LyricsCacheDir, track.Id + ".lrc");

        try
        {
            // First play: nothing on disk, so the page asks lrc.red.
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == OnlineLyricsSource.LrcRed);
            Assert.Equal(OnlineLyricsSource.LrcRed, vm.LyricsSourceName);
            AssertLayers(vm.LyricLines);

            await PumpUntilAsync(() => File.Exists(elrc) && File.Exists(cache));
            await LyricsViewModel.EnqueueLyricsFileWork(() => { });
            AssertLayers(LrcParser.Parse(File.ReadAllText(cache)));
            AssertLayers(LrcParser.Parse(File.ReadAllText(elrc)));

            // Next play: the page reads the .elrc it saved, with no online search.
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == string.Empty && vm.LyricLines.Count(l => l.IsSynced) >= 3);
            AssertLayers(vm.LyricLines);
        }
        finally
        {
            foreach (var path in new[] { elrc, Path.ChangeExtension(track.FilePath, ".lrc") })
                AppWrittenSidecarRegistry.Default.Remove(path);
            try { File.Delete(cache); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class LyricsSearchMetadataStub : IMetadataService
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
}
