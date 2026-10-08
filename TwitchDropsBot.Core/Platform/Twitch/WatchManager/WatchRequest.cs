using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwitchDropsBot.Core.Platform.Shared.Exceptions;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Shared.WatchManager;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Device;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Repository;
using TwitchDropsBot.Core.Platform.Twitch.Utils;
using Stream = TwitchDropsBot.Core.Platform.Twitch.Models.Stream;

namespace TwitchDropsBot.Core.Platform.Twitch.WatchManager;

public class WatchRequest : ITwitchWatchManager
{
    public TwitchUser BotUser { get; }
    private ILogger _logger;

    private string? streamUrl;
    private readonly TwitchGqlRepository twitchGraphQlClient;
    private DateTime lastRequestTime;
    private readonly bool enableOldSystem;
    // Reused across every WatchStreamAsync call instead of `new HttpClient()`
    // per call — this method fires roughly once a minute for the entire
    // lifetime of a watch session, and a fresh HttpClient per call leaks
    // handles/sockets over long-running processes (the well-documented .NET
    // HttpClient-per-request anti-pattern).
    private readonly HttpClient client;

    // --- Stream segment requests -------------------------------------------
    // Since 2026-10-07 ~21:30Z Twitch only credits Drops watch time to a
    // viewer that actually requests the stream's media segments; the
    // minute-watched event alone stopped counting (measured across our bots:
    // ~98% of watched minutes credited before, ~1% after, while accounts that
    // request every new segment earn one minute per minute again). So while a
    // channel is being watched, a background loop reads the lowest-quality
    // playlist and sends a HEAD for each new segment. No audio or video is
    // downloaded.
    // The playlist holds the last ~30 s of segments (15 x 2 s). Reading it
    // every 10, 20 or 30 s was credited alike; 15 s halves the playlist
    // traffic of a 10 s poll and still leaves room for a slow request.
    private const int SegmentPollSeconds = 15;
    // The loop only runs while WatchStreamAsync keeps being called: if the
    // account's watch loop dies without calling Close(), it stops by itself.
    private const int SegmentLeaseSeconds = 180;
    private const int SegmentMemory = 256;
    private static readonly HttpClient hlsClient = CreateHlsClient();
    private CancellationTokenSource? segmentCts;
    private Task? segmentTask;
    private string? segmentChannel;
    private long segmentLeaseTicks;

    public WatchRequest(TwitchUser user, ILogger logger, bool enableOldSystem)
    {
        BotUser = user;
        twitchGraphQlClient = BotUser.TwitchRepository;
        this.enableOldSystem = enableOldSystem;
        lastRequestTime = DateTime.MinValue;
        streamUrl = null;

        _logger = logger;

        client = new HttpClient();
        client.DefaultRequestHeaders.Add("Connection", "close");
    }

    /*
     * Inspired by DevilXD's TwitchDropsMiner
     * https://github.dev/DevilXD/TwitchDropsMiner/blob/b20f98da7a72ddca20eb462229faf330026b3511/channel.py#L76
     */
    public async Task WatchStreamAsync(User broadcaster, Game game)
    {
        DateTime requestTime = DateTime.Now;

        try
        {
            EnsureSegmentWatch(broadcaster);

            if (enableOldSystem)
            {
                if (streamUrl == null)
                {
                    PlaybackAccessToken? streamPlaybackAccessToken =
                        await twitchGraphQlClient.FetchPlaybackAccessTokenAsync(broadcaster.Login);

                    var requestBroadcastQualitiesURL =
                        $"https://usher.ttvnw.net/api/channel/hls/{broadcaster.Login}.m3u8?sig={streamPlaybackAccessToken!.Signature}&token={streamPlaybackAccessToken!.Value}";

                    HttpResponseMessage response = await client.GetAsync(requestBroadcastQualitiesURL);
                    response.EnsureSuccessStatusCode();
                    string responseBody = await response.Content.ReadAsStringAsync();

                    string[] lines = responseBody.Split("\n");
                    var regex = new Regex(@"VIDEO=""([^""]+)""");
                    var qualitiesPlaylist = new Dictionary<string, string>();
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("https"))
                        {
                            var previousLine = Array.IndexOf(lines, line) - 1;
                            var match = regex.Match(lines[previousLine]);
                            if (match.Success)
                            {
                                qualitiesPlaylist.Add(match.Groups[1].Value, line);
                            }
                        }
                    }

                    if (qualitiesPlaylist.TryGetValue("chunked", out var chunkedUrl))
                    {
                        streamUrl = chunkedUrl;
                    }
                    else
                    {
                        streamUrl = qualitiesPlaylist.Values.FirstOrDefault();
                    }
                }

                HttpResponseMessage response2 = await client.GetAsync(streamUrl);
                response2.EnsureSuccessStatusCode();
                string responseBody2 = await response2.Content.ReadAsStringAsync();

                string[] lines2 = responseBody2.Split("\n");
                string lastLine2 = lines2[lines2.Length - 2];

                HttpResponseMessage response3 =
                    await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, lastLine2));
                response3.EnsureSuccessStatusCode();
            }

            if ((requestTime - lastRequestTime).TotalSeconds >= 59)
            {
                var tempBroadcaster = await twitchGraphQlClient.FetchStreamInformationAsync(broadcaster.Login);

                if (tempBroadcaster is not null)
                {
                    if (tempBroadcaster.Stream is null)
                    {
                        throw new StreamOffline();
                    }

                    if (game.DisplayName != "Special Events")
                    {
                        if (tempBroadcaster?.BroadcastSettings?.Game?.Id != game.Id)
                        {
                            throw new StreamOffline("Wrong game");
                        }
                    }
                }

                if (tempBroadcaster?.Stream is not null)
                {
                    var stream = tempBroadcaster.Stream;

                    if (Constant.TwitchDevice == TwitchDeviceType.ANDROID_APP)
                    {
                        var payload = GetPayload(tempBroadcaster, stream, game, true);
                        await twitchGraphQlClient.SimulateWatchMobileAsync(payload);
                    }
                    else
                    {
                        var payload = GetPayload(tempBroadcaster, stream, game, false);
                        await twitchGraphQlClient.SimulateWatchAsync(payload);
                    }
                }

                lastRequestTime = DateTime.Now;
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex.Message);
            throw;
        }
    }

    public async Task<DropCurrentSession?> FakeWatchAsync(User broadcaster, Game game, int tryCount = 1)
    {
        _logger.LogDebug("Watching {seconds} seconds to ensure drops are registered...", (20 * tryCount));

        for (int i = 0; i < tryCount; i++)
        {
            await WatchStreamAsync(broadcaster, game);
            await Task.Delay(TimeSpan.FromSeconds(20));
            Close();
        }

        return await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);
    }

    public void Close()
    {
        StopSegmentWatch();
        streamUrl = null;
        lastRequestTime = DateTime.MinValue;
    }

    private static HttpClient CreateHlsClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var hls = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        hls.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Constant.TwitchDevice.UserAgents[0]);
        return hls;
    }

    private void EnsureSegmentWatch(User broadcaster)
    {
        Interlocked.Exchange(ref segmentLeaseTicks, DateTime.UtcNow.AddSeconds(SegmentLeaseSeconds).Ticks);

        if (string.IsNullOrEmpty(broadcaster.Login))
        {
            return;
        }

        if (segmentTask is { IsCompleted: false } && segmentChannel == broadcaster.Login)
        {
            return;
        }

        StopSegmentWatch();

        var login = broadcaster.Login;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            BotUser.CancellationTokenSource?.Token ?? CancellationToken.None);
        segmentChannel = login;
        segmentCts = cts;
        segmentTask = Task.Run(() => SegmentLoopAsync(login, cts.Token));
    }

    private void StopSegmentWatch()
    {
        var cts = segmentCts;
        segmentCts = null;
        segmentTask = null;
        segmentChannel = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task SegmentLoopAsync(string login, CancellationToken ct)
    {
        string? playlistUrl = null;
        var seen = new HashSet<string>();
        var seenOrder = new Queue<string>();
        var failures = 0;

        _logger.LogDebug("Requesting the stream segments of {login} so the watch time counts.", login);

        try
        {
            while (!ct.IsCancellationRequested &&
                   DateTime.UtcNow.Ticks < Interlocked.Read(ref segmentLeaseTicks))
            {
                var started = DateTime.UtcNow;
                var ok = false;

                try
                {
                    // One poll never outlives its slot, whatever a request does.
                    using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    poll.CancelAfter(TimeSpan.FromSeconds(SegmentPollSeconds + 5));

                    playlistUrl ??= await FetchPlaylistUrlAsync(login, poll.Token);

                    if (playlistUrl is not null)
                    {
                        ok = await RequestNewSegmentsAsync(playlistUrl, seen, seenOrder, poll.Token);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (System.Exception ex)
                {
                    _logger.LogDebug("Stream segment request for {login} failed: {message}", login, ex.Message);
                }

                if (ok)
                {
                    failures = 0;
                }
                else
                {
                    failures++;

                    // The playlist address is signed and expires; ask for a new one.
                    if (failures % 3 == 0)
                    {
                        playlistUrl = null;
                    }

                    if (failures == 6)
                    {
                        _logger.LogWarning(
                            "Could not request the stream segments of {login} for a minute, watch time may not count.",
                            login);
                    }
                }

                var wait = TimeSpan.FromSeconds(SegmentPollSeconds) - (DateTime.UtcNow - started);
                if (wait < TimeSpan.FromSeconds(1))
                {
                    wait = TimeSpan.FromSeconds(1);
                }

                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> FetchPlaylistUrlAsync(string login, CancellationToken ct)
    {
        PlaybackAccessToken? token = await twitchGraphQlClient.FetchPlaybackAccessTokenAsync(login);

        if (string.IsNullOrEmpty(token?.Signature) || string.IsNullOrEmpty(token?.Value))
        {
            return null;
        }

        var masterUrl =
            $"https://usher.ttvnw.net/api/channel/hls/{login}.m3u8?sig={Uri.EscapeDataString(token.Signature)}" +
            $"&token={Uri.EscapeDataString(token.Value)}&allow_source=true&allow_audio_only=true";

        using var response = await hlsClient.GetAsync(masterUrl, ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var qualities = PlaylistUris(await response.Content.ReadAsStringAsync(ct), masterUrl, "#EXT-X-STREAM-INF:");

        // The last quality is the lightest one (audio only, or the lowest bitrate).
        return qualities.LastOrDefault();
    }

    // True when the playlist was read and every segment in it has been requested.
    private static async Task<bool> RequestNewSegmentsAsync(string playlistUrl, HashSet<string> seen,
        Queue<string> seenOrder, CancellationToken ct)
    {
        using var response = await hlsClient.GetAsync(playlistUrl, ct);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var segments = PlaylistUris(await response.Content.ReadAsStringAsync(ct), playlistUrl, "#EXTINF:");

        if (segments.Count == 0)
        {
            return false;
        }

        var ok = true;

        foreach (var segment in segments)
        {
            if (seen.Contains(segment))
            {
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Head, segment);
            using var head = await hlsClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!head.IsSuccessStatusCode)
            {
                ok = false;
                continue;
            }

            seen.Add(segment);
            seenOrder.Enqueue(segment);

            if (seenOrder.Count > SegmentMemory)
            {
                seen.Remove(seenOrder.Dequeue());
            }
        }

        return ok;
    }

    // The address lines of an HLS playlist that follow the given tag
    // (#EXT-X-STREAM-INF = qualities of a master playlist, #EXTINF = segments).
    internal static List<string> PlaylistUris(string playlist, string baseUrl, string tag)
    {
        var uris = new List<string>();
        var pending = false;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(tag, StringComparison.Ordinal))
                {
                    pending = true;
                }

                continue;
            }

            if (!pending)
            {
                continue;
            }

            pending = false;

            if (Uri.TryCreate(new Uri(baseUrl), line, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                uris.Add(uri.AbsoluteUri);
            }
        }

        return uris;
    }

    private string GetPayload(User broadcaster, Stream stream, Game game, bool onlyB64 = false)
    {
        var payload = new[]
        {
            new Dictionary<string, object>
            {
                ["event"] = "minute-watched",
                ["properties"] = new Dictionary<string, object>
                {
                    ["broadcast_id"] = stream.Id,
                    ["channel_id"] = broadcaster.Id,
                    ["channel"] = broadcaster.Login,
                    ["client_time"] = DateTime.UtcNow.ToString("o").Replace("+00:00", "Z"),
                    ["game"] = game.Name ?? game.DisplayName ?? "",
                    ["game_id"] = game.Id ?? "",
                    ["hidden"] = false,
                    ["is_live"] = true,
                    ["live"] = true,
                    ["logged_in"] = true,
                    ["minutes_logged"] = 1,
                    ["muted"] = false,
                    ["user_id"] = int.Parse(BotUser.Id),
                }
            }
        };
        
        var json = JsonSerializer.Serialize(payload);
        
        if (onlyB64)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }
        
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }

        var compressedBytes = output.ToArray();
        var b64 = Convert.ToBase64String(compressedBytes);
        return b64;
    }
}