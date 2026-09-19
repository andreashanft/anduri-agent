using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Anduri.Agent.Sensors;

namespace Anduri.Agent.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(PairRequestMessage))]
[JsonSerializable(typeof(PairConfirmMessage))]
[JsonSerializable(typeof(SetIntervalMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(CatalogMessage))]
[JsonSerializable(typeof(HistoryMessage))]
[JsonSerializable(typeof(SnapshotMessage))]
[JsonSerializable(typeof(IntervalMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(PairChallengeMessage))]
[JsonSerializable(typeof(PairAcceptedMessage))]
[JsonSerializable(typeof(PairRejectedMessage))]
[JsonSerializable(typeof(SensorDescriptor))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

/// <summary>Thrown for frames that aren't a JSON object with a string <c>type</c>, or whose fields have the wrong types.</summary>
public sealed class ProtocolFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Encodes and decodes protocol messages with the source-generated context.</summary>
public static class ProtocolJson
{
    // Options passed to a context constructor replace the attribute's options, so they are repeated here.
    // Frames are never embedded in HTML, so "°C" and "·" can go out as plain UTF-8 instead of \u escapes.
    private static readonly ProtocolJsonContext Context = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static byte[] SerializeToUtf8(ProtocolMessage message) => message switch
    {
        HelloMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.HelloMessage),
        PairRequestMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.PairRequestMessage),
        PairConfirmMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.PairConfirmMessage),
        SetIntervalMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.SetIntervalMessage),
        WelcomeMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.WelcomeMessage),
        CatalogMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.CatalogMessage),
        HistoryMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.HistoryMessage),
        SnapshotMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.SnapshotMessage),
        IntervalMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.IntervalMessage),
        ErrorMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.ErrorMessage),
        PairChallengeMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.PairChallengeMessage),
        PairAcceptedMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.PairAcceptedMessage),
        PairRejectedMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Context.PairRejectedMessage),
        _ => throw new ArgumentException($"Unsupported message type {message.GetType().Name}.", nameof(message)),
    };

    public static string Serialize(ProtocolMessage message) => System.Text.Encoding.UTF8.GetString(SerializeToUtf8(message));

    /// <summary>
    /// Decodes one frame. Returns <c>null</c> for message types this agent doesn't know, which the protocol says to ignore.
    /// </summary>
    /// <exception cref="ProtocolFormatException">The frame is malformed.</exception>
    public static ProtocolMessage? Deserialize(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 32 });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new ProtocolFormatException("The message isn't valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtocolFormatException("The message isn't a JSON object.");
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                throw new ProtocolFormatException("The message has no string \"type\" field.");

            try
            {
                return typeElement.GetString() switch
                {
                    MessageTypes.Hello => Read(root, Context.HelloMessage),
                    MessageTypes.PairRequest => Read(root, Context.PairRequestMessage),
                    MessageTypes.PairConfirm => Read(root, Context.PairConfirmMessage),
                    MessageTypes.SetInterval => Read(root, Context.SetIntervalMessage),
                    MessageTypes.Welcome => Read(root, Context.WelcomeMessage),
                    MessageTypes.Catalog => Read(root, Context.CatalogMessage),
                    MessageTypes.History => Read(root, Context.HistoryMessage),
                    MessageTypes.Snapshot => Read(root, Context.SnapshotMessage),
                    MessageTypes.Interval => Read(root, Context.IntervalMessage),
                    MessageTypes.Error => Read(root, Context.ErrorMessage),
                    MessageTypes.PairChallenge => Read(root, Context.PairChallengeMessage),
                    MessageTypes.PairAccepted => Read(root, Context.PairAcceptedMessage),
                    MessageTypes.PairRejected => Read(root, Context.PairRejectedMessage),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
            {
                throw new ProtocolFormatException($"The \"{typeElement.GetString()}\" message has invalid fields.", ex);
            }
        }
    }

    public static ProtocolMessage? Deserialize(string json) => Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

    private static T Read<T>(JsonElement element, JsonTypeInfo<T> typeInfo) where T : ProtocolMessage =>
        element.Deserialize(typeInfo) ?? throw new ProtocolFormatException("The message is null.");
}
