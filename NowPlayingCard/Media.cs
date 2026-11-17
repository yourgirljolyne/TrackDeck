using Windows.Media.Control;

namespace NowPlayingCard;

public sealed record NowPlaying(string Title, string Artist, string Album, bool IsPlaying, byte[]? Cover, string Source)
{
    /// <summary>Identity of what is shown on the card (excluding cover bytes, which can arrive late).</summary>
    public string Key => $"{Title}\u0001{Artist}\u0001{Album}\u0001{IsPlaying}\u0001{Cover?.Length ?? 0}";
}

/// <summary>Reads what Windows says is playing (System Media Transport Controls), preferring Spotify.</summary>
public sealed class MediaReader
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    public bool SpotifyOnly { get; set; } = true;

    public async Task<NowPlaying?> ReadAsync()
    {
        _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = _mgr.GetSessions();
        var session = sessions.FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
        if (session == null && !SpotifyOnly) session = _mgr.GetCurrentSession();
        if (session == null) return null;

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

        var status = session.GetPlaybackInfo()?.PlaybackStatus;
        bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        byte[]? cover = null;
        if (props.Thumbnail != null)
        {
            try
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var s = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                cover = ms.ToArray();
            }
            catch { /* thumbnail not ready yet */ }
        }

        return new NowPlaying(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "", playing, cover, session.SourceAppUserModelId);
    }
}
