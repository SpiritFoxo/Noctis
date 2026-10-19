using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// An online lyrics source past LRCLIB and NetEase (issue #113): the name the lyrics page
/// shows ("Try Kugou"), its Settings switch for automatic lookups, and its search.
/// </summary>
public sealed record OnlineLyricsSource(
    string Name,
    Func<AppSettings, bool> IsEnabled,
    Func<string, string, double, CancellationToken, Task<LrcLibResult?>> SearchAsync)
{
    public const string Musixmatch = "Musixmatch";
    public const string Kugou = "Kugou";
    public const string YouTubeMusic = "YouTube Music";
    public const string LrcRed = "lrc.red";
    public const string Unison = "Unison";

    /// <summary>The built-in extra sources in Auto's priority order: on a format tie the earlier wins,
    /// so Musixmatch's hand-made richsync beats lrc.red's and Unison's TTML, which keep duet voices
    /// and background vocals and so beat Kugou's KRC; all beat YouTube's line sync.</summary>
    public static IReadOnlyList<OnlineLyricsSource> BuiltIns(
        IMusixmatchService musixmatch, IKugouLyricsService kugou, IYouTubeMusicLyricsService youTubeMusic,
        ILrcRedLyricsService lrcRed, IUnisonLyricsService unison) =>
    [
        new(Musixmatch, s => s.MusixmatchEnabled, musixmatch.SearchLyricsAsync),
        new(LrcRed, s => s.LrcRedEnabled, lrcRed.SearchLyricsAsync),
        new(Unison, s => s.UnisonEnabled, unison.SearchLyricsAsync),
        new(Kugou, s => s.KugouEnabled, kugou.SearchLyricsAsync),
        new(YouTubeMusic, s => s.YouTubeMusicLyricsEnabled, youTubeMusic.SearchLyricsAsync),
    ];
}

/// <summary>One source's answer in a lyrics search: what it found (null = nothing), or that it could not
/// answer — <paramref name="Busy"/> when it said it was busy (503/429) rather than being unreachable.</summary>
public sealed record LyricsSourceHit(string Source, LrcLibResult? Result, bool Errored, bool Busy = false);
