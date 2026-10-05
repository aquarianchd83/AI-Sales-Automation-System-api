using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Domain.Enums;

// Every enum here travels over the API by NAME ("Currency", "RequiresUpdate"), not by number: the setup screens are driven
// by these values, and a renumbered enum must not silently change what a stored or in-flight value means.

/// <summary>The kinds of answer a plan can ask a tenant for. The Talent UI renders one input per type, so a new
/// plan never needs new UI - only a new type does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupFieldType
{
    Text = 0,
    MultilineText = 1,
    Number = 2,
    Decimal = 3,
    Currency = 4,
    Date = 5,
    Dropdown = 6,
    MultiSelect = 7,
    Radio = 8,
    Checkbox = 9,
    FileUpload = 10,
    Url = 11,
    Email = 12,
    Phone = 13
}

/// <summary>How a requirement decides whether it is shown (and therefore required) based on another field's answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupConditionOperator
{
    Equals = 0,
    NotEquals = 1,
    /// <summary>A multi-select contains the value, or a text field contains it.</summary>
    Contains = 2,
    /// <summary>The answer is one of a comma separated list.</summary>
    In = 3,
    NotEmpty = 4,
    Empty = 5
}

/// <summary>
/// A plan's setup requirements are versioned so that editing them never changes an application already
/// running on an earlier version: only a Draft can be edited, only a Published one is handed to new
/// applications, and publishing the next one marks the previous Superseded (it stays readable - existing
/// applications keep using it until migrated).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupVersionStatus
{
    Draft = 0,
    Published = 1,
    Superseded = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicationSetupStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    /// <summary>A completed setup was edited and a mandatory answer is now missing or invalid.</summary>
    Incomplete = 3,
    /// <summary>Completed, but older than the plan version's validity period - it has to be re-confirmed.</summary>
    Expired = 4,
    /// <summary>The plan or its requirements changed and the setup no longer covers them.</summary>
    RequiresUpdate = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicationStatus
{
    Active = 0,
    Archived = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupAuditAction
{
    ApplicationCreated = 0,
    ValueSet = 1,
    ValueCleared = 2,
    SetupCompleted = 3,
    PlanChanged = 4,
    VersionMigrated = 5,
    Executed = 6,
    ExecutionBlocked = 7
}
