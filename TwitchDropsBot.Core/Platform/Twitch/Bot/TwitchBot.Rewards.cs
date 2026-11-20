using Microsoft.Extensions.Logging;
using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Utils;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public partial class TwitchBot
{
    private RewardCodeStore? rewardCodeStore;

    private async Task CollectRewardCodesAsync(Inventory inventory)
    {
        // Validate durable storage before making any code reveal/claim request.
        rewardCodeStore ??= string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TWITCH_REWARD_CODE_KEY"))
            ? RewardCodeStore.FromKeyFile(TwitchSettings.RewardCodeDirectory, TwitchSettings.RewardCodeKeyFile)
            : new RewardCodeStore(TwitchSettings.RewardCodeDirectory,
                Environment.GetEnvironmentVariable("TWITCH_REWARD_CODE_KEY")!);

        foreach (var edge in inventory.EarnedDropRewards?.Edges ?? new List<EarnedDropRewardEdge>())
        {
            var reward = edge?.Node;
            if (reward?.Item?.DistributionType != DistributionType.CODE ||
                string.IsNullOrWhiteSpace(reward.Id) || string.IsNullOrWhiteSpace(reward.Campaign?.Id)) continue;
            if (rewardCodeStore.Load(BotUser.Id, reward.Id) != null) continue;
            if (_failedRewardCodeModals.TryGetValue(reward.Id, out var failedAt) &&
                DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(5)) continue;

            try
            {
                var result = await BotUser.TwitchRepository.RewardCodeModal(reward.Campaign.Id, reward.Id);
                if (string.IsNullOrWhiteSpace(result?.Value))
                    throw new InvalidDataException("Twitch did not return an issued code.");
                if ((!string.IsNullOrEmpty(result.RewardID) && result.RewardID != reward.Id) ||
                    (!string.IsNullOrEmpty(result.RewardCampaignID) && result.RewardCampaignID != reward.Campaign.Id))
                    throw new InvalidDataException("Twitch returned a different reward identity.");
                rewardCodeStore.Save(new RewardCodeReceipt(BotUser.Id, BotUser.Login, reward.Campaign.Id,
                    reward.Campaign.Name, reward.Id, reward.Item.Name, result.Value, result.ExpiresAt, DateTime.UtcNow));
                _failedRewardCodeModals.Remove(reward.Id);
                Logger.LogInformation("Saved an encrypted issued-code receipt for reward {RewardId}.", reward.Id);
            }
            catch (Exception)
            {
                // API exceptions may embed the response, including the key. Do not log them.
                _failedRewardCodeModals[reward.Id] = DateTime.UtcNow;
                Logger.LogWarning("Could not collect code reward {RewardId}; retrying after five minutes.", reward.Id);
            }
        }
    }

    private async Task<bool> TryWatchRewardCampaignAsync(IEnumerable<RewardCampaign> campaigns)
    {
        foreach (var campaign in campaigns)
        {
            var target = TwitchSettings.RewardCampaignTargets?
                .FirstOrDefault(t => t.IsValid && t.CampaignId == campaign.Id);
            var now = DateTime.UtcNow;
            if (target == null || campaign.Game == null ||
                target.WindowStart > now || target.WindowEnd <= now ||
                campaign.GetEndDate() <= now || (campaign.StartsAt ?? campaign.StartAt) > now ||
                campaign.Status is "EXPIRED" or "ENDED" or "UPCOMING" or "INACTIVE" or "DISABLED" or "PAUSED" ||
                TwitchSettings.AvoidCampaign.Contains(campaign.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (BotUser.OnlyFavouriteGames && !campaign.Game.IsFavorite) continue;
            // Explicit category binding must agree with Twitch when Twitch supplies a game.
            if (campaign.Game.Id != target.GameId) continue;

            var directory = await BotUser.TwitchRepository.FetchDirectoryPageGameAsync(target.GameSlug, false);
            if (directory?.Id != target.GameId) continue;
            var allowed = campaign.Allow?.Channels;
            var broadcaster = directory.Streams?.Edges?
                .OrderByDescending(edge => edge?.Node?.ViewersCount ?? 0)
                .Select(edge => edge?.Node?.Broadcaster)
                .FirstOrDefault(b => b != null && (allowed is not { Count: > 0 } ||
                    allowed.Any(c => string.Equals(c.Name, b.Login, StringComparison.OrdinalIgnoreCase))));
            if (broadcaster == null || string.IsNullOrWhiteSpace(broadcaster.Login)) continue;
            var live = await BotUser.TwitchRepository.FetchStreamInformationAsync(broadcaster.Login);
            if (live?.Stream == null || live.BroadcastSettings?.Game?.Id != target.GameId) continue;
            // Campaign-level channel restrictions override category-wide discovery.
            if (campaign.Allow?.Channels is { Count: > 0 } channels &&
                !channels.Any(c => string.Equals(c.Name, live.Login, StringComparison.OrdinalIgnoreCase))) continue;

            BotUser.CurrentCampaign = campaign;
            BotUser.CurrentBroadcaster = live;
            BotUser.CurrentTimeBasedDrop = null;
            BotUser.CurrentDropCurrentSession = null;
            BotUser.CurrentMinutesWatched = null;
            BotUser.RequiredMinutesWatched = null;
            BotUser.Status = BotStatus.Watching;
            Logger.LogInformation("Watching reward campaign {CampaignName} on {Channel}; game-code issuance is separate from badge progress.",
                campaign.Name, live.Login);
            var token = BotUser.CancellationTokenSource?.Token ?? CancellationToken.None;
            var until = now.AddMinutes(target.SessionMinutes);
            if (campaign.GetEndDate() < until) until = campaign.GetEndDate();
            if (target.WindowEnd.HasValue && target.WindowEnd.Value < until) until = target.WindowEnd.Value;
            try
            {
                while (DateTime.UtcNow < until)
                {
                    token.ThrowIfCancellationRequested();
                    await BotUser.WatchManager.WatchStreamAsync(live, campaign.Game);
                    var progress = await FetchCampaignProgress(live, campaign);
                    var timedGroup = progress?.RewardGroups?.FirstOrDefault(group =>
                        group?.Self?.CurrentMinutesWatched != null &&
                        group.ProgressCriteria?.Requirements?.MinutesWatched > 0 &&
                        (group.ProgressCriteria.Requirements.Subs ?? 0) == 0 &&
                        (group.ProgressCriteria.Requirements.TurboSubs ?? 0) == 0);
                    BotUser.CurrentMinutesWatched = timedGroup?.Self?.CurrentMinutesWatched;
                    BotUser.RequiredMinutesWatched = timedGroup?.ProgressCriteria?.Requirements?.MinutesWatched;
                    if (timedGroup == null)
                        Logger.LogInformation("Reward watch active; Twitch has not reported a timed progress counter.");
                    else
                        Logger.LogInformation("Reward watch progress reported by Twitch: {Current}/{Required} minutes (not a game-code count).",
                            BotUser.CurrentMinutesWatched, BotUser.RequiredMinutesWatched);
                    if (TwitchSettings.CollectRewardCodes)
                    {
                        var inventory = await BotUser.TwitchRepository.FetchInventoryDropsAsync();
                        if (inventory != null)
                        {
                            BotUser.Inventory = inventory;
                            await CollectRewardCodesAsync(inventory);
                        }
                    }
                    var delay = until - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay < TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1), token);
                }
            }
            finally { BotUser.WatchManager.Close(); }
            // Never add this to finishedCampaigns: a badge goal is not giveaway completion.
            return true;
        }
        return false;
    }
}
