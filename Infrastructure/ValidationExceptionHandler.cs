using CareerHub.Api.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// Maps service-layer failures and the database's own check-constraint rejections
/// to the correct HTTP status as RFC 7807 Problem Details. Validation (400) is the
/// original Assignment 2.4 "Proving It Works" #1 case; the auth/apply work added
/// the 401/404/409 domain exceptions.
/// </summary>
public sealed class ValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            UnauthorizedException ue => (StatusCodes.Status401Unauthorized, "Unauthorized", ue.Message),
            NotFoundException nfe => (StatusCodes.Status404NotFound, "Not found", nfe.Message),
            ConflictException ce => (StatusCodes.Status409Conflict, "Conflict", ce.Message),
            DuplicateApplicationException dae => (StatusCodes.Status409Conflict, "Conflict", dae.Message),
            ArgumentException ae => (StatusCodes.Status400BadRequest, "Validation failed", ae.Message),
            Microsoft.EntityFrameworkCore.DbUpdateException due
                when due.InnerException?.Message.Contains("ck_") == true
                => (StatusCodes.Status400BadRequest, "Validation failed",
                    "A database business rule (check constraint) rejected this data."),
            // The composite-PK unique violation is the race-safe backstop for a
            // duplicate application when the pre-check loses a race.
            Microsoft.EntityFrameworkCore.DbUpdateException due2
                when due2.InnerException?.Message.Contains("PK_applications") == true
                => (StatusCodes.Status409Conflict, "Conflict",
                    "You have already applied to this listing."),
            _ => (0, string.Empty, string.Empty)
        };

        if (status == 0)
            return false; // not ours — let the default pipeline handle it

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail
            }
        });
    }
}
