using Noctis.Models;

namespace Noctis.Services;

public interface ILrcRedLyricsService
{
    /// <summary>
    /// Looks the track up on lrc.red and returns its TTML lyrics as ELRC (word-synced, with duet
    /// voices and background vocals), LRC (line-synced) or plain text, or null on a definitive miss.
    /// The result carries the recording's <see cref="LrcLibResult.Isrc"/>.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);

    /// <summary>
    /// The lookup alone: the recording lrc.red has for this track (its ISRC and timing level), or
    /// null when it has none. Remembered for a while, so asking again costs no request.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);

    /// <summary>
    /// "Sync with the model" on lrc.red: one POST that has lrc.red's in-house alignment model time
    /// every word of its own copy of the recording (no audio is sent), returned like
    /// <see cref="SearchLyricsAsync"/>'s word-synced lyrics. Only ever called on a user's press —
    /// the model is donation-funded — and never retried. Throws <see cref="LrcRedSyncException"/>
    /// when it fails; <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default);
}

/// <summary>The recording lrc.red's lookup matched to a track. <see cref="Timing"/> is "word", "line" or "none".</summary>
public sealed record LrcRedMatch(string Isrc, string Title, string Artist, string? Album, double DurationSeconds, string Timing)
{
    public bool IsWordSynced => Timing == "word";
}

/// <summary>Why a model sync failed; the views turn it into words.</summary>
public enum LrcRedSyncFailure
{
    /// <summary>No connection, or the transfer broke.</summary>
    Network,
    /// <summary>The model took longer than <see cref="LrcRedLyricsService.ModelSyncTimeout"/>.</summary>
    Timeout,
    /// <summary>429: too many syncs.</summary>
    RateLimited,
    /// <summary>Any other refusal; <see cref="LrcRedSyncException.ServerMessage"/> says why when lrc.red did.</summary>
    Refused,
    /// <summary>An answer Noctis could not read.</summary>
    BadResponse,
    /// <summary>The model answered without word timings.</summary>
    NotWordSynced,
}

public sealed class LrcRedSyncException : Exception
{
    public LrcRedSyncFailure Failure { get; }

    /// <summary>lrc.red's own "error" text (one line, at most 200 characters), when it sent one.</summary>
    public string? ServerMessage { get; }

    /// <summary>The HTTP status of a refusal; null when there was no answer.</summary>
    public int? StatusCode { get; }

    public LrcRedSyncException(LrcRedSyncFailure failure, string? serverMessage = null, int? statusCode = null, Exception? inner = null)
        : base($"lrc.red model sync failed: {failure}{(statusCode is { } s ? $" ({s})" : "")}{(serverMessage is null ? "" : $" — {serverMessage}")}", inner)
    {
        Failure = failure;
        ServerMessage = serverMessage;
        StatusCode = statusCode;
    }
}
