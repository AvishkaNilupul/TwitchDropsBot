using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitchDropsBot.Core.Platform.Twitch.Utils;

public sealed record RewardCodeReceipt(string AccountId, string Login, string CampaignId,
    string CampaignName, string RewardId, string RewardName, string Code, DateTime? ExpiresAt,
    DateTime SavedAt);

// One encrypted, immutable receipt per account + earned reward. Never log this object.
public sealed class RewardCodeStore
{
    private readonly string directory;
    private readonly byte[] key;

    public static RewardCodeStore FromKeyFile(string directory, string keyFile)
    {
        if (string.IsNullOrWhiteSpace(keyFile))
            throw new InvalidOperationException("Set TWITCH_REWARD_CODE_KEY or a persistent RewardCodeKeyFile.");
        var path = Path.GetFullPath(keyFile);
        var parent = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(parent);
        else Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(temporary, options))
                {
                    var bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new RewardCodeStore(directory, File.ReadAllText(path).Trim());
    }

    public RewardCodeStore(string directory, string base64Key)
    {
        this.directory = Path.GetFullPath(directory);
        key = Convert.FromBase64String(base64Key);
        if (key.Length != 32) throw new ArgumentException("Reward code key must contain 32 bytes.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(this.directory);
        else Directory.CreateDirectory(this.directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string ReceiptPath(string accountId, string rewardId) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\n" + rewardId))) + ".json");

    public RewardCodeReceipt? Load(string accountId, string rewardId)
    {
        var path = ReceiptPath(accountId, rewardId);
        if (!File.Exists(path)) return null;
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid reward code receipt.");
        if (envelope.Version != 1 || envelope.AccountId != accountId || envelope.RewardId != rewardId)
            throw new InvalidDataException("Unsupported or mismatched reward code receipt.");
        var plaintext = new byte[envelope.Ciphertext.Length];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext,
            Encoding.UTF8.GetBytes(accountId + "\n" + rewardId));
        try
        {
            return JsonSerializer.Deserialize<RewardCodeReceipt>(plaintext)
                ?? throw new InvalidDataException("Invalid reward code receipt.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public IEnumerable<RewardCodeReceipt> List()
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid reward code receipt.");
            var receipt = Load(envelope.AccountId, envelope.RewardId)
                ?? throw new InvalidDataException("Missing reward code receipt.");
            yield return receipt;
        }
    }

    public void Save(RewardCodeReceipt receipt)
    {
        var existing = Load(receipt.AccountId, receipt.RewardId);
        if (existing != null)
        {
            if (existing.Code != receipt.Code) throw new InvalidDataException("Reward code receipt conflict.");
            return;
        }
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(receipt);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(key, 16);
        try
        {
            cipher.Encrypt(nonce, plaintext, ciphertext, tag,
                Encoding.UTF8.GetBytes(receipt.AccountId + "\n" + receipt.RewardId));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }

        var path = ReceiptPath(receipt.AccountId, receipt.RewardId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(output, new Envelope(1, receipt.AccountId, receipt.RewardId, nonce, ciphertext, tag));
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if (Load(receipt.AccountId, receipt.RewardId)?.Code != receipt.Code)
                    throw new InvalidDataException("Reward code receipt conflict.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Envelope(int Version, string AccountId, string RewardId, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
