using CareerHub.Api.DTOs;
using Microsoft.AspNetCore.WebUtilities;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// Builds the HATEOAS-lite <see cref="PageLinks"/> for a paged response from the
/// current request (extra improvement #1). Each link preserves the request's
/// existing query string (filters, sort) and only swaps the <c>page</c> /
/// <c>pageSize</c> values, so following a link re-runs the same query on a
/// different page. <c>next</c>/<c>previous</c> are null at the range boundaries.
/// </summary>
public static class PaginationLinks
{
    public static PageLinks Build<T>(HttpRequest request, PagedResponse<T> page)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";

        // Start from the incoming query so filters/sort survive paging.
        var template = QueryHelpers.ParseQuery(request.QueryString.Value)
            .ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value.ToString());

        string Url(int p)
        {
            var q = new Dictionary<string, string?>(template)
            {
                ["page"] = p.ToString(),
                ["pageSize"] = page.PageSize.ToString()
            };
            return QueryHelpers.AddQueryString(baseUrl, q);
        }

        var lastPage = page.TotalPages < 1 ? 1 : page.TotalPages;
        return new PageLinks(
            Self: Url(page.Page),
            Next: page.HasNextPage ? Url(page.Page + 1) : null,
            Previous: page.HasPreviousPage ? Url(page.Page - 1) : null,
            First: Url(1),
            Last: Url(lastPage));
    }
}
