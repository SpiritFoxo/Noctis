using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Word-sync with lrc.red's model" on the lyrics page and in Search Lyrics: offered only for
/// lrc.red's line-synced or plain answer while lrc.red is on, one model request per press, the
/// word-synced answer shown and kept like any picked online lyrics, failures said and retried
/// only by another press, and a song change cancels it.
/// </summary>
public class LrcRedModelSyncPageTests
{
    private const string Isrc = "USUM72413062";
    private const string LineLrc = "[00:09.55]I, I just woke up from a dream\n[00:15.00]Where you and I had to say goodbye";
    private const string WordElrc = "[00:09.55]<00:09.55>I, <00:11.85>I <00:12.26>just <00:12.63>woke <00:13.01>up <00:13.35>from <00:13.90>a <00:14.14>dream<00:14.19>\n" +
                                    "[00:15.00]<00:15.00>Where <00:15.50>you <00:16.00>and <00:16.40>I<00:17.00>";

    private static LrcLibResult Answer(string? synced, string? isrc = Isrc, string? plain = null) => new()
    {
        TrackName = "Die With A Smile (Acoustic)",
        ArtistName = "Lady Gaga, Bruno Mars",
        Duration = 252,
        SyncedLyrics = synced,
        PlainLyrics = plain ?? (synced == null ? null : LyricsTextHelper.StripTimestamps(synced)),
        Isrc = isrc,
    };

    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public LrcLibResult? Answer;
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult(Answer);
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

    /// <summary>lrc.red whose search and model answers the test controls; counts every model call.</summary>
    internal sealed class FakeLrcRed : ILrcRedLyricsService
    {
        public LrcLibResult? SearchAnswer;
        public int Searches;
        public Func<string, CancellationToken, Task<LrcLibResult>> Sync = (_, _) => throw new InvalidOperationException("no model answer set");
        public readonly List<(string Isrc, CancellationToken Token)> SyncCalls = new();

        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Searches);
            return Task.FromResult(SearchAnswer);
        }

        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcRedMatch?>(SearchAnswer?.Isrc is { } isrc ? new LrcRedMatch(isrc, trackName, artist, null, durationSeconds, "line") : null);

        public Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default)
        {
            lock (SyncCalls) SyncCalls.Add((isrc, ct));
            return Sync(isrc, ct);
        }
    }

    private sealed record Mounted(LyricsViewModel Vm, PlayerViewModel Player, Track Track, StubLrcLib LrcLib, FakeLrcRed LrcRed, SettingsPersistence Persistence, string Dir);

    private static Mounted Mount(Action<Mounted>? before = null)
    {
        var persistence = new SettingsPersistence();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrcLib = new StubLrcLib();
        var lrcRed = new FakeLrcRed();
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new StubMetadata(), persistence, new FakeLibraryService())
        {
            ExtraLyricsSources = new[] { new OnlineLyricsSource(OnlineLyricsSource.LrcRed, s => s.LrcRedEnabled, lrcRed.SearchLyricsAsync) },
            LrcRed = lrcRed,
            TrashSidecarFile = path => { File.Delete(path); return true; },
        };
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lrcred-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var track = new Track
        {
            Title = "Die With A Smile (Acoustic)",
            Artist = "Lady Gaga",
            Duration = TimeSpan.FromSeconds(251.7),
            FilePath = Path.Combine(dir, "acoustic.mp3"),
        };
        player.CurrentTrack = track;
        var m = new Mounted(vm, player, track, lrcLib, lrcRed, persistence, dir);
        before?.Invoke(m);
        return m;
    }

    private static void Cleanup(Mounted m)
    {
        foreach (var dir in new[] { m.Dir })
        {
            foreach (var ext in new[] { ".lrc", ".elrc" })
                AppWrittenSidecarRegistry.Default.Remove(Path.Combine(dir, "acoustic" + ext));
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
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

    private static async Task SearchAsync(Mounted m, string expectedSource)
    {
        m.Vm.SearchLyricsForTrack(m.Track);
        await PumpUntilAsync(() => m.Vm.LyricsSourceName == expectedSource && !m.Vm.IsSearching);
        Assert.Equal(expectedSource, m.Vm.LyricsSourceName);
    }

    private static bool ShowsWordTiming(LyricsViewModel vm) => vm.LyricLines.Any(l => l.Words is { Count: > 0 });

    // ── Lyrics page ──

    [AvaloniaFact]
    public async Task LineSyncedLrcRedAnswer_OffersTheModel_AndItsAnswerIsShownCreditedAndSaved()
    {
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        var reply = new TaskCompletionSource<LrcLibResult>();
        m.LrcRed.Sync = (_, _) => reply.Task;
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            Assert.True(m.Vm.ShowLrcRedSync);
            Assert.Equal("Word-sync with lrc.red's model", m.Vm.LrcRedSyncLabel);
            Assert.Equal("Lyrics from lrc.red", m.Vm.LyricsCreditText);
            Assert.False(ShowsWordTiming(m.Vm));

            var press = m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => m.Vm.IsLrcRedSyncing);
            Assert.Equal("Timing every word… a few seconds", m.Vm.LrcRedSyncLabel);
            Assert.True(m.Vm.ShowLrcRedSync);
            Assert.False(m.Vm.WordSyncWithLrcRedCommand.CanExecute(null)); // one request per press

            reply.SetResult(Answer(WordElrc));
            await press;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Isrc, Assert.Single(m.LrcRed.SyncCalls).Isrc);
            Assert.True(ShowsWordTiming(m.Vm));
            Assert.Equal(OnlineLyricsSource.LrcRed, m.Vm.LyricsSourceName);
            Assert.Equal("Word-synced by lrc.red's model", m.Vm.LyricsCreditText);
            Assert.False(m.Vm.ShowLrcRedSync); // word-synced now: nothing left to offer
            Assert.False(m.Vm.IsLrcRedSyncing);

            // Kept as picked online lyrics are: the cache and an .elrc beside the song.
            var elrc = Path.ChangeExtension(m.Track.FilePath, ".elrc");
            var cache = Path.Combine(LyricsViewModel.LyricsCacheDir, m.Track.Id + ".lrc");
            await PumpUntilAsync(() => File.Exists(elrc) && File.Exists(cache) && File.ReadAllText(cache).Contains("<00:12.26>"));
            await LyricsViewModel.EnqueueLyricsFileWork(() => { });
            Assert.Contains("<00:12.26>just", File.ReadAllText(elrc));
            Assert.Contains("<00:12.26>just", File.ReadAllText(cache));
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task LrclibLineSyncedShown_WithALrcRedMatch_IsOffered_AndLrclibStaysTheAlternate()
    {
        var m = Mount(x =>
        {
            x.LrcLib.Answer = Answer("[00:09.55]I, I just woke up from a dream (LRCLIB)", isrc: null);
            x.LrcRed.SearchAnswer = Answer(LineLrc);
        });
        m.LrcRed.Sync = (_, _) => Task.FromResult(Answer(WordElrc));
        try
        {
            await SearchAsync(m, "LRCLIB");
            Assert.Equal("Try lrc.red", m.Vm.AlternateLyricsLabel);
            Assert.True(m.Vm.ShowLrcRedSync);
            Assert.Equal(string.Empty, m.Vm.LyricsCreditText);

            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);

            Assert.Equal(OnlineLyricsSource.LrcRed, m.Vm.LyricsSourceName);
            Assert.True(ShowsWordTiming(m.Vm));
            Assert.True(m.Vm.HasAlternateLyrics);
            Assert.Equal("Try LRCLIB", m.Vm.AlternateLyricsLabel);
            m.Vm.SwitchToAlternateLyricsCommand.Execute(null);
            Assert.Equal("LRCLIB", m.Vm.LyricsSourceName);
            Assert.Equal(string.Empty, m.Vm.LyricsCreditText);
            Assert.Equal("Try lrc.red", m.Vm.AlternateLyricsLabel);
            Assert.False(m.Vm.ShowLrcRedSync); // synced once; "Try lrc.red" brings those back
            m.Vm.SwitchToAlternateLyricsCommand.Execute(null);
            Assert.True(ShowsWordTiming(m.Vm));
            Assert.Equal("Word-synced by lrc.red's model", m.Vm.LyricsCreditText);
            Assert.Single(m.LrcRed.SyncCalls);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task NotOffered_WhenTheLyricsAreWordSynced_OrLrcRedHasNoRecording()
    {
        // lrc.red's own answer is already word-synced.
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(WordElrc));
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            Assert.False(m.Vm.ShowLrcRedSync);
            Assert.Equal("Lyrics from lrc.red", m.Vm.LyricsCreditText);
        }
        finally { Cleanup(m); }

        // LRCLIB is word-synced, lrc.red only line-synced: the page already has word timing.
        m = Mount(x =>
        {
            x.LrcLib.Answer = Answer(WordElrc, isrc: null);
            x.LrcRed.SearchAnswer = Answer(LineLrc);
        });
        try
        {
            await SearchAsync(m, "LRCLIB");
            Assert.False(m.Vm.ShowLrcRedSync);
        }
        finally { Cleanup(m); }

        // No ISRC on lrc.red's answer: nothing to sync by.
        m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc, isrc: null));
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            Assert.False(m.Vm.ShowLrcRedSync);
            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            Assert.Empty(m.LrcRed.SyncCalls);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task NotOffered_WithLrcRedOff_OrLineSyncedPreferred_AndAPressAfterSwitchingItOffAsksNothing()
    {
        var m = Mount(x =>
        {
            x.Persistence.Settings.LrcRedEnabled = false;
            x.LrcLib.Answer = Answer("[00:09.55]line from LRCLIB", isrc: null);
            x.LrcRed.SearchAnswer = Answer(LineLrc);
        });
        try
        {
            await SearchAsync(m, "LRCLIB");
            Assert.Equal(0, m.LrcRed.Searches);
            Assert.False(m.Vm.ShowLrcRedSync);
        }
        finally { Cleanup(m); }

        m = Mount(x =>
        {
            x.Player.LyricsPreferredType = "LineSynced";
            x.LrcRed.SearchAnswer = Answer(LineLrc);
        });
        try
        {
            Assert.Equal(LyricsPreferredType.LineSynced, LyricsSaveOptions.ParsePreferredType(m.Player.LyricsPreferredType));
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            Assert.False(m.Vm.ShowLrcRedSync);
        }
        finally { Cleanup(m); }

        // Offered, then lrc.red is switched off before the press: the model is not asked.
        m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            Assert.True(m.Vm.ShowLrcRedSync);
            m.Persistence.Settings.LrcRedEnabled = false;
            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            Assert.Empty(m.LrcRed.SyncCalls);
            Assert.False(m.Vm.ShowLrcRedSync);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task Failure_SaysWhy_AndOnlyAnotherPressTriesAgain()
    {
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        m.LrcRed.Sync = (_, _) => Task.FromException<LrcLibResult>(new LrcRedSyncException(LrcRedSyncFailure.RateLimited, "Slow down.", 429));
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);

            Assert.Equal("lrc.red is busy right now. Try again in a minute.", m.Vm.LrcRedSyncError);
            Assert.Equal("Try again", m.Vm.LrcRedSyncLabel);
            Assert.True(m.Vm.ShowLrcRedSync);
            Assert.False(ShowsWordTiming(m.Vm)); // lrc.red's line-synced lyrics stay
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(m.LrcRed.SyncCalls); // never retried on its own

            m.LrcRed.Sync = (_, _) => Task.FromException<LrcLibResult>(new LrcRedSyncException(LrcRedSyncFailure.Refused, "No lyrics with that ISRC.", 404));
            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            Assert.Equal("lrc.red: No lyrics with that ISRC.", m.Vm.LrcRedSyncError);

            m.LrcRed.Sync = (_, _) => Task.FromResult(Answer(WordElrc));
            await m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            Assert.Equal(3, m.LrcRed.SyncCalls.Count);
            Assert.True(ShowsWordTiming(m.Vm));
            Assert.Equal(string.Empty, m.Vm.LrcRedSyncError);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task SongChange_CancelsTheSync_AndItsAnswerNeverLandsOnTheNextSong()
    {
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        var reply = new TaskCompletionSource<LrcLibResult>();
        m.LrcRed.Sync = async (_, ct) =>
        {
            await using (ct.Register(() => reply.TrySetCanceled(ct)))
                return await reply.Task;
        };
        var next = new Track
        {
            Title = "Another Song",
            Artist = "Someone",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(m.Dir, "next.mp3"),
        };
        File.WriteAllText(Path.ChangeExtension(next.FilePath, ".lrc"), "[00:05.00]the next song's own line");
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            var press = m.Vm.WordSyncWithLrcRedCommand.ExecuteAsync(null);
            await PumpUntilAsync(() => m.Vm.IsLrcRedSyncing);
            var token = Assert.Single(m.LrcRed.SyncCalls).Token;

            m.Player.CurrentTrack = next;
            m.Vm.SearchLyricsForTrack(next);
            await PumpUntilAsync(() => m.Vm.LyricLines.Any(l => l.Text == "the next song's own line"));

            Assert.True(token.IsCancellationRequested);
            await press;
            Dispatcher.UIThread.RunJobs();
            Assert.False(m.Vm.IsLrcRedSyncing);
            Assert.False(m.Vm.ShowLrcRedSync);
            Assert.Equal(string.Empty, m.Vm.LrcRedSyncError);
            Assert.Contains(m.Vm.LyricLines, l => l.Text == "the next song's own line");
            Assert.False(File.Exists(Path.ChangeExtension(m.Track.FilePath, ".elrc")));
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task LyricsPage_ShowsThePill_TheReasonAndTheCredit()
    {
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        m.LrcRed.Sync = (_, _) => Task.FromException<LrcLibResult>(new LrcRedSyncException(LrcRedSyncFailure.Network));
        var win = new Window { Width = 1400, Height = 900, Content = new LyricsView { DataContext = m.Vm } };
        win.Show();
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            win.UpdateLayout();

            Button Pill() => win.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("lrcred-sync"));
            Assert.True(Pill().IsEffectivelyVisible);
            Assert.Equal("Word-sync with lrc.red's model", AutomationProperties.GetName(Pill()));
            Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Lyrics from lrc.red" && t.IsEffectivelyVisible);

            Pill().Command!.Execute(null);
            await PumpUntilAsync(() => m.Vm.LrcRedSyncError.Length > 0);
            win.UpdateLayout();
            Assert.Equal("Try again", AutomationProperties.GetName(Pill()));
            Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "Couldn't reach lrc.red. Check your internet connection." && t.IsEffectivelyVisible);
        }
        finally
        {
            win.Close();
            Cleanup(m);
        }
    }

    // ── Search Lyrics ──

    private static LyricsSearchViewModel Picker(LrcLibResult? lrcRedAnswer, bool lrcRedOn,
        Func<string, CancellationToken, Task<LrcLibResult>> sync, Action<LrcLibResult, string>? apply = null) =>
        new(new Track { Title = "Die With A Smile (Acoustic)", Artist = "Lady Gaga" }, new[] { "LRCLIB", OnlineLyricsSource.LrcRed },
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "LRCLIB", OnlineLyricsSource.LrcRed }),
            (_, _, _, _) => Task.FromResult<IReadOnlyList<LyricsSourceHit>>(new[]
            {
                new LyricsSourceHit("LRCLIB", Answer("[00:09.55]from LRCLIB", isrc: null), false),
                new LyricsSourceHit(OnlineLyricsSource.LrcRed, lrcRedAnswer, false),
            }),
            apply ?? ((_, _) => { }))
        {
            SyncWithLrcRedModel = sync,
            CanOfferLrcRedModel = () => Task.FromResult(lrcRedOn),
        };

    [AvaloniaFact]
    public async Task SearchDialog_LrcRedsRowOffersTheModel_AndItsAnswerTakesTheRowsPlace()
    {
        var calls = new List<string>();
        (LrcLibResult Result, string Source)? applied = null;
        var picker = Picker(Answer(LineLrc), lrcRedOn: true, (isrc, _) =>
        {
            calls.Add(isrc);
            return Task.FromResult(Answer(WordElrc));
        }, (r, s) => applied = (r, s));

        await picker.SearchAsync();
        var row = picker.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);
        Assert.True(row.CanModelSync);
        Assert.False(picker.Results.Single(r => r.Source == "LRCLIB").CanModelSync);
        Assert.Equal("Word-sync with lrc.red's model", row.ModelSyncLabel);

        await picker.WordSyncWithLrcRedCommand.ExecuteAsync(row);

        Assert.Equal(new[] { Isrc }, calls);
        var synced = picker.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);
        Assert.NotSame(row, synced);
        Assert.Same(synced, picker.SelectedResult);
        Assert.Equal("Word-synced by lrc.red's model", synced.FormatLabel);
        Assert.False(synced.CanModelSync);
        Assert.Equal("Word-synced by lrc.red's model. Press Use Lyrics to keep them.", picker.StatusText);

        picker.ApplyCommand.Execute(null);
        Assert.Equal(OnlineLyricsSource.LrcRed, applied?.Source);
        Assert.Equal(WordElrc, applied?.Result.SyncedLyrics);
    }

    [AvaloniaFact]
    public async Task SearchDialogFromThePage_ModelSyncedPick_IsShownCreditedToTheModel()
    {
        var m = Mount(x => x.LrcRed.SearchAnswer = Answer(LineLrc));
        m.LrcRed.Sync = (_, _) => Task.FromResult(Answer(WordElrc));
        try
        {
            await SearchAsync(m, OnlineLyricsSource.LrcRed);
            var picker = m.Vm.CreateLyricsSearchViewModel(m.Track);
            picker.SelectedSource = OnlineLyricsSource.LrcRed;
            await PumpUntilAsync(() => picker.Results.Count == 1 && !picker.IsSearching);
            var row = Assert.Single(picker.Results);
            Assert.True(row.CanModelSync);

            await picker.WordSyncWithLrcRedCommand.ExecuteAsync(row);
            picker.ApplyCommand.Execute(null);

            Assert.Single(m.LrcRed.SyncCalls);
            Assert.True(ShowsWordTiming(m.Vm));
            Assert.Equal("Word-synced by lrc.red's model", m.Vm.LyricsCreditText);
            Assert.False(m.Vm.ShowLrcRedSync);
        }
        finally { Cleanup(m); }
    }

    [AvaloniaFact]
    public async Task SearchDialog_NoOffer_WhenLrcRedIsOff_OrItsAnswerIsWordSynced()
    {
        var off = Picker(Answer(LineLrc), lrcRedOn: false, (_, _) => throw new InvalidOperationException());
        await off.SearchAsync();
        Assert.All(off.Results, r => Assert.False(r.CanModelSync));

        var word = Picker(Answer(WordElrc), lrcRedOn: true, (_, _) => throw new InvalidOperationException());
        await word.SearchAsync();
        Assert.All(word.Results, r => Assert.False(r.CanModelSync));
    }

    [AvaloniaFact]
    public async Task SearchDialog_FailureSaysWhyOnTheRow_AndClosingCancelsARunningSync()
    {
        var picker = Picker(Answer(LineLrc), lrcRedOn: true,
            (_, _) => Task.FromException<LrcLibResult>(new LrcRedSyncException(LrcRedSyncFailure.Timeout)));
        await picker.SearchAsync();
        var row = picker.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);

        await picker.WordSyncWithLrcRedCommand.ExecuteAsync(row);
        Assert.Equal("lrc.red's model took too long. Try again in a moment.", row.ModelSyncStatus);
        Assert.Equal("Try again", row.ModelSyncLabel);
        Assert.Same(row, picker.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed));

        CancellationToken seen = default;
        var hang = Picker(Answer(LineLrc), lrcRedOn: true, async (_, ct) =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Answer(WordElrc);
        });
        await hang.SearchAsync();
        var hangRow = hang.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed);
        var running = hang.WordSyncWithLrcRedCommand.ExecuteAsync(hangRow);
        Assert.True(hangRow.IsModelSyncing);
        Assert.Equal("Timing every word… a few seconds", hangRow.ModelSyncStatus);
        hang.CloseCommand.Execute(null);
        await running;
        Assert.True(seen.IsCancellationRequested);
        Assert.False(hangRow.IsModelSyncing);
        Assert.Same(hangRow, hang.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed));
    }

    [AvaloniaFact]
    public async Task SearchDialog_Mounts_WithTheModelButtonOnLrcRedsRow()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("DismissIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
            });
        var picker = Picker(Answer(LineLrc), lrcRedOn: true, (_, _) => Task.FromResult(Answer(WordElrc)));
        await picker.SearchAsync();

        var window = new LyricsSearchDialog(picker) { Width = 1000, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("ls-model") && b.IsEffectivelyVisible);
            Assert.Equal("Word-sync with lrc.red's model", AutomationProperties.GetName(button));
            Assert.Same(picker.Results.Single(r => r.Source == OnlineLyricsSource.LrcRed), button.CommandParameter);

            button.Command!.Execute(button.CommandParameter);
            await PumpUntilAsync(() => picker.SelectedResult?.FormatLabel == "Word-synced by lrc.red's model");
            window.UpdateLayout();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("ls-model") && b.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
}
