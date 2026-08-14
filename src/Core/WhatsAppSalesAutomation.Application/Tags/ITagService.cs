using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Tags;

/// <summary>
/// Managed CRUD over customer tags. Tags are also created implicitly by
/// <c>CustomerService.AddTagsAsync</c> and by import, so this exists mainly to rename, tidy and
/// retire the vocabulary those paths accumulate.
/// </summary>
public interface ITagService
{
    Task<PagedResult<TagDto>> GetPagedAsync(PagedRequest request, CancellationToken cancellationToken = default);

    Task<TagDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TagDto> CreateAsync(CreateTagRequest request, CancellationToken cancellationToken = default);

    Task<TagDto> UpdateAsync(Guid id, UpdateTagRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard delete - tags carry no history worth preserving. Deleting a tag that is still applied to
    /// customers is refused unless <paramref name="force"/> is set, since the assignments go with it.
    /// </summary>
    Task DeleteAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);
}
