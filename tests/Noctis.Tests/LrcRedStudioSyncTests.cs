using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;
using Status = Noctis.ViewModels.LyricsStudioViewModel.StudioStatus;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio's "Sync with lrc.red's model" beside Lullaby: offered for a song lrc.red has only
/// line-synced or plain while lrc.red is on; its word-synced lyrics open for review as a run's
/// result does (romaji and translation riding along), a review on screen is replaced only after
/// asking, and nothing runs Lullaby or touches disk.
/// </summary>
public class LrcRedStudioSyncTests : IDisposable
{
    private const string Isrc = "JPP302200613";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "NoctisTests", "lrcred-studio-" + Guid.NewGuid().ToString("N"));

    public LrcRedStudioSyncTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private sealed class FakeLrcRed : ILrcRedLyricsService
    {
        public LrcRedMatch? Match = new(Isrc, "祝福", "YOASOBI", null, 192, "line");
        public int Lookups;
        public Func<CancellationToken, Task<LrcLibResult>> Sync = _ => Task.FromResult(YoasobiWordSynced());
        public readonly List<CancellationToken> SyncCalls = new();

        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => throw new InvalidOperationException("the Studio only looks up");

        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult(Match);
        }

        public Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default)
        {
            Assert.Equal(Isrc, isrc);
            lock (SyncCalls) SyncCalls.Add(ct);
            return Sync(ct);
        }
    }

    /// <summary>lrc.red's YOASOBI 「祝福」 as its model's answer would come: word timing, romaji and translation.</summary>
    private static LrcLibResult YoasobiWordSynced()
    {
        var lyrics = OnlineTtml.Convert(LrcRedLayersEndToEndTests.YoasobiTtml)!.Value;
        return new LrcLibResult { TrackName = "祝福", ArtistName = "YOASOBI", Duration = 192.4, SyncedLyrics = lyrics.Synced, PlainLyrics = lyrics.Plain, Isrc = Isrc };
    }

    private sealed class NoEngine : ILyricsStudioEngine
    {
        public int Runs;
        public NoEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Runs++;
            throw new InvalidOperationException("Lullaby must not run for lrc.red's model");
        }
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    private Track Song(string name, string? lrc = null)
    {
        var track = new Track { Title = "祝福", Artist = "YOASOBI", Duration = TimeSpan.FromSeconds(192.4), FilePath = Path.Combine(_tmp, name + ".mp3") };
        if (lrc != null) File.WriteAllText(Path.ChangeExtension(track.FilePath, ".lrc"), lrc);
        return track;
    }

    private (LyricsStudioViewModel Vm, FakeLrcRed LrcRed, NoEngine Engine, AppSettings Settings, List<string> Asked) Studio(params Track[] tracks)
    {
        var settings = new AppSettings { LrcRedEnabled = true, LyricsStudioWordTimings = false };
        var engine = new NoEngine(_tmp);
        var lrcRed = new FakeLrcRed();
        var asked = new List<string>();
        var vm = new LyricsStudioViewModel(tracks, engine, new LyricsWriter(null!, null), new FakeLibraryService(), null, () => settings, _ => { })
        {
            AutoTick = false,
            Confirm = message => { asked.Add(message); return Task.FromResult(true); },
            LrcRed = lrcRed,
        };
        return (vm, lrcRed, engine, settings, asked);
    }

    private static async Task SelectAsync(LyricsStudioViewModel vm, int index)
    {
        vm.Selected = vm.Queue[index];
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SongWithoutLyrics_LrcRedsModelBringsWordTimedLyrics_ForReview_WithRomajiAndTranslation()
    {
        var (vm, lrcRed, engine, _, asked) = Studio(Song("blessing"));
        await SelectAsync(vm, 0);
        var item = vm.Queue[0];
        Assert.True(vm.IsComposing);
        Assert.True(vm.CanLrcRedSync);
        Assert.True(vm.ShowLrcRedSync);
        Assert.Equal("Sync with lrc.red's model", vm.LrcRedSyncLabel);
        Assert.False(vm.WordTimings);

        await vm.SyncWithLrcRedCommand.ExecuteAsync(null);

        Assert.Single(lrcRed.SyncCalls);
        Assert.Equal(0, engine.Runs);
        Assert.Empty(asked); // nothing on screen to replace
        Assert.Equal(Status.Ready, item.Status);
        Assert.Equal("Word-synced by lrc.red's model · review", item.StatusText);
        Assert.True(vm.HasReview);
        Assert.True(vm.WordTimings); // Save writes the word timings (ELRC)
        Assert.Equal("From lrc.red · word-synced by its model", vm.ReviewSourceText);
        Assert.DoesNotContain("heard", vm.ReviewConfidenceText);
        Assert.Equal(3, vm.ReviewLines.Count);
        Assert.All(vm.ReviewLines, l => Assert.NotEmpty(l.ToAlignedLine().Words));
        Assert.False(vm.CanLrcRedSync); // word-timed now

        // Romaji and translation ride along to the save, as companion lines at each line's start.
        var saved = LrcParser.Parse(vm.BuildReviewText()).Where(l => l.IsSynced && !l.IsIntroPlaceholder).ToList();
        Assert.Equal("遥か遠くに浮かぶ星を", saved[0].Text); // CJK timed a piece at a time: no spaces added
        Assert.Equal("haruka tooku ni ukabu hoshi o", saved[0].Transliteration);
        Assert.Equal("You think of the stars floating", saved[0].Translation);
        Assert.Equal(8, saved[0].Words!.Count);
        Assert.False(File.Exists(Path.Combine(_tmp, "blessing.elrc"))); // nothing written before Save
    }

    [AvaloniaFact]
    public async Task LoadedLrcReview_IsReplacedOnlyAfterAsking()
    {
        var (vm, lrcRed, _, _, asked) = Studio(Song("blessing", "[00:00.56]遥か遠くに浮かぶ星を\n[00:03.29]想い眠りにつく君の"));
        var answers = new Queue<bool>(new[] { false, true });
        vm.Confirm = message => { asked.Add(message); return Task.FromResult(answers.Dequeue()); };
        await SelectAsync(vm, 0);
        var item = vm.Queue[0];
        Assert.Equal(Status.Loaded, item.Status);
        Assert.True(vm.CanLrcRedSync);

        await vm.SyncWithLrcRedCommand.ExecuteAsync(null); // "No"
        Assert.Single(asked);
        Assert.Contains("“祝福”", asked[0]);
        Assert.Empty(lrcRed.SyncCalls);
        Assert.Equal(Status.Loaded, item.Status);
        Assert.Equal(2, vm.ReviewLines.Count);

        await vm.SyncWithLrcRedCommand.ExecuteAsync(null); // "Yes"
        Assert.Equal(2, asked.Count);
        Assert.Single(lrcRed.SyncCalls);
        Assert.Equal(Status.Ready, item.Status);
        Assert.Equal(3, vm.ReviewLines.Count);
    }

    [AvaloniaFact]
    public async Task NotOffered_WhenLrcRedIsOff_HasNoMatch_IsWordSynced_OrTheSongHasElrc()
    {
        var (vm, lrcRed, _, settings, _) = Studio(Song("a"), Song("b"), Song("c"), Song("d", "[00:01.00]<00:01.00>word <00:01.50>timed<00:02.00>"));
        File.Move(Path.Combine(_tmp, "d.lrc"), Path.Combine(_tmp, "d.elrc"));
        vm.Queue[3].RefreshExistingFormat();

        settings.LrcRedEnabled = false;
        await SelectAsync(vm, 0);
        Assert.False(vm.ShowLrcRedSync);
        Assert.Equal(0, lrcRed.Lookups); // off: not even looked up

        settings.LrcRedEnabled = true;
        lrcRed.Match = null;
        await SelectAsync(vm, 1);
        Assert.Equal(1, lrcRed.Lookups);
        Assert.False(vm.ShowLrcRedSync);

        lrcRed.Match = new LrcRedMatch(Isrc, "祝福", "YOASOBI", null, 192, "word");
        await SelectAsync(vm, 2);
        Assert.False(vm.ShowLrcRedSync);

        lrcRed.Match = new LrcRedMatch(Isrc, "祝福", "YOASOBI", null, 192, "line");
        await SelectAsync(vm, 3);
        Assert.Equal(LyricsFormat.Elrc, vm.Queue[3].ExistingFormat);
        Assert.False(vm.ShowLrcRedSync);

        // A song is looked up once: selecting it again asks lrc.red nothing.
        var lookups = lrcRed.Lookups;
        await SelectAsync(vm, 1);
        await SelectAsync(vm, 2);
        Assert.Equal(lookups, lrcRed.Lookups);
    }

    [AvaloniaFact]
    public async Task Failure_SaysWhy_AndTheSongKeepsWhatItHad()
    {
        var (vm, lrcRed, _, _, _) = Studio(Song("blessing", "[00:00.56]遥か遠くに浮かぶ星を"));
        lrcRed.Sync = _ => Task.FromException<LrcLibResult>(new LrcRedSyncException(LrcRedSyncFailure.Refused, "No lyrics with that ISRC.", 404));
        await SelectAsync(vm, 0);
        var item = vm.Queue[0];

        await vm.SyncWithLrcRedCommand.ExecuteAsync(null);

        Assert.Equal("lrc.red: No lyrics with that ISRC.", item.StatusText);
        Assert.Equal("lrc.red: No lyrics with that ISRC.", vm.RunStatusText);
        Assert.Equal(Status.Loaded, item.Status);
        Assert.Single(vm.ReviewLines);
        Assert.False(vm.IsLrcRedSyncing);
        Assert.True(vm.CanLrcRedSync); // another press may try again
        Assert.Single(lrcRed.SyncCalls);
    }

    [AvaloniaFact]
    public async Task Panel_ShowsTheButtonBesideTimeEveryWord_AndInTheLyricsBox()
    {
        var app = Avalonia.Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
            });
        }
        var (vm, _, _, _, _) = Studio(Song("loaded", "[00:00.56]遥か遠くに浮かぶ星を"), Song("empty"));
        var panel = new Noctis.Views.LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Avalonia.Controls.Window { Width = 1200, Height = 800, Content = panel };
        win.Show();
        try
        {
            static List<Avalonia.Controls.Button> Shown(Avalonia.Controls.Control root) =>
                Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.Button>()
                    .Where(b => b.Classes.Contains("studio-lrcred") && b.IsEffectivelyVisible).ToList();

            await SelectAsync(vm, 0); // .lrc loaded for review: beside Time every word
            win.UpdateLayout();
            var inReview = Assert.Single(Shown(panel));
            Assert.Equal("Sync with lrc.red's model", Avalonia.Automation.AutomationProperties.GetName(inReview));
            Assert.Same(vm.SyncWithLrcRedCommand, inReview.Command);

            await SelectAsync(vm, 1); // no lyrics: in the lyrics box's row
            win.UpdateLayout();
            Assert.True(vm.IsComposing);
            Assert.Same(vm.SyncWithLrcRedCommand, Assert.Single(Shown(panel)).Command);
        }
        finally { win.Close(); }
    }

    /// <summary>Lullaby that times the lines it is given, a second apart, and records what it was given.</summary>
    private sealed class EchoEngine : ILyricsStudioEngine
    {
        public readonly List<LyricsStudioOptions> Options = new();
        public EchoEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
        {
            Options.Add(options);
            var lines = (options.SourceLines ?? Array.Empty<string>()).Select((text, i) =>
                new AlignedLine(text, TimeSpan.FromSeconds(i + 1), TimeSpan.FromSeconds(i + 1.9),
                    new[] { new AlignedWord(text, TimeSpan.FromSeconds(i + 1), TimeSpan.FromSeconds(i + 1.9)) }, 0.9, false)).ToList();
            return Task.FromResult(new LyricsStudioResult(track, lines, LyricsStudioSource.ExistingLyrics, 0.9, "ja", lines.Count));
        }
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    [AvaloniaFact]
    public async Task ResyncAfterLrcRedsModel_RetimesTheLyricsOnScreen_NotTheSongsOldLrc()
    {
        var engine = new EchoEngine(_tmp);
        var settings = new AppSettings { LrcRedEnabled = true };
        var lrcRed = new FakeLrcRed();
        var vm = new LyricsStudioViewModel(new[] { Song("blessing", "[00:00.56]an old line of my own\n[00:03.29]another old line") }, engine,
            new LyricsWriter(null!, null), new FakeLibraryService(), null, () => settings, _ => { })
        {
            AutoTick = false,
            Confirm = _ => Task.FromResult(true),
            LrcRed = lrcRed,
        };
        await SelectAsync(vm, 0);
        await vm.SyncWithLrcRedCommand.ExecuteAsync(null);
        Assert.Equal("遥か遠くに浮かぶ星を", vm.ReviewLines[0].ToAlignedLine().Text);
        Assert.Equal("Re-sync", vm.StartLabel);

        await vm.StartCommand.ExecuteAsync(null); // Re-sync: "a fresh run of Lullaby" on what is shown

        var given = Assert.Single(engine.Options).SourceLines!;
        Assert.Equal(new[] { "遥か遠くに浮かぶ星を", "想い眠りにつく君の", "選ぶ未来が望む道が" }, given);
        Assert.Equal(Status.Ready, vm.Queue[0].Status);
        Assert.Equal(3, vm.ReviewLines.Count);
        // lrc.red's romaji and translation stay with their lines.
        var saved = LrcParser.Parse(vm.BuildReviewText()).Where(l => l.IsSynced && !l.IsIntroPlaceholder).ToList();
        Assert.Equal("You think of the stars floating", saved[0].Translation);
        Assert.Equal("haruka tooku ni ukabu hoshi o", saved[0].Transliteration);
    }

    [AvaloniaFact]
    public async Task WhileItRuns_LullabyWaits_AndClosingTheStudioCancelsIt()
    {
        var (vm, lrcRed, engine, _, _) = Studio(Song("blessing"));
        lrcRed.Sync = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return YoasobiWordSynced();
        };
        await SelectAsync(vm, 0);

        var running = vm.SyncWithLrcRedCommand.ExecuteAsync(null);
        Assert.True(vm.IsLrcRedSyncing);
        Assert.True(vm.ShowLrcRedSync);
        Assert.Equal("lrc.red's model is timing every word…", vm.LrcRedSyncLabel);
        Assert.False(vm.CanStart);

        await vm.CloseCommand.ExecuteAsync(null);
        await running;

        Assert.True(lrcRed.SyncCalls[0].IsCancellationRequested);
        Assert.False(vm.IsLrcRedSyncing);
        Assert.Equal(0, engine.Runs);
        Assert.NotEqual(Status.Ready, vm.Queue[0].Status);
    }
}
