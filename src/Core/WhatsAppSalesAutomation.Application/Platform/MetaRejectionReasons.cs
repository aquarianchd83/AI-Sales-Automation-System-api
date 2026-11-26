namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>A rejection explained to the platform admin: what Meta's code means, and what to change before syncing again.</summary>
public record MetaRejectionExplanation(string Title, string Tip);

/// <summary>
/// Meta's <c>rejected_reason</c> codes for a message template, in plain words with a fix. Meta only sends the code (the detailed text is in
/// WhatsApp Manager), so the tip is the usual cause of that code for a Utility notice like ours. Also covers the statuses we treat as
/// rejected (PAUSED, DISABLED) since they block sending the same way.
/// </summary>
public static class MetaRejectionReasons
{
    private const string AfterFix = " Then press Edit, change the wording, save and press Sync - Meta reviews it again.";

    /// <summary>Meta's code, upper-cased; null for no code or Meta's "NONE".</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var upper = code.Trim().ToUpperInvariant();
        return upper == "NONE" ? null : upper;
    }

    public static MetaRejectionExplanation Explain(string? code) => Normalize(code) switch
    {
        "INVALID_FORMAT" => new(
            "Format problem",
            "Meta found the layout or placeholders invalid. Usual causes: a placeholder at the very start or end, two placeholders next to each " +
            "other, too many placeholders for the amount of text, emojis or special characters inside a placeholder, or wording that is not in " +
            "the template's language (it is registered as English, so Hindi or long Hinglish sentences can fail)." + AfterFix),
        "TAG_CONTENT_MISMATCH" => new(
            "Wording does not match category or language",
            "The template is filed as Utility in English, but Meta read the text as something else - for example sales or promotional wording, " +
            "or a different language. Keep it to the account update itself (what happened and what to do), and write it in English." + AfterFix),
        "PROMOTIONAL" or "INCORRECT_CATEGORY" => new(
            "Reads as marketing",
            "A Utility template may only inform about the account. Remove offers, discounts, \"buy now\" style lines and anything that sells; " +
            "keep the fact (plan expiring, credits low, connection lost) and the step to take." + AfterFix),
        "ABUSIVE_CONTENT" => new(
            "Content not allowed",
            "Meta's policy check flagged the wording. Remove threatening, pressuring or sensitive language and anything that could look like " +
            "spam, and keep the message short and factual." + AfterFix),
        "SCAM" => new(
            "Looks like a scam",
            "Meta thought the message could be a scam. Avoid urgent payment or \"verify now\" language, phone numbers or links that ask for " +
            "codes or money, and say clearly it is from AiSaarthi about the company's own account." + AfterFix),
        "PAUSED" => new(
            "Paused by Meta",
            "Meta paused this template because people blocked or reported it. Make the message more relevant and less frequent; Meta un-pauses " +
            "it after a while, and a Sync shows the new status."),
        "DISABLED" => new(
            "Disabled by Meta",
            "Meta disabled this template after repeated poor feedback. It can't be fixed by editing - use a new template name with improved " +
            "wording, or contact Meta support."),
        _ => new(
            "Rejected by Meta",
            "Meta gave no specific reason. Open WhatsApp Manager > Message templates to see Meta's note. Common fixes: keep the wording " +
            "factual (no sales lines), write it in English to match the template language, and don't start or end with a placeholder." + AfterFix)
    };
}
