using System.Text.RegularExpressions;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// Common product, service and topic terms for the industries the Business Profile suggests - what keyword
/// suggestion falls back to when the tenant has no real AI provider to ask. A starting point to edit, not an
/// exhaustive list. An industry is matched by its name or by any word it shares with one listed here.
/// </summary>
public static class IndustryKeywordCatalog
{
    private static readonly (string Industry, string[] Aliases, string[] Keywords)[] Entries =
    {
        ("Automotive", new[] { "auto", "car", "vehicle", "motor", "dealership" },
            new[] { "new cars", "used cars", "test drive", "car loan", "car insurance", "service and repair", "spare parts", "electric vehicles", "trade-in", "extended warranty" }),
        ("Education", new[] { "school", "college", "coaching", "tuition", "training", "learning", "institute" },
            new[] { "admissions", "online courses", "coaching classes", "entrance exam preparation", "scholarship", "certification", "career counselling", "study abroad", "tuition", "skill development" }),
        ("Financial services", new[] { "finance", "bank", "loan", "insurance", "investment", "wealth", "mutual fund" },
            new[] { "personal loan", "home loan", "business loan", "credit card", "insurance", "mutual funds", "fixed deposit", "tax planning", "wealth management", "retirement planning" }),
        ("Food & beverage", new[] { "food", "restaurant", "cafe", "catering", "beverage", "bakery", "kitchen" },
            new[] { "dine in", "home delivery", "takeaway", "catering", "party orders", "menu", "combo offers", "bakery", "healthy meals", "table reservation" }),
        ("Healthcare", new[] { "health", "clinic", "hospital", "medical", "dental", "pharma", "wellness", "diagnostic" },
            new[] { "appointment booking", "health check-up", "specialist consultation", "diagnostic tests", "dental care", "eye care", "physiotherapy", "teleconsultation", "vaccination", "preventive care" }),
        ("Real estate", new[] { "property", "realty", "housing", "builder", "construction" },
            new[] { "apartments", "villas", "plots", "site visit", "home loan", "resale", "rental", "commercial space", "ready to move", "property investment" }),
        ("Retail & e-commerce", new[] { "retail", "ecommerce", "e-commerce", "shop", "store", "fashion", "apparel", "online store" },
            new[] { "new arrivals", "discounts", "festive offers", "free delivery", "easy returns", "gift cards", "loyalty rewards", "cash on delivery", "bestsellers", "seasonal sale" }),
        ("Solar & energy", new[] { "solar", "energy", "renewable", "power", "electric" },
            new[] { "solar panels", "rooftop solar", "net metering", "solar inverter", "battery storage", "government subsidy", "maintenance", "off-grid solar", "electricity savings", "installation" }),
        ("Technology & software", new[] { "technology", "software", "saas", "app", "digital", "tech", "web", "it" },
            new[] { "custom software", "mobile app development", "web development", "cloud migration", "cybersecurity", "AI automation", "IT support", "SaaS", "managed services", "digital transformation" }),
        ("Travel & hospitality", new[] { "travel", "hotel", "hospitality", "tour", "resort", "tourism", "holiday" },
            new[] { "holiday packages", "hotel booking", "flight tickets", "visa assistance", "group tours", "honeymoon packages", "weekend getaways", "airport transfers", "travel insurance", "resort stays" }),
    };

    /// <summary>
    /// The terms for an industry, or none when it is not one the catalog knows. An exact industry name wins; failing
    /// that, an alias counts only as a whole word ("car" matches "car dealership", not "healthcare").
    /// </summary>
    public static IReadOnlyList<string> For(string? industry)
    {
        if (string.IsNullOrWhiteSpace(industry))
            return Array.Empty<string>();

        var text = industry.Trim().ToLowerInvariant();

        foreach (var (name, _, keywords) in Entries)
        {
            if (string.Equals(name, industry.Trim(), StringComparison.OrdinalIgnoreCase))
                return keywords;
        }

        foreach (var (name, aliases, keywords) in Entries)
        {
            if (aliases.Append(name.ToLowerInvariant()).Any(term => Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b")))
                return keywords;
        }

        return Array.Empty<string>();
    }
}
