using System.Globalization;
using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>What one bridge delivery means to us. <see cref="Canonical"/> is the only thing ever stored: a compact payload of our own that has
/// none of the bridge's secrets (every delivery carries the instance token in <c>apikey</c>) and none of the encryption blobs a message carries.</summary>
/// <param name="ProvidedToken">The instance token the delivery claims, for the caller to check - never stored.</param>
public record NormalizedBridgeEvent(
    NormalizedBridgeEvent.EventKind Kind, string? ProvidedToken, string? ConnectionState = null, string? ConnectedPhone = null, string? Canonical = null)
{
    public enum EventKind { Ignored, Connection, Messages }
}

/// <summary>
/// Turns an Evolution API v2.3.7 webhook body into <see cref="NormalizedBridgeEvent"/>, and parses our canonical form back into the same
/// plain records Meta deliveries become. Every field name and rule here was observed on a connected session (see section 9.1 of the analysis).
/// Group messages, messages we sent ourselves, and read-state of messages we received are dropped here, before anything is stored.
/// </summary>
public static class EvolutionWebhookNormalizer
{
    private const string CanonicalMarker = "bridge";

    public static NormalizedBridgeEvent Normalize(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var token = Str(root, "apikey");
            var data = Obj(root, "data");

            // Subscribed as UPPER_SNAKE, delivered lowercase.dotted.
            switch ((Str(root, "event") ?? string.Empty).ToLowerInvariant().Replace('_', '.'))
            {
                case "connection.update":
                    return new NormalizedBridgeEvent(NormalizedBridgeEvent.EventKind.Connection, token, Str(data, "state"), PhoneFromJid(Str(data, "wuid")));

                case "messages.upsert":
                    return Messages(token, UpsertToMessage(data), null);

                case "messages.update":
                    return Messages(token, null, StatusFromUpdate(data));

                default:
                    // qrcode.updated (never persisted), send.message (our own send is already recorded), anything else.
                    return new NormalizedBridgeEvent(NormalizedBridgeEvent.EventKind.Ignored, token);
            }
        }
        catch (JsonException)
        {
            return new NormalizedBridgeEvent(NormalizedBridgeEvent.EventKind.Ignored, null);
        }
    }

    /// <summary>True when <paramref name="rawPayload"/> is our canonical form (and parsed); false means it is not ours and the caller should try Meta's shape.</summary>
    public static bool TryParseCanonical(string rawPayload, out WhatsAppWebhookParseResult result)
    {
        result = default!;
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;
            if (Str(root, CanonicalMarker) != "evolution")
                return false;

            var messages = new List<InboundWhatsAppMessage>();
            var statuses = new List<WhatsAppStatusUpdate>();

            if (root.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    messages.Add(new InboundWhatsAppMessage(
                        Str(m, "id") ?? string.Empty, Str(m, "from") ?? string.Empty, Str(m, "name"),
                        FromUnix(m, "ts"), Str(m, "type") ?? "text", Str(m, "text")));

            if (root.TryGetProperty("statuses", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray())
                    statuses.Add(new WhatsAppStatusUpdate(Str(s, "id") ?? string.Empty, Str(s, "status") ?? string.Empty, FromUnix(s, "ts")));

            result = new WhatsAppWebhookParseResult(
                messages.Where(m => m.WhatsAppMessageId.Length > 0 && m.FromPhone.Length > 0).ToList(),
                statuses.Where(s => s.WhatsAppMessageId.Length > 0 && s.Status.Length > 0).ToList(),
                Provider: WhatsAppProviders.QrBridge);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static NormalizedBridgeEvent Messages(string? token, object? message, object? status)
    {
        if (message is null && status is null)
            return new NormalizedBridgeEvent(NormalizedBridgeEvent.EventKind.Ignored, token);

        var canonical = JsonSerializer.Serialize(new
        {
            bridge = "evolution",
            messages = message is null ? Array.Empty<object>() : new[] { message },
            statuses = status is null ? Array.Empty<object>() : new[] { status },
        });
        return new NormalizedBridgeEvent(NormalizedBridgeEvent.EventKind.Messages, token, Canonical: canonical);
    }

    private static object? UpsertToMessage(JsonElement data)
    {
        var key = Obj(data, "key");
        // Messages typed on the linked phone itself (fromMe) and group chats must never reach the AI or create customers.
        if (key.ValueKind != JsonValueKind.Object || Bool(key, "fromMe"))
            return null;

        var id = Str(key, "id");
        var phone = PhoneFromJid(Str(key, "remoteJid")) ?? PhoneFromJid(Str(key, "remoteJidAlt"));
        if (string.IsNullOrEmpty(id) || phone is null)
            return null;

        var content = Obj(data, "message");
        var text = Str(content, "conversation") ?? Str(Obj(content, "extendedTextMessage"), "text");
        var messageType = Str(data, "messageType");

        return new
        {
            id,
            from = phone,
            name = Str(data, "pushName"),
            ts = Number(data, "messageTimestamp"),
            // Anything but plain text is recorded without a body, like the Meta path.
            type = text is not null ? "text" : messageType ?? "unsupported",
            text,
        };
    }

    private static object? StatusFromUpdate(JsonElement data)
    {
        // fromMe:false updates are the read-state of messages we RECEIVED - not a delivery status of ours.
        if (data.ValueKind != JsonValueKind.Object || !Bool(data, "fromMe"))
            return null;

        var id = Str(data, "keyId");
        var status = Str(data, "status")?.ToUpperInvariant() switch
        {
            "SERVER_ACK" => "sent",
            "DELIVERY_ACK" => "delivered",
            "READ" => "read",
            _ => null
        };

        return string.IsNullOrEmpty(id) || status is null ? null : new { id, status, ts = (long?)null };
    }

    /// <summary>Digits of a person's JID. Groups, broadcasts and hidden (@lid) ids carry no usable phone number and return null.</summary>
    private static string? PhoneFromJid(string? jid)
    {
        if (string.IsNullOrEmpty(jid))
            return null;

        var at = jid.IndexOf('@');
        if (at <= 0 || !jid[(at + 1)..].Equals("s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            return null;

        var local = jid[..at];
        // A device suffix ("1234:5@s.whatsapp.net") is not part of the number.
        local = local.Split(':')[0];
        return local.All(char.IsDigit) && local.Length > 0 ? local : null;
    }

    private static DateTime FromUnix(JsonElement element, string name)
    {
        var seconds = Number(element, name);
        return seconds is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.UtcNow;
    }

    private static long? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
