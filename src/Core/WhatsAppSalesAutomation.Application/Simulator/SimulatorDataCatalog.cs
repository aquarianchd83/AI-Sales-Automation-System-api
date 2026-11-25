using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Simulator;

/// <summary>
/// The keys the simulator reads and the starter dummy data. The starter rows are inserted once by the AddSimulatorDummyData migration
/// and again only when the admin presses "Restore defaults"; after that the database rows are what the simulator uses. A keyed row
/// (AiReply, IntentKeywords, TaskAnswer, Setting) that has been deleted or switched off falls back to the value here, so the simulator
/// keeps working; a list (lead businesses, DND numbers) that has been emptied stays empty.
/// </summary>
public static class SimulatorDataCatalog
{
    public static class Replies
    {
        /// <summary>Used when the knowledge base found something. {answer} is the top passage.</summary>
        public const string Grounded = "Grounded";
        public const string Escalation = "Escalation";
        public const string Default = "Default";
    }

    public static class Intents
    {
        public const string Complaint = "Complaint";
        public const string HumanRequest = "HumanRequest";
        public const string CannotProceed = "CannotProceed";
        public const string PurchaseIntent = "PurchaseIntent";
        public const string DemoRequest = "DemoRequest";
        public const string Negotiation = "Negotiation";
        public const string Support = "Support";
        public const string PriceEnquiry = "PriceEnquiry";
        public const string Information = "Information";
    }

    public static class Answers
    {
        /// <summary>A plain-text task reply. {task} is the task name.</summary>
        public const string TextReply = "TextReply";
        public const string Answer = "Answer";
        public const string MissingEvidenceNote = "MissingEvidenceNote";
        public const string ConflictAnswer = "ConflictAnswer";
        public const string ConflictBrochurePrice = "ConflictBrochurePrice";
        public const string ConflictWebsitePrice = "ConflictWebsitePrice";
        public const string MalformedReply = "MalformedReply";
    }

    public static class Settings
    {
        /// <summary>true: each lead run adds a run tag to names and websites and new last digits to phones, so a rerun is not all duplicates.</summary>
        public const string UniquePerRun = "UniquePerRun";

        /// <summary>How many of the businesses a lead run hands back are the flawed ones (0-90).</summary>
        public const string FlawedSharePercent = "FlawedSharePercent";

        /// <summary>true: besides the listed DND numbers, any number whose digits add up to a multiple of four is DND (about one in four).</summary>
        public const string DndDigitSumRule = "DndDigitSumRule";
    }

    /// <summary>The fixed keys per keyed category, with their built-in value and a short help line for the admin page.</summary>
    public static readonly IReadOnlyList<SimulatorKeyDefinition> Keys = new SimulatorKeyDefinition[]
    {
        new(SimulatorDataCategory.AiReply, Replies.Grounded, "Here's what I found: {answer}", "Reply when the knowledge base has an answer. {answer} = the top passage."),
        new(SimulatorDataCategory.AiReply, Replies.Escalation, "I understand, and I want to make sure this gets handled properly - connecting you with a team member who can help.", "Reply for complaints, human requests, negotiation and support (the chat goes to a person)."),
        new(SimulatorDataCategory.AiReply, Replies.Default, "Thanks for reaching out! Someone from our team will follow up with more details shortly.", "Reply for everything else."),

        new(SimulatorDataCategory.IntentKeywords, Intents.Complaint, "complain, complaint, angry, terrible, worst, refund, broken, disappointed", "Words that mean a complaint."),
        new(SimulatorDataCategory.IntentKeywords, Intents.HumanRequest, "human, real person, speak to someone, agent please, talk to a person", "Words that ask for a person."),
        new(SimulatorDataCategory.IntentKeywords, Intents.CannotProceed, "not right now, not now, maybe later, maybe next, next quarter, can't afford, cannot afford, budget is tight, budget is low, call me after, come back in, ask me in, after a few months", "Words that mean interested but not now (a follow-up is planned)."),
        new(SimulatorDataCategory.IntentKeywords, Intents.PurchaseIntent, "want to buy, how do i pay, payment, purchase, book it, ready to buy", "Words that mean they want to buy."),
        new(SimulatorDataCategory.IntentKeywords, Intents.DemoRequest, "demo, site visit, appointment, trial, show me", "Words that ask for a demo or visit."),
        new(SimulatorDataCategory.IntentKeywords, Intents.Negotiation, "discount, lower price, best price, negotiate, cheaper", "Words that mean price negotiation."),
        new(SimulatorDataCategory.IntentKeywords, Intents.Support, "error code, not working, technical issue, bug, crash, doesn't work", "Words that mean a technical problem."),
        new(SimulatorDataCategory.IntentKeywords, Intents.PriceEnquiry, "price, cost, how much, rate, charges", "Words that ask about price."),
        new(SimulatorDataCategory.IntentKeywords, Intents.Information, "hours, location, hello, hi, info, information", "Words that ask for general information."),

        new(SimulatorDataCategory.TaskAnswer, Answers.TextReply, "This is a predefined test reply for {task}. It was not produced by an AI provider and is not based on your documents.", "Plain-text reply of a product-knowledge task. {task} = the task name."),
        new(SimulatorDataCategory.TaskAnswer, Answers.Answer, "A predefined answer used for testing.", "The answer under the Success scenario."),
        new(SimulatorDataCategory.TaskAnswer, Answers.MissingEvidenceNote, "Predefined test data. No source was read and no AI provider was contacted.", "The note sent with every predefined answer."),
        new(SimulatorDataCategory.TaskAnswer, Answers.ConflictAnswer, "The price differs between two sources.", "The answer under the Conflict scenario."),
        new(SimulatorDataCategory.TaskAnswer, Answers.ConflictBrochurePrice, "999", "Conflict scenario: the price in the brochure."),
        new(SimulatorDataCategory.TaskAnswer, Answers.ConflictWebsitePrice, "1499", "Conflict scenario: the price on the website."),
        new(SimulatorDataCategory.TaskAnswer, Answers.MalformedReply, "sorry, here is the answer: { \"answer\": ", "The broken reply under the Malformed scenario."),

        new(SimulatorDataCategory.Setting, Settings.UniquePerRun, "true", "true = each lead run tags names/websites and changes the last 4 phone digits, so a rerun is not all duplicates. false = data is used exactly as written."),
        new(SimulatorDataCategory.Setting, Settings.FlawedSharePercent, "30", "Share (0-90%) of a lead run that comes from the flawed businesses (phone not on page / closed)."),
        new(SimulatorDataCategory.Setting, Settings.DndDigitSumRule, "true", "true = besides the DND numbers list, about 1 in 4 numbers (digits add up to a multiple of 4) also fail the DND check."),
    };

    public static SimulatorKeyDefinition? Find(SimulatorDataCategory category, string key) =>
        Keys.FirstOrDefault(k => k.Category == category && string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool IsKeyed(SimulatorDataCategory category) =>
        category is SimulatorDataCategory.AiReply or SimulatorDataCategory.IntentKeywords or SimulatorDataCategory.TaskAnswer or SimulatorDataCategory.Setting;

    /// <summary>The starter lead businesses: a mix of kinds of business, most of which qualify, and a few flawed ones in turn. City is
    /// left blank so the run's own location is used.</summary>
    public static readonly IReadOnlyList<SimulatorLeadBusiness> DefaultBusinesses = new SimulatorLeadBusiness[]
    {
        new("Sharma Dental Care", "Dental clinic", "Dr. Rakesh Sharma", "SCO 12, Sector 17", null, "+91 98140 21001", "hello@sharmadental.example", "https://sharmadental.example", 86, SimulatorLeadVariant.Qualified),
        new("Smile Studio Dental", "Dental clinic", "Dr. Neha Kapoor", "Booth 45, Sector 35", null, "+91 98720 31002", "care@smilestudio.example", "https://smilestudio.example", 78, SimulatorLeadVariant.Qualified),
        new("Vision Plus Eye Hospital", "Eye clinic", "Dr. Amit Verma", "Plot 7, Phase 2", null, "+91 99150 41003", "info@visionplus.example", "https://visionplus.example", 82, SimulatorLeadVariant.Qualified),
        new("Netra Jyoti Eye Centre", "Eye clinic", "Dr. Sunita Gill", "House 221, Sector 22", null, "+91 97790 51004", "contact@netrajyoti.example", "https://netrajyoti.example", 74, SimulatorLeadVariant.Qualified),
        new("FitZone Gym", "Gym", "Rohit Malhotra", "SCF 88, Phase 7", null, "+91 98880 61005", "join@fitzone.example", "https://fitzone.example", 71, SimulatorLeadVariant.Qualified),
        new("Iron Temple Fitness", "Gym", "Karan Bedi", "Top Floor, City Centre Mall", null, "+91 98150 71006", "hello@irontemple.example", "https://irontemple.example", 69, SimulatorLeadVariant.Qualified),
        new("Glow Beauty Salon", "Salon", "Priya Arora", "Shop 3, Elante Market", null, "+91 98550 81007", "book@glowsalon.example", "https://glowsalon.example", 76, SimulatorLeadVariant.Qualified),
        new("Bright Future Coaching", "Coaching institute", "Manish Gupta", "SCO 140, Sector 34", null, "+91 99880 91008", "admissions@brightfuture.example", "https://brightfuture.example", 88, SimulatorLeadVariant.Qualified),
        new("Green Leaf Pharmacy", "Pharmacy", "Anil Jain", "Shop 9, Sector 15 Market", null, "+91 97800 11009", "orders@greenleaf.example", "https://greenleaf.example", 66, SimulatorLeadVariant.Qualified),
        new("HomeNest Realty", "Real estate agency", "Vikram Singh", "Office 5, Industrial Area", null, "+91 98140 12010", "sales@homenest.example", "https://homenest.example", 90, SimulatorLeadVariant.Qualified),
        new("Little Steps Play School", "Play school", "Meenu Bansal", "House 12, Sector 8", null, "+91 98760 13011", "info@littlesteps.example", "https://littlesteps.example", 73, SimulatorLeadVariant.Qualified),
        new("City Physio Clinic", "Physiotherapy clinic", "Dr. Harpreet Kaur", "SCO 61, Sector 20", null, "+91 99140 14012", "appointments@cityphysio.example", "https://cityphysio.example", 80, SimulatorLeadVariant.Qualified),

        new("Unverified Phone Clinic", "Dental clinic", null, null, null, "+91 90000 00000", null, "https://unverified-listing.example", 75, SimulatorLeadVariant.PhoneNotOnPage),
        new("Shut Down Opticals", "Eye clinic", null, null, null, "+91 98765 43210", null, "https://shutdown-opticals.example", 80, SimulatorLeadVariant.PermanentlyClosed),
        new("No Number Fitness", "Gym", null, null, null, "+91 90000 11111", null, "https://nonumber-fitness.example", 72, SimulatorLeadVariant.PhoneNotOnPage),
        new("Closed Down Salon", "Salon", null, null, null, "+91 98765 12345", null, "https://closed-salon.example", 77, SimulatorLeadVariant.PermanentlyClosed),
    };

    /// <summary>Numbers the simulated DND check always rejects. One of them matches a starter business.</summary>
    public static readonly IReadOnlyList<string> DefaultDndNumbers = new[] { "+91 99999 00000", "+91 97790 51004" };

    /// <summary>Every starter row, as entities, in display order - used by the migration and by "Restore defaults".</summary>
    public static IReadOnlyList<SimulatorDummyRecord> DefaultRecords()
    {
        var rows = new List<SimulatorDummyRecord>();
        var order = 0;
        foreach (var b in DefaultBusinesses)
        {
            rows.Add(new SimulatorDummyRecord
            {
                Category = SimulatorDataCategory.LeadBusiness, Name = b.Name, BusinessType = b.BusinessType, ContactPerson = b.ContactPerson,
                Address = b.Address, City = b.City, Phone = b.Phone, Email = b.Email, Website = b.Website, LeadScore = b.LeadScore,
                Variant = b.Variant, IsActive = true, DisplayOrder = order += 10
            });
        }

        foreach (var number in DefaultDndNumbers)
            rows.Add(new SimulatorDummyRecord { Category = SimulatorDataCategory.DndNumber, Name = number, IsActive = true, DisplayOrder = order += 10 });

        foreach (var k in Keys)
            rows.Add(new SimulatorDummyRecord { Category = k.Category, Name = k.Key, Value = k.DefaultValue, IsActive = true, DisplayOrder = order += 10 });

        return rows;
    }
}

public record SimulatorKeyDefinition(SimulatorDataCategory Category, string Key, string DefaultValue, string Help);

/// <summary>A dummy business as the simulated lead discovery uses it.</summary>
public record SimulatorLeadBusiness(
    string Name,
    string? BusinessType,
    string? ContactPerson,
    string? Address,
    string? City,
    string? Phone,
    string? Email,
    string? Website,
    int LeadScore,
    SimulatorLeadVariant Variant);
