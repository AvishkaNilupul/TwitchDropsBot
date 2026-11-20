using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphQL.Client.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Shared.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Repository;
using TwitchDropsBot.Core.Platform.Twitch.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Utils;
using TwitchDropsBot.Core.Platform.Twitch.WatchManager;

const string campaignId = "b7669f9e-c69c-4a9b-a3b2-36266c1bdfc0";
const string gameId = "1867279146";
const string syntheticCode = "SYNTHETIC-TEST-CODE-ONLY";
var checks = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
var directory = Path.Combine(Path.GetTempPath(), "reward-worker-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var previousKey = Environment.GetEnvironmentVariable("TWITCH_REWARD_CODE_KEY");
Environment.SetEnvironmentVariable("TWITCH_REWARD_CODE_KEY", key);
try
{
    var settings = new BotSettings();
    settings.LogLevel = 1;
    settings.TwitchSettings.ClaimDrops = false;
    settings.TwitchSettings.RewardCodeDirectory = directory;
    var target = new RewardCampaignTarget { CampaignId = campaignId, GameId = gameId,
        GameName = "Grand Theft Auto VI", GameSlug = "grand-theft-auto-vi", SessionMinutes = 1 };
    var user = (TwitchUser)RuntimeHelpers.GetUninitializedObject(typeof(TwitchUser));
    user.Id = "123"; user.Login = "synthetic-viewer"; user.ClientSecret = "fixture-only";
    user.UniqueId = "fixture-only"; user.FavouriteGames = [target.GameName];
    user.CancellationTokenSource = new();
    var options = new TestOptions(settings);
    var logger = new TestLogger();
    var repo = new TwitchGqlRepository(user, logger, options);
    user.TwitchRepository = repo;
    using var transport = new FixtureTransport(campaignId, gameId, syntheticCode);
    using var http = new HttpClient(transport);
    using var gql = new GraphQLHttpClient("https://fixture.invalid/gql", TwitchJson.CreateGraphQLSerializer(), http);
    var field = typeof(TwitchGqlRepository).GetField("graphQLClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using var originalClient = (GraphQLHttpClient)field.GetValue(repo)!;
    field.SetValue(repo, gql);
    var redeemField = typeof(TwitchGqlRepository).GetField("redeemGraphQLClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using var originalRedeemClient = (GraphQLHttpClient)redeemField.GetValue(repo)!;
    redeemField.SetValue(repo, gql);
    var watcher = new TestWatcher(user);
    typeof(TwitchUser).GetField("<WatchManager>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(user, watcher);
    var bot = new TwitchBot(user, logger, new NotificationService(options), options);
    Task Invoke(string method, params object[] args) => (Task)typeof(TwitchBot)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bot, args)!;
    async Task<bool> TryWatch(RewardCampaign campaign) => await (Task<bool>)typeof(TwitchBot)
        .GetMethod("TryWatchRewardCampaignAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(bot, new object[] { new[] { campaign } })!;

    var campaigns = await repo.FetchDropsAsync();
    Check(campaigns.Count == 1 && campaigns[0].Id == "standard-control", "Default standard-drop behavior changed");
    settings.TwitchSettings.RewardCampaignTargets.Add(target);
    campaigns = await repo.FetchDropsAsync();
    var gta = campaigns.OfType<RewardCampaign>().Single();
    Check(gta.Id == campaignId && gta.Game?.Id == gameId && gta.Game.IsFavorite, "Explicit null-game GTA target not selected");
    Check(campaigns.Any(c => c.Id == "standard-control"), "Ordinary Drop control was lost");
    var before = transport.Operations.Count;
    await Invoke("CheckForClaim", new Inventory());
    Check(transport.Operations.Count == before, "No-claim/default collector made a claim call");
    var inventory = JsonSerializer.Deserialize<Inventory>("""
    {"dropCampaignsInProgress":[{"timeBasedDrops":[{"self":{"isClaimed":false,"dropInstanceID":"never-claim-this"}}]}],
     "earnedDropRewards":{"edges":[{"node":{"id":"earned-1","status":"CLAIMED",
       "campaign":{"id":"giveaway-campaign","name":"GTA VI giveaway"},
       "item":{"name":"Synthetic game code","distributionType":"CODE"}}},
       {"node":{"id":"badge-1","campaign":{"id":"badge"},"item":{"distributionType":"BADGE"}}}]}}
    """, TwitchJson.CreateOptions(propertyNameCaseInsensitive: true))!;
    settings.TwitchSettings.CollectRewardCodes = true;
    await Invoke("CheckForClaim", inventory);
    Check(transport.Operations.Count(op => op == "RewardCodeModal") == 1, "Issued code was not fetched with ClaimDrops=false");
    var store = new RewardCodeStore(directory, key);
    Check(store.Load(user.Id, "earned-1")?.Code == syntheticCode, "Code did not survive encrypted receipt round trip");
    Check(!string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText)).Contains(syntheticCode), "Plaintext code leaked to disk");
    await Invoke("CheckForClaim", inventory);
    Check(transport.Operations.Count(op => op == "RewardCodeModal") == 1, "Durable receipt failed to deduplicate");
    Check(!logger.Messages.Any(message => message.Contains(syntheticCode)), "Code leaked into logs");
    Check(store.List().Single().Code == syntheticCode, "Encrypted receipt listing failed");
    var cli = Assembly.Load("TwitchDropsBot.Console").GetType("TwitchDropsBot.Console.Utils.RewardCodesCommand")!
        .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
    var consoleOutput = Console.Out;
    using var listingOutput = new StringWriter();
    try
    {
        Console.SetOut(listingOutput);
        Check((int)cli.Invoke(null, new object[] { new[] { "reward-codes", "list", directory } })! == 0,
            "Receipt list command failed");
        Check(!listingOutput.ToString().Contains(syntheticCode) && listingOutput.ToString().Contains("earned-1"),
            "Receipt list command leaked a code or omitted identity");
        listingOutput.GetStringBuilder().Clear();
        Check((int)cli.Invoke(null, new object[] { new[] { "reward-codes", "reveal", directory, user.Id, "earned-1" } })! == 0 &&
            listingOutput.ToString().Trim() == syntheticCode, "Explicit receipt reveal command failed");
    }
    finally { Console.SetOut(consoleOutput); }
    Check(!transport.Operations.Contains("DropsPage_ClaimDropRewards"), "Collector claimed an ordinary drop");

    var receipt = store.Load(user.Id, "earned-1")!;
    store.Save(receipt);
    Check(Directory.GetFiles(directory).Length == 1, "Idempotent retry created extra receipt");
    try { store.Save(receipt with { Code = "DIFFERENT" }); throw new Exception("Overwrite accepted"); }
    catch (InvalidDataException) { checks++; }
    try { new RewardCodeStore(directory, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).Load(user.Id, "earned-1"); throw new Exception("Wrong key accepted"); }
    catch (CryptographicException) { checks++; }
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(Directory.GetFiles(directory).Single()) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "Code receipt is readable by other users");

    transport.CodeValue = "";
    inventory.EarnedDropRewards.Edges[0].Node.Id = "earned-empty";
    await Invoke("CheckForClaim", inventory);
    await Invoke("CheckForClaim", inventory);
    Check(transport.Operations.Count(op => op == "RewardCodeModal") == 2 && store.Load(user.Id, "earned-empty") == null,
        "Empty code was persisted or immediate retry was not throttled");
    transport.CodeValue = syntheticCode;
    settings.TwitchSettings.CollectRewardCodes = false;
    var progress = await repo.FetchDropCampaignsProgressAsync(new User { Id = "channel" }, preserveUnknownProgress: true);
    Check(progress.Single().RewardGroups.Single().Self.CurrentMinutesWatched == null, "Unknown reward counter fabricated zero");
    progress = await repo.FetchDropCampaignsProgressAsync(new User { Id = "channel" });
    Check(progress.Single().RewardGroups.Single().Self.CurrentMinutesWatched == 0, "Ordinary progress normalization changed");

    // Cancel on the first watch call: exercise the real reward path without waiting a minute.
    try { await TryWatch(gta); throw new Exception("Cancellation not propagated"); }
    catch (OperationCanceledException) { checks++; }
    Check(watcher.Watches == 1 && watcher.Closes == 1, "Reward watch did not start and close on cancellation");
    Check(user.CurrentMinutesWatched == null && user.RequiredMinutesWatched == null, "Unknown progress was turned into completion");
    Check(!transport.Operations.Contains("DropCampaignDetails"), "Reward campaign was sent through ordinary Drop details");
    Check(!transport.Operations.Any(op => op.Contains("Claim", StringComparison.OrdinalIgnoreCase)), "Reward watching made an ordinary claim call");
    gta.EndsAt = DateTime.UtcNow.AddMinutes(-1);
    Check(!await TryWatch(gta), "Ended campaign was watched");
    gta.EndsAt = null;
    settings.TwitchSettings.AvoidCampaign.Add(gta.Name);
    Check(!await TryWatch(gta), "AvoidCampaign was ignored");
    settings.TwitchSettings.AvoidCampaign.Clear();
    transport.WrongGame = true;
    Check(!await TryWatch(gta), "Wrong-category broadcaster was watched");
    transport.WrongGame = false;
    target.WindowEnd = DateTime.UtcNow.AddSeconds(-1);
    Check(!await TryWatch(gta), "Configured expired giveaway window was watched");
    target.WindowEnd = null;
    settings.TwitchSettings.RewardCampaignTargets.Clear();
    settings.TwitchSettings.RewardCampaignsOnly = true;
    settings.TwitchSettings.TwitchUsers = [new TwitchUserSettings { Id = user.Id, FavouriteGames = [target.GameName] }];
    try { await Invoke("StartAsync"); throw new Exception("Reward-only worker fell back to ordinary Drops"); }
    catch (TwitchDropsBot.Core.Platform.Shared.Exceptions.NoBroadcasterOrNoCampaignLeft) { checks++; }
    var keyFile = Path.Combine(directory, "key-file", "reward-code.key");
    var codeDirectory = Path.Combine(directory, "key-file", "receipts");
    var fileStore = RewardCodeStore.FromKeyFile(codeDirectory, keyFile);
    fileStore.Save(receipt);
    var originalKeyText = File.ReadAllText(keyFile);
    Check(RewardCodeStore.FromKeyFile(codeDirectory, keyFile).Load(user.Id, "earned-1")?.Code == syntheticCode &&
        File.ReadAllText(keyFile) == originalKeyText, "Persistent key was regenerated or receipt lost on restart");
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(keyFile) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
            "Generated key file is readable by other users");
    Console.WriteLine($"PASS: {checks} reward-worker checks; all transports were fixtures, zero live watch/claim requests.");
}
finally
{
    Environment.SetEnvironmentVariable("TWITCH_REWARD_CODE_KEY", previousKey);
    Directory.Delete(directory, recursive: true);
}

sealed class TestOptions(BotSettings settings) : IOptionsMonitor<BotSettings>
{
    public BotSettings CurrentValue => settings;
    public BotSettings Get(string? name) => settings;
    public IDisposable? OnChange(Action<BotSettings, string?> listener) => null;
}
sealed class TestLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
sealed class TestWatcher(TwitchUser user) : ITwitchWatchManager
{
    public TwitchUser BotUser => user;
    public int Watches; public int Closes;
    public Task WatchStreamAsync(User broadcaster, Game game) { Watches++; user.CancellationTokenSource.Cancel(); return Task.CompletedTask; }
    public void Close() => Closes++;
    public Task<DropCurrentSession?> FakeWatchAsync(User broadcaster, Game game, int tryCount = 3) => throw new Exception("Ordinary Drop fake-watch was used");
}
sealed class FixtureTransport(string campaignId, string gameId, string code) : HttpMessageHandler
{
    public List<string> Operations { get; } = [];
    public string CodeValue = code;
    public bool WrongGame;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri?.Host != "fixture.invalid") throw new Exception("Live request attempted");
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var op = payload.RootElement.GetProperty("operationName").GetString()!;
        Operations.Add(op);
        object data = op switch
        {
            "ViewerDropsDashboard" => new { currentUser = new { dropCampaigns = new[] { new { id = "standard-control", name = "Ordinary drop", status = "ACTIVE", game = new { id = "1", displayName = "Control" } } } },
                rewardCampaignsAvailableToUser = new[] { new { id = campaignId, name = "GTAVI Launch Celebration", status = "UNKNOWN", game = (object?)null, unlockRequirements = new { minuteWatchedGoal = 60 } } } },
            "RewardCodeModal" => new { currentUser = new { inventory = new { rewardValue = new { value = CodeValue } } } },
            "Inventory" => new { currentUser = new { inventory = new { dropCampaignsInProgress = Array.Empty<object>(), earnedDropRewards = new { edges = Array.Empty<object>() } } } },
            "DirectoryPage_Game" => new { game = new { id = gameId, streams = new { edges = new[] { new { node = new { viewersCount = 100, broadcaster = new { id = "channel", login = "fixture-channel" } } } } } } },
            "VideoPlayerStreamInfoOverlayChannel" => new { user = new { id = "channel", login = "fixture-channel", stream = new { id = "live" }, broadcastSettings = new { game = new { id = WrongGame ? "wrong" : gameId } } } },
            "DropChannelCampaignsProgress" => new { channelDropCampaignsProgress = new[] { new { id = campaignId, rewardGroups = new[] { new { id = "badge-group", self = new { status = "IN_PROGRESS" },
                progressCriteria = new { requirementType = "MINUTES_WATCHED", requirements = new { minutesWatched = 60 } } } } } } },
            _ => throw new Exception("Unexpected operation: " + op)
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
    }
}
