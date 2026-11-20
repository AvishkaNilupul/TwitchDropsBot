# Reward campaign worker contract

Opt-in campaign targets use the existing Twitch worker and watch manager. A target must name the exact campaign ID and a game ID/name/slug. A missing campaign game may be supplied only by that explicit target. Standard Drops retain their existing selection and claim behavior.

Reward campaigns bypass DropCampaignDetails and TimeBasedDrop lookup. They select a live stream in the configured category, read channel reward-group progress where available, and watch for a bounded session before refreshing discovery. Missing progress stays unknown. A reported badge goal is not evidence that a surprise game key was issued; the worker continues watching on later passes while the campaign remains active.

Code collection is separately opt-in. It only fetches CODE entries Twitch actually returned in that account's earned inventory. Save the returned value durably before logging success. Ordinary drop claims stay controlled by ClaimDrops. Codes must never be printed or posted to a webhook by the independent collector. The encrypted output directory and encryption key must survive container replacement.

GTA VI's known launch campaign is a badge event. The separate surprise giveaway has no guaranteed minute goal. A local replay verifies worker behavior, not live Twitch watch credit or issuance.

## GTA VI configuration

Merge these fields into an existing **dedicated GTA VI worker** config. All existing TwitchUsers credentials stay in the existing private config. Set each viewer's FavouriteGames to `["Grand Theft Auto VI"]` if OnlyFavouriteGames is enabled. Use the same account ownership and single-home allocation rules as ordinary no-claim workers.

```json
{
  "TwitchSettings": {
    "ClaimDrops": false,
    "CollectRewardCodes": true,
    "RewardCodeDirectory": "/app/Configuration/reward-codes",
    "RewardCodeKeyFile": "/app/Configuration/reward-code.key",
    "RewardCampaignTargets": [
      {
        "CampaignId": "b7669f9e-c69c-4a9b-a3b2-36266c1bdfc0",
        "GameId": "1867279146",
        "GameName": "Grand Theft Auto VI",
        "GameSlug": "grand-theft-auto-vi",
        "SessionMinutes": 10
      }
    ]
  }
}
```

The configured key file is generated atomically on first use with private permissions, then reused on restart. Keep `/app/Configuration` on its existing persistent host mount, including the key file and receipt directory. An existing invalid key stops collection before requesting a code value. Alternatively, supply a persistent 32-byte base64 key as `TWITCH_REWARD_CODE_KEY`; this overrides the configured key file. Retain that key separately from the output. The independent collector handles all earned CODE items on that worker, including already-claimed items missing a local receipt. It does not buy subscriptions, redeem game keys, create accounts, or publish marketplace listings.

The worker gives explicitly selected reward campaigns priority over normal Drops, watches for a bounded session, then refreshes the catalog. When there is no eligible live stream it falls back to the existing selection flow. A missing timer is displayed as unknown; neither a timer nor a badge is counted as a game code. Channel allowlists are respected; category directory discovery can only choose channels returned in Twitch's current directory page. A vanished or ended campaign stops being selected on the next refresh. Keep this worker running only for the intended campaign window; surprise awards are not guaranteed by watching.

Inspect stored receipts with `TWITCH_REWARD_CODE_KEY_FILE=/app/Configuration/reward-code.key` (or the equivalent host path), or set `TWITCH_REWARD_CODE_KEY` to the same key:

```sh
TwitchDropsBot.Console reward-codes list /app/Configuration/reward-codes
TwitchDropsBot.Console reward-codes reveal /app/Configuration/reward-codes ACCOUNT_ID EARNED_REWARD_ID
```

`list` returns metadata only. `reveal` prints exactly one code for explicit operator retrieval. Receipt filenames and encrypted envelopes identify the Twitch account ID and earned reward ID; usernames, reward details and code values are encrypted with AES-256-GCM. Writes use a private temporary file, flush to disk, then atomically create the final receipt. Concurrent identical writes converge; conflicting code values cannot replace the saved receipt. Failed/empty code responses are retried after five minutes. Restart retries converge on the existing encrypted receipt.

The existing Accounts & codes dashboard and its separate `gta-vi-code` upload command have not been changed or deployed by this patch. This worker stores receipts on disk; automatic dashboard upload is not implemented here.

## Build and verification

```sh
dotnet build TwitchDropsBot.Console/TwitchDropsBot.Console.csproj
dotnet run --project tests/RewardCampaigns/RewardCampaigns.csproj
```

Requires the .NET 8 SDK. Tests run the actual repository, reward watch selection, independent collector and CLI using a fixture HTTP transport and synthetic codes. They do not demonstrate live Twitch watch credit or a real giveaway award. Existing standard Drop defaults remain off-target controls in the replay.

This patch is local and opt-in. Matching nodeserver integration is in `/Users/avishkanilupul/projects/nodeserver-gta-vi-reward-farm` on `codex/gta-vi-reward-fleet`: explicit reward windows supplement the wake catalog, category liveness does not require a Drops-enabled tag, and ordinary completion markers cannot finish a surprise giveaway. Its fleet config generator supplies targets and persistent key/output paths. Both builds must be deployed together and a GTA VI bot must be explicitly configured before the live farm uses this path. No live configs, pool accounts, fleet images or marketplace listings have been changed.
