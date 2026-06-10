namespace CareerHub.Api.Models;

/// <summary>
/// A recruiter account belonging to one <see cref="Company"/>. This is the
/// employer-side counterpart to <see cref="Applicant"/>: both are login accounts
/// (email + password hash), but an employer additionally carries the company it
/// acts on behalf of. That <see cref="CompanyId"/> is what authorization keys
/// off — an employer may only post listings and read statistics for THIS company.
///
/// A company can have many recruiters (the FK is non-unique), which mirrors how a
/// real employer has several people posting roles.
/// </summary>
public class Employer
{
    public Guid Id { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash. The plain password is never stored.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    // The employer acts on behalf of exactly one company.
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;
}
