namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>How a <c>LeadScoringRule</c>'s MatchValue is interpreted. Every type here is evaluable in
/// C# against state the platform already holds - none of them require asking the model anything, which
/// is what keeps a lead's score reproducible from its stored state alone.</summary>
public enum LeadScoringRuleType
{
    /// <summary>MatchValue is a <see cref="CustomerIntent"/> name. Fires when the turn's detected
    /// intent equals it.</summary>
    IntentMatch = 0,

    /// <summary>MatchValue is a QualificationField.FieldKey. Fires once the lead has any accepted
    /// value for that field.</summary>
    FieldPresent = 1,

    /// <summary>MatchValue is "field_key=value". Fires when that field's normalized value (falling
    /// back to the raw value) equals the right-hand side, case-insensitively.</summary>
    FieldValueMatch = 2,

    /// <summary>MatchValue is a day count. Fires when the lead's purchase-timeline field normalizes to
    /// a date within that many days of now. A timeline that did not normalize to a date never fires
    /// this - guessing "soon" into a day count is exactly the invented precision this avoids.</summary>
    TimelineWithinDays = 3,

    /// <summary>MatchValue is a keyword. Fires when the inbound message contains it as a whole word,
    /// case-insensitively.</summary>
    MessageKeyword = 4,

    /// <summary>Fires when the model reported buying intent for this turn and code did not override
    /// it. MatchValue is ignored.</summary>
    BuyingIntentDetected = 5
}
