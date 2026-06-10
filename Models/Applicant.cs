namespace CareerHub.Api.Models;

/// <summary>
/// A job seeker. Registers once and can apply to many listings. The link to
/// listings runs through the explicit join entity <see cref="Application"/>,
/// because that link carries its own data (status, submission date).
/// </summary>
public class Applicant
{
    public Guid Id { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash. The plain password is never stored.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;   // e.g. "Backend Developer, 4 yrs"
    public int YearsOfExperience { get; set; }

    /// <summary>
    /// The applicant's formal qualifications, certifications and key skills as
    /// free text (e.g. "BSc Computer Science (UCT); AWS Certified Solutions
    /// Architect; C#, PostgreSQL, Kubernetes"). This is the field an employer
    /// searches when shortlisting people who applied to their roles, so it is kept
    /// as one searchable column rather than a rigid taxonomy.
    /// </summary>
    public string Qualifications { get; set; } = string.Empty;

    public ICollection<Application> Applications { get; set; } = [];
}
