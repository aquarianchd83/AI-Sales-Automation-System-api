using System.Globalization;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Simulator;

/// <summary>The active dummy data, read once and shared by the simulators. A keyed value that is missing falls back to the built-in one
/// in <see cref="SimulatorDataCatalog"/>; the lists are exactly what is in the table.</summary>
public sealed class SimulatorDataSnapshot
{
    private readonly Dictionary<(SimulatorDataCategory, string), string> _values;

    public SimulatorDataSnapshot(IEnumerable<SimulatorDummyRecord> activeRecords)
    {
        var rows = activeRecords.Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).ThenBy(r => r.CreatedAt).ToList();

        Businesses = rows.Where(r => r.Category == SimulatorDataCategory.LeadBusiness && !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new SimulatorLeadBusiness(r.Name.Trim(), r.BusinessType, r.ContactPerson, r.Address, r.City, r.Phone, r.Email, r.Website,
                r.LeadScore ?? 75, r.Variant ?? SimulatorLeadVariant.Qualified))
            .ToList();

        DndDigits = rows.Where(r => r.Category == SimulatorDataCategory.DndNumber)
            .Select(r => Digits(r.Name)).Where(d => d.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        _values = new Dictionary<(SimulatorDataCategory, string), string>();
        foreach (var r in rows.Where(r => SimulatorDataCatalog.IsKeyed(r.Category) && r.Value is not null))
            _values.TryAdd((r.Category, r.Name.Trim().ToLowerInvariant()), r.Value!);
    }

    /// <summary>The starter data, for when there is no database (tests, and a provider that could not read).</summary>
    public static SimulatorDataSnapshot Defaults { get; } = new(SimulatorDataCatalog.DefaultRecords());

    public IReadOnlyList<SimulatorLeadBusiness> Businesses { get; }

    /// <summary>Digits only, so "+91 99999 00000" and "+919999900000" match.</summary>
    public IReadOnlySet<string> DndDigits { get; }

    public string Get(SimulatorDataCategory category, string key) =>
        _values.TryGetValue((category, key.ToLowerInvariant()), out var value)
            ? value
            : SimulatorDataCatalog.Find(category, key)?.DefaultValue ?? string.Empty;

    public string Reply(string key) => Get(SimulatorDataCategory.AiReply, key);

    public string Answer(string key) => Get(SimulatorDataCategory.TaskAnswer, key);

    public string[] Keywords(string intent) =>
        Get(SimulatorDataCategory.IntentKeywords, intent)
            .Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Flag(string key) =>
        bool.TryParse(Get(SimulatorDataCategory.Setting, key).Trim(), out var on)
            ? on
            : bool.Parse(SimulatorDataCatalog.Find(SimulatorDataCategory.Setting, key)!.DefaultValue);

    public int Number(string key, int min, int max) =>
        int.TryParse(Get(SimulatorDataCategory.Setting, key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, min, max)
            : int.Parse(SimulatorDataCatalog.Find(SimulatorDataCategory.Setting, key)!.DefaultValue, CultureInfo.InvariantCulture);

    public bool IsDndNumber(string phone) => DndDigits.Contains(Digits(phone));

    public static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
