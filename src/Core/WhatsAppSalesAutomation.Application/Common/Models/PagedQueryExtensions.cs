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
    public static Task<int> CountForPageAsync<T>(
        this IQueryable<T> query, PagedRequest request, int itemsOnPage, CancellationToken cancellationToken = default) =>
        query.CountForPageAsync(request.Page, request.PageSize, itemsOnPage, cancellationToken);

    /// <inheritdoc cref="CountForPageAsync{T}(IQueryable{T}, PagedRequest, int, CancellationToken)"/>
    /// <remarks>For list queries whose paging lives on their own query type rather than a <see cref="PagedRequest"/>.</remarks>
    public static async Task<int> CountForPageAsync<T>(
        this IQueryable<T> query, int page, int pageSize, int itemsOnPage, CancellationToken cancellationToken = default)
    {
        if (itemsOnPage < pageSize && (itemsOnPage > 0 || page == 1))
            return (page - 1) * pageSize + itemsOnPage;

        return await query.CountAsync(cancellationToken);
    }
}
