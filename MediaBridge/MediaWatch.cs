// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaBridge;

using Session = GlobalSystemMediaTransportControlsSession;

/// <summary>
/// The current system media session (the one Windows shows in its media flyout): its track, cover, position and
/// whether it plays — sent on every change and once a second while playing (not every player moves its own
/// position) — and the commands that drive it.
/// </summary>
sealed class MediaWatch(Action<JsonObject> emit)
{
    GlobalSystemMediaTransportControlsSessionManager? manager;
    Session? current;
    string artKey = "\0";
    volatile bool playing;
    readonly SemaphoreSlim push = new(1, 1);
    Timer? tick;

    public async Task StartAsync()
    {
        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        manager.CurrentSessionChanged += (_, _) => Bind();
        manager.SessionsChanged += (_, _) => Bind();
        Bind();
        tick = new Timer(_ =>
        {
            if (playing) _ = PushAsync();
        }, null, 1000, 1000);
    }

    void Bind()
    {
        var next = manager?.GetCurrentSession();
        if (next?.SourceAppUserModelId != current?.SourceAppUserModelId || (next is null) != (current is null))
        {
            if (current is not null)
            {
                current.MediaPropertiesChanged -= OnProps;
                current.PlaybackInfoChanged -= OnPlayback;
                current.TimelinePropertiesChanged -= OnTimeline;
            }
            current = next;
            if (current is not null)
            {
                current.MediaPropertiesChanged += OnProps;
                current.PlaybackInfoChanged += OnPlayback;
                current.TimelinePropertiesChanged += OnTimeline;
            }
        }
        _ = PushAsync();
    }

    void OnProps(Session s, MediaPropertiesChangedEventArgs e) => _ = PushAsync();
    void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => _ = PushAsync();
    void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => _ = PushAsync();

    async Task PushAsync()
    {
        await push.WaitAsync();
        try
        {
            var s = current;
            if (s is null)
            {
                playing = false;
                emit(new JsonObject { ["t"] = "media" });
                SendArt("", null);
                return;
            }
            GlobalSystemMediaTransportControlsSessionMediaProperties? p = null;
            try { p = await s.TryGetMediaPropertiesAsync(); } catch (COMException) { /* the player is closing */ }
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();
            playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var len = (tl.EndTime - tl.StartTime).TotalSeconds;
            var pos = (tl.Position - tl.StartTime).TotalSeconds;
            // The timeline says where it was at LastUpdatedTime; while playing, it has moved on since.
            if (playing && tl.LastUpdatedTime.Year > 2000)
                pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds * (info.PlaybackRate ?? 1);
            if (len > 0) pos = Math.Clamp(pos, 0, len);
            var app = AppName(s.SourceAppUserModelId);
            emit(new JsonObject
            {
                ["t"] = "media",
                ["app"] = app,
                ["id"] = s.SourceAppUserModelId,
                ["title"] = p?.Title ?? "",
                ["artist"] = p?.Artist is { Length: > 0 } a ? a : p?.AlbumArtist ?? "",
                ["album"] = p?.AlbumTitle ?? "",
                ["playing"] = playing,
                ["pos"] = Math.Round(Math.Max(0, pos), 1),
                ["len"] = Math.Round(Math.Max(0, len), 1),
                ["canPrev"] = info.Controls.IsPreviousEnabled,
                ["canNext"] = info.Controls.IsNextEnabled,
            });
            SendArt($"{s.SourceAppUserModelId}|{p?.Title}|{p?.Artist}|{p?.AlbumTitle}", p?.Thumbnail);
        }
        catch (Exception e)
        {
            emit(new JsonObject { ["t"] = "error", ["message"] = $"media: {e.Message}" });
        }
        finally
        {
            push.Release();
        }
    }

    /// <summary>The cover, once per track: shrunk to 256 px and sent as a JPEG data URL ("" = none).</summary>
    void SendArt(string key, IRandomAccessStreamReference? thumb)
    {
        if (key == artKey) return;
        artKey = key;
        _ = Task.Run(async () =>
        {
            var art = thumb is null ? "" : await ArtAsync(thumb) ?? "";
            if (key == artKey) emit(new JsonObject { ["t"] = "art", ["art"] = art });
        });
    }

    static async Task<string?> ArtAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var src = await thumb.OpenReadAsync();
            var dec = await BitmapDecoder.CreateAsync(src);
            var k = Math.Min(1.0, 256.0 / Math.Max(dec.PixelWidth, dec.PixelHeight));
            var (w, h) = ((uint)Math.Max(1, dec.PixelWidth * k), (uint)Math.Max(1, dec.PixelHeight * k));
            var px = await dec.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            using var mem = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, px.DetachPixelData());
            await enc.FlushAsync();
            var bytes = new byte[mem.Size];
            using var reader = new DataReader(mem.GetInputStreamAt(0));
            await reader.LoadAsync((uint)mem.Size);
            reader.ReadBytes(bytes);
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception)
        {
            return null; // a broken or missing thumbnail: no cover rather than no track
        }
    }

    static readonly (string Has, string Name)[] Known =
    [
        ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("308046b0af4a39cb", "Firefox"),
        ("opera", "Opera"), ("brave", "Brave"), ("yandex", "Yandex Browser"), ("vivaldi", "Vivaldi"),
        ("spotify", "Spotify"), ("vlc", "VLC"), ("applemusic", "Apple Music"), ("zunemusic", "Media Player"),
        ("zunevideo", "Movies & TV"), ("itunes", "iTunes"), ("foobar", "foobar2000"), ("aimp", "AIMP"),
        ("yandexmusic", "Yandex Music"), ("deezer", "Deezer"), ("tidal", "TIDAL"),
    ];

    /// <summary>The player's name from its app id: a known one by name ("Chrome.…UserData.Profile2" → Chrome,
    /// "Spotify.exe" → Spotify), else the id's first part.</summary>
    internal static string AppName(string id)
    {
        var low = id.ToLowerInvariant();
        foreach (var (has, name) in Known.OrderByDescending((k) => k.Has.Length))
            if (low.Contains(has)) return name;
        var n = id.Contains('!') ? id[(id.IndexOf('!') + 1)..] : id;
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        var dot = n.IndexOf('.');
        return dot > 0 ? n[..dot] : n;
    }

    public async Task CommandAsync(string c)
    {
        var s = current;
        if (s is null)
        {
            // No session: the media key — Windows hands it to the last player.
            MediaKey(c switch { "next" => 0xB0, "prev" => 0xB1, "stop" => 0xB2, _ => 0xB3 });
            return;
        }
        IAsyncOperation<bool> op = c switch
        {
            "next" => s.TrySkipNextAsync(),
            "prev" => s.TrySkipPreviousAsync(),
            "stop" => s.TryStopAsync(),
            _ => s.TryTogglePlayPauseAsync(),
        };
        await op;
    }

    public async Task SeekAsync(double seconds)
    {
        var s = current;
        if (s is null) return;
        var start = s.GetTimelineProperties().StartTime;
        await s.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(Math.Max(0, seconds))).Ticks);
    }

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static void MediaKey(byte vk)
    {
        const uint extended = 0x1, up = 0x2;
        keybd_event(vk, 0, extended, UIntPtr.Zero);
        keybd_event(vk, 0, extended | up, UIntPtr.Zero);
    }
}
