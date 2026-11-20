using System.Text.Json;
using TwitchDropsBot.Core.Platform.Twitch.Utils;

namespace TwitchDropsBot.Console.Utils;

internal static class RewardCodesCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 2 && args[1] == "--help")
        {
            System.Console.WriteLine("reward-codes list <directory>");
            System.Console.WriteLine("reward-codes export <directory> (private code-sync transport)");
            System.Console.WriteLine("reward-codes reveal <directory> <account-id> <earned-reward-id>");
            System.Console.WriteLine("Set TWITCH_REWARD_CODE_KEY to the worker's persistent 32-byte base64 key.");
            System.Console.WriteLine("Alternatively, set TWITCH_REWARD_CODE_KEY_FILE to the existing worker key file.");
            return 0;
        }
        if (!((args.Length == 3 && args[1] is "list" or "export") || (args.Length == 5 && args[1] == "reveal")))
        {
            System.Console.Error.WriteLine("Use reward-codes --help.");
            return 1;
        }
        try
        {
            if (!Directory.Exists(args[2])) throw new DirectoryNotFoundException();
            var key = Environment.GetEnvironmentVariable("TWITCH_REWARD_CODE_KEY");
            if (string.IsNullOrEmpty(key)) key = File.ReadAllText(
                Environment.GetEnvironmentVariable("TWITCH_REWARD_CODE_KEY_FILE") ?? throw new InvalidOperationException()).Trim();
            var store = new RewardCodeStore(args[2], key);
            if (args[1] == "export")
                System.Console.WriteLine(JsonSerializer.Serialize(store.List()));
            else if (args[1] == "list")
                System.Console.WriteLine(JsonSerializer.Serialize(store.List().Select(r => new
                {
                    r.AccountId, r.Login, r.CampaignId, r.CampaignName, r.RewardId, r.RewardName, r.ExpiresAt, r.SavedAt
                })));
            else
            {
                var receipt = store.Load(args[3], args[4]);
                if (receipt == null)
                {
                    System.Console.Error.WriteLine("Receipt not found.");
                    return 1;
                }
                System.Console.WriteLine(receipt.Code);
            }
            return 0;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine("Could not read receipts. Check the directory, encryption key and receipt integrity.");
            return 1;
        }
    }
}
