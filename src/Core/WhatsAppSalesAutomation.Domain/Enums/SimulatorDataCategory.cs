namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What a <see cref="Entities.Platform.SimulatorDummyRecord"/> feeds in the simulator.</summary>
public enum SimulatorDataCategory
{
    /// <summary>A business the simulated lead discovery hands back as if it had found it on the web.</summary>
    LeadBusiness = 0,

    /// <summary>A canned reply of the simulated WhatsApp AI (keyed: Grounded, Escalation, Default).</summary>
    AiReply = 1,

    /// <summary>The words that make the simulated AI pick an intent (keyed by intent, comma separated).</summary>
    IntentKeywords = 2,

    /// <summary>The predefined answers of the simulated product-knowledge tasks (keyed).</summary>
    TaskAnswer = 3,

    /// <summary>A phone number the simulated DND check always rejects.</summary>
    DndNumber = 4,

    /// <summary>A simulator switch (keyed: UniquePerRun, FlawedSharePercent, DndDigitSumRule).</summary>
    Setting = 5
}

/// <summary>How a dummy lead business behaves when the qualification rules check it.</summary>
public enum SimulatorLeadVariant
{
    /// <summary>Phone and email are on its fetched page: it qualifies.</summary>
    Qualified = 0,

    /// <summary>Its phone appears on no fetched page: rejected when a phone is required.</summary>
    PhoneNotOnPage = 1,

    /// <summary>The page says it has permanently closed: always rejected.</summary>
    PermanentlyClosed = 2
}
