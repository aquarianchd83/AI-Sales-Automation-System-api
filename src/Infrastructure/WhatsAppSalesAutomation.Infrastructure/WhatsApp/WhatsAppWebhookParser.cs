using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// Parses Meta's webhook JSON shape (entry[].changes[].value.{messages[],statuses[],contacts[]})
/// into the plain <see cref="InboundWhatsAppMessage"/>/<see cref="WhatsAppStatusUpdate"/> records the
/// Application layer works with. Never throws on a malformed/unrecognised payload - an empty result
/// is exactly as valid a parse outcome as "nothing to report" here, so InboundWebhookProcessor's own
/// "processedAnything" bookkeeping already handles it without a special case.
/// </summary>
public class WhatsAppWebhookParser : IWhatsAppWebhookParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<WhatsAppWebhookParser> _logger;

    public WhatsAppWebhookParser(ILogger<WhatsAppWebhookParser> logger)
    {
        _logger = logger;
    }

    public WhatsAppWebhookParseResult Parse(string rawPayload)
    {
        var messages = new List<InboundWhatsAppMessage>();
        var statuses = new List<WhatsAppStatusUpdate>();

        MetaWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MetaWebhookPayload>(rawPayload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "WhatsApp webhook payload was not valid JSON");
            return new WhatsAppWebhookParseResult(messages, statuses);
        }

        foreach (var entry in payload?.Entry ?? Enumerable.Empty<MetaEntry>())
        {
            foreach (var change in entry.Changes ?? Enumerable.Empty<MetaChange>())
            {
                var value = change.Value;
                if (value is null)
                    continue;

                var contactNameByWaId = (value.Contacts ?? new List<MetaContact>())
                    .Where(c => c.WaId is not null)
                    .ToDictionary(c => c.WaId!, c => c.Profile?.Name);

                foreach (var m in value.Messages ?? Enumerable.Empty<MetaMessage>())
                {
                    if (m.Id is null || m.From is null)
                        continue;

                    contactNameByWaId.TryGetValue(m.From, out var contactName);

                    messages.Add(new InboundWhatsAppMessage(
                        m.Id,
                        m.From,
                        contactName,
                        ParseUnixTimestamp(m.Timestamp),
                        m.Type ?? "unknown",
                        m.Text?.Body));
                }

                foreach (var s in value.Statuses ?? Enumerable.Empty<MetaStatus>())
                {
                    if (s.Id is null || s.Status is null)
                        continue;

                    statuses.Add(new WhatsAppStatusUpdate(s.Id, s.Status, ParseUnixTimestamp(s.Timestamp), BuildFailureReason(s.Errors)));
                }
            }
        }

        return new WhatsAppWebhookParseResult(messages, statuses);
    }

    /// <summary>Meta sends Unix epoch seconds as a string. Falls back to now on anything else rather
    /// than throwing - a message that fails to parse is still worth recording with a best-effort time.</summary>
    private static DateTime ParseUnixTimestamp(string? timestamp) =>
        long.TryParse(timestamp, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;

    /// <summary>Meta reports why a message failed via this array - present only on a "failed" status,
    /// e.g. code 131047 "Re-engagement message" for a template sent outside the 24-hour customer
    /// service window, or 470 for a paused template. error_data.details is the specific human-readable
    /// explanation when present; message/title are the generic fallbacks Meta always includes.</summary>
    private static string? BuildFailureReason(List<MetaStatusError>? errors)
    {
        var first = errors?.FirstOrDefault();
        if (first is null)
            return null;

        var detail = first.ErrorData?.Details ?? first.Message ?? first.Title ?? "Unknown error";
        return first.Code is { } code ? $"[{code}] {detail}" : detail;
    }

    private class MetaWebhookPayload
    {
        [JsonPropertyName("entry")]
        public List<MetaEntry>? Entry { get; set; }
    }

    private class MetaEntry
    {
        [JsonPropertyName("changes")]
        public List<MetaChange>? Changes { get; set; }
    }

    private class MetaChange
    {
        [JsonPropertyName("value")]
        public MetaValue? Value { get; set; }
    }

    private class MetaValue
    {
        [JsonPropertyName("contacts")]
        public List<MetaContact>? Contacts { get; set; }

        [JsonPropertyName("messages")]
        public List<MetaMessage>? Messages { get; set; }

        [JsonPropertyName("statuses")]
        public List<MetaStatus>? Statuses { get; set; }
    }

    private class MetaContact
    {
        [JsonPropertyName("wa_id")]
        public string? WaId { get; set; }

        [JsonPropertyName("profile")]
        public MetaProfile? Profile { get; set; }
    }

    private class MetaProfile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class MetaMessage
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public MetaText? Text { get; set; }
    }

    private class MetaText
    {
        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private class MetaStatus
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("errors")]
        public List<MetaStatusError>? Errors { get; set; }
    }

    private class MetaStatusError
    {
        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("error_data")]
        public MetaErrorData? ErrorData { get; set; }
    }

    private class MetaErrorData
    {
        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }
}
