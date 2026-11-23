using Microsoft.EntityFrameworkCore;

namespace WhatsAppSalesAutomation.Application.Common.Models;

public static class PagedQueryExtensions
{
    /// <summary>
    /// The total for a page that has already been read. When the page came back short it is the last page, so the total is
    /// known without a second round trip: (page - 1) * pageSize + rows read. Only a full page (or an empty page past the end)
    /// still runs COUNT(*). Most list reads - a filtered inbox, a search, a short conversation - end on page one, so this
    /// drops the COUNT query (which re-runs the whole filter, including any LIKE search) from the common case.
    /// </summary>
    public static async Task<int> CountForPageAsync<T>(
        this IQueryable<T> query, PagedRequest request, int itemsOnPage, CancellationToken cancellationToken = default)
    {
        if (itemsOnPage < request.PageSize && (itemsOnPage > 0 || request.Page == 1))
            return (request.Page - 1) * request.PageSize + itemsOnPage;

        return await query.CountAsync(cancellationToken);
    }
}
