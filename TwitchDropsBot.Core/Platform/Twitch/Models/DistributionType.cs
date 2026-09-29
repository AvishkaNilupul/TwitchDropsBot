namespace TwitchDropsBot.Core.Platform.Twitch.Models;

public enum DistributionType
{
    DIRECT_ENTITLEMENT,
    BADGE,
    EMOTE,
    CODE,
    // Seen 2026-09-29 on earnedDropRewards items; handled like a direct entitlement.
    POOL,
    // Any value Twitch sends that this build does not know (see TwitchJson).
    UNKNOWN,
}