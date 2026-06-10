using System.Security.Claims;
using CareerHub.Api.Exceptions;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// Reads the identity claims the API trusts off the validated JWT. <c>[Authorize]</c>
/// guarantees a token is present, but a token of the wrong shape (e.g. an applicant
/// token hitting an employer-only endpoint, or a missing claim) should fail closed
/// as 401 rather than throw a NullReferenceException.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated account's id (applicant or employer).</summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(raw, out var id)) return id;
        throw new UnauthorizedException("The token is missing a valid user id.");
    }

    /// <summary>The employer's company id. Throws if the token has no company claim.</summary>
    public static Guid GetCompanyId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("companyId");
        if (Guid.TryParse(raw, out var id)) return id;
        throw new UnauthorizedException("This action requires an employer account.");
    }
}
