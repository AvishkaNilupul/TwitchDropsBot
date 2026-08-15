using TwitchDropsBot.Core.Platform.Shared.WatchManager;

namespace TwitchDropsBot.Core.Platform.Twitch.Settings;

public class TwitchSettings
{
    public List<TwitchUserSettings> TwitchUsers { get; set; } = new List<TwitchUserSettings>();
    public List<string> AvoidCampaign { get; set; } = new List<string>();
    public bool OnlyFavouriteGames { get; set; } = false;
    public bool MinimizeInTray { get; set; } = true;
    public bool ForceTryWithTags { get; set; } = false;
    public bool OnlyConnectedAccounts { get; set; } = false;
    // When false, the bot watches drops to completion but does NOT press "Claim".
    // The drop is left as earned-but-unclaimed in the account's inventory so the buyer
    // can connect their own game account and claim it themselves. Default true = upstream behaviour.
    public bool ClaimDrops { get; set; } = true;
    public string WatchManager { get; set; } = WatchManagerType.WatchRequest;
}