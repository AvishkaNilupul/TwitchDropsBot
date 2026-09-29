namespace TwitchDropsBot.Core.Platform.Twitch.Models;

public enum DistributionType
{
    DIRECT_ENTITLEMENT,
    BADGE,
    EMOTE,
    CODE,
    // Any value Twitch sends that this build does not know (see TwitchJson).
    UNKNOWN,
}