namespace TwitchDropsBot.Core.Platform.Twitch.Settings;

public sealed class RewardCampaignTarget
{
    public string CampaignId { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string GameSlug { get; set; } = "";
    public int SessionMinutes { get; set; } = 10;
    public DateTime? WindowStart { get; set; }
    public DateTime? WindowEnd { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(CampaignId) &&
        !string.IsNullOrWhiteSpace(GameId) && !string.IsNullOrWhiteSpace(GameName) &&
        !string.IsNullOrWhiteSpace(GameSlug) && SessionMinutes is >= 1 and <= 60;
}
