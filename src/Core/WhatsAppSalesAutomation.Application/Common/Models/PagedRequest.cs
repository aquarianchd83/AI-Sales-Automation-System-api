namespace WhatsAppSalesAutomation.Application.Common.Models;

/// <summary>Bindable from query string, e.g. <c>?page=2&amp;pageSize=50&amp;search=john</c>.</summary>
public record PagedRequest
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    private int _page = 1;
    private int _pageSize = DefaultPageSize;

    public int Page
    {
        get => _page;
        init => _page = value > 0 ? value : 1;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value is > 0 and <= MaxPageSize ? value : DefaultPageSize;
    }

    public string? Search { get; init; }
}
