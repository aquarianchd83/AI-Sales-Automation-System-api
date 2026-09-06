using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.LogViewer;

/// <summary>Bindable from query string, e.g.
/// <c>?date=2026-09-03&amp;level=Warning&amp;module=CampaignSendService&amp;method=AttemptSendAsync&amp;search=template&amp;page=2</c>.
/// <paramref name="Date"/> defaults to today (IST) when omitted; <paramref name="Level"/> matches
/// case-insensitively against either the full name ("Warning") or Serilog's 3-letter code ("WRN");
/// <paramref name="Module"/> and <paramref name="Method"/> match a substring of the logging class'
/// namespace+name and the calling method, respectively (e.g. "ConversationService" matches the fully
/// qualified "WhatsAppSalesAutomation.Application.Conversations.ConversationService"); <c>Search</c>
/// (inherited) matches a substring within the message.</summary>
public record LogQueryRequest : PagedRequest
{
    public DateOnly? Date { get; init; }

    public string? Level { get; init; }

    public string? Module { get; init; }

    public string? Method { get; init; }
}
