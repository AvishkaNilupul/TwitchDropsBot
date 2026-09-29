using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphQL.Client.Serializer.SystemTextJson;

namespace TwitchDropsBot.Core.Platform.Twitch.Utils;

// Twitch changes the shape of its GQL responses without notice, and with the
// stock serializer ONE unexpected value anywhere in a response throws for the
// whole query. That turns a cosmetic change into a farming outage:
//
//   * 2026-09: DropChannelCampaignsProgress started sending
//     rewardGroups[].progressCriteria.channels as a list where the model said
//     string. Every progress check threw, the bot dropped the campaign and
//     logged "No broadcaster or campaign left" — a whole auto-farm task
//     (Plants on Fire, 34 accounts) never watched a minute.
//   * 2026-09: Inventory threw on an earned reward whose
//     item.distributionType was a value the DistributionType enum did not
//     have, so that account could not read its inventory at all.
//
// These converters make fields we do not depend on degrade instead of throw:
// a string field that receives a number/list/object keeps its raw JSON text,
// and an enum that receives a value it does not know maps to its UNKNOWN
// member (or its default when it has none). Each anomaly is recorded once so
// the repository can log it and the operator still sees that Twitch changed.
public static class TwitchJson
{
    private const int MaxAnomalies = 200;
    private static readonly ConcurrentDictionary<string, byte> SeenAnomalies = new();
    private static readonly ConcurrentQueue<string> PendingAnomalies = new();

    // The serializer every Twitch GraphQLHttpClient must use.
    public static SystemTextJsonSerializer CreateGraphQLSerializer() =>
        new SystemTextJsonSerializer(options => AddTolerantConverters(options));

    // Plain System.Text.Json options with the same tolerance, for responses
    // parsed outside the GraphQL client.
    public static JsonSerializerOptions CreateOptions(bool propertyNameCaseInsensitive = true)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = propertyNameCaseInsensitive
        };
        AddTolerantConverters(options);
        return options;
    }

    // Converters earlier in the list win, so these go in front of the stock
    // JsonStringEnumConverter the GraphQL client registers.
    public static JsonSerializerOptions AddTolerantConverters(JsonSerializerOptions options)
    {
        options.Converters.Insert(0, new TolerantEnumConverterFactory());
        options.Converters.Insert(0, new LenientStringConverter());
        return options;
    }

    internal static void RecordAnomaly(string message)
    {
        if (SeenAnomalies.Count >= MaxAnomalies) return;
        if (SeenAnomalies.TryAdd(message, 0)) PendingAnomalies.Enqueue(message);
    }

    // Anomalies seen since the last call, each reported once per process.
    public static List<string> DrainNewAnomalies()
    {
        var list = new List<string>();
        while (PendingAnomalies.TryDequeue(out var message)) list.Add(message);
        return list;
    }

    internal static string Snippet(string raw, int max = 80)
    {
        var oneLine = raw.Replace('\n', ' ').Replace('\r', ' ');
        return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
    }
}

// A string property that receives a non-string value keeps the value's raw
// JSON text instead of failing the whole response.
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    var raw = doc.RootElement.GetRawText();
                    if (doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        TwitchJson.RecordAnomaly(
                            $"a string field received a JSON {doc.RootElement.ValueKind.ToString().ToLowerInvariant()} " +
                            $"({TwitchJson.Snippet(raw)}) — kept as text");
                    }
                    return raw;
                }
            default:
                throw new JsonException($"Unexpected JSON token {reader.TokenType} for a string value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    // Dictionary<string, …> keys go through these once a string converter is
    // registered; the defaults throw for custom converters.
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => reader.GetString() ?? string.Empty;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value);
}

public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

// Reads Twitch's CONSTANT_CASE strings (or numbers) into an enum; a value the
// enum does not define becomes its UNKNOWN member, else its default. Writes
// CONSTANT_CASE, like the stock converter the GraphQL client registers.
public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByKey = BuildMap();
    private static readonly T Fallback = Enum.TryParse<T>("UNKNOWN", out var unknown) ? unknown : default;

    private static string Key(string value) =>
        value.Replace("_", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    private static Dictionary<string, T> BuildMap()
    {
        var map = new Dictionary<string, T>();
        foreach (var value in Enum.GetValues<T>())
        {
            map.TryAdd(Key(value.ToString()), value);
        }
        return map;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var text = reader.GetString() ?? string.Empty;
                if (ByKey.TryGetValue(Key(text), out var value)) return value;
                TwitchJson.RecordAnomaly($"{typeof(T).Name} received unknown value \"{TwitchJson.Snippet(text, 40)}\" — read as {Fallback}");
                return Fallback;
            }
            case JsonTokenType.Number when reader.TryGetInt64(out var number):
            {
                var value = (T)Enum.ToObject(typeof(T), number);
                if (Enum.IsDefined(value)) return value;
                TwitchJson.RecordAnomaly($"{typeof(T).Name} received unknown number {number} — read as {Fallback}");
                return Fallback;
            }
            default:
                // Arrays/objects/other: consume the whole value, keep going.
                TwitchJson.RecordAnomaly($"{typeof(T).Name} received a JSON {reader.TokenType} — read as {Fallback}");
                reader.Skip();
                return Fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToConstantCase(value.ToString()));

    private static string ToConstantCase(string name)
    {
        if (name.ToUpperInvariant() == name) return name;
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && name[i - 1] != '_' && !char.IsUpper(name[i - 1])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
