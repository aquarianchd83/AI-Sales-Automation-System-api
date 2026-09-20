namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Shared by every Platform Admin Console content-management entity (<c>Flowchart</c>,
/// <c>FaqEntry</c>). Only <see cref="Published"/> content is meant to be shown outside the console;
/// <see cref="Draft"/> lets a superadmin author and review it first, and <see cref="Archived"/> hides
/// it again without deleting its history.</summary>
public enum ContentStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2
}
