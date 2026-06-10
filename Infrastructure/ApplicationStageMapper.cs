using CareerHub.Api.Models;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// The five internal pipeline statuses (<see cref="ApplicationStatus"/>) are an
/// employer-facing concept. A job seeker thinks in four simpler buckets — did I
/// apply, is it still being considered, did I get it, or was I turned down. This
/// mapper is the single place that collapses the internal statuses into those
/// applicant-friendly <see cref="ApplicationStage"/> buckets, so the "track my
/// applications" endpoints never leak recruiter jargon and the mapping can never
/// drift between endpoints.
///
///   Submitted                → Applied   (received, not yet looked at)
///   UnderReview, Shortlisted → Pending   (actively being considered)
///   Offered                  → Accepted  (you got the role)
///   Rejected                 → Rejected  (unsuccessful)
/// </summary>
public enum ApplicationStage { Applied, Pending, Accepted, Rejected }

public static class ApplicationStageMapper
{
    public static ApplicationStage ToStage(ApplicationStatus status) => status switch
    {
        ApplicationStatus.Submitted => ApplicationStage.Applied,
        ApplicationStatus.UnderReview => ApplicationStage.Pending,
        ApplicationStatus.Shortlisted => ApplicationStage.Pending,
        ApplicationStatus.Offered => ApplicationStage.Accepted,
        ApplicationStatus.Rejected => ApplicationStage.Rejected,
        _ => ApplicationStage.Pending
    };

    /// <summary>
    /// The set of internal statuses a friendly stage covers — used to translate an
    /// applicant's <c>?stage=Pending</c> filter into the SQL <c>WHERE Status IN (…)</c>.
    /// </summary>
    public static IReadOnlyCollection<ApplicationStatus> ToStatuses(ApplicationStage stage) => stage switch
    {
        ApplicationStage.Applied => [ApplicationStatus.Submitted],
        ApplicationStage.Pending => [ApplicationStatus.UnderReview, ApplicationStatus.Shortlisted],
        ApplicationStage.Accepted => [ApplicationStatus.Offered],
        ApplicationStage.Rejected => [ApplicationStatus.Rejected],
        _ => []
    };

    /// <summary>
    /// Parse a free-text stage filter (case-insensitive). Accepts the friendly
    /// names plus a couple of natural synonyms a client might send. Returns null
    /// for an empty/unrecognised value so the caller can treat it as "no filter".
    /// </summary>
    public static ApplicationStage? ParseStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "applied" or "submitted" => ApplicationStage.Applied,
            "pending" or "inreview" or "underreview" or "shortlisted" => ApplicationStage.Pending,
            "accepted" or "offered" => ApplicationStage.Accepted,
            "rejected" or "declined" => ApplicationStage.Rejected,
            _ => null
        };
    }
}
