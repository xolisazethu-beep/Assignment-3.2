namespace CareerHub.Api.Models;

/// <summary>
/// An employer on the CareerHub platform. A company exists independently of any
/// listing and owns many <see cref="JobListing"/> records (the "one" side of a
/// one-to-many). Modelled on real South African employers (see SeedData).
/// </summary>
public class Company
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Head-office town/city, e.g. "Cape Town", "Polokwane".</summary>
    public string City { get; set; } = string.Empty;

    public string Province { get; set; } = string.Empty;
    public string Industry { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public int? FoundedYear { get; set; }

    // Reverse navigation. Never null so callers can enumerate safely.
    public ICollection<JobListing> JobListings { get; set; } = [];
}
