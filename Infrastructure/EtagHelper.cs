using System.Security.Cryptography;
using System.Text;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// PART 7: computes a strong-ish ETag for a resource. The fingerprint parts are
/// joined with ':', hashed with SHA-256, base64-encoded and wrapped in double
/// quotes (the HTTP entity-tag grammar requires the quotes). Centralising it here
/// means the jobs and applications controllers never duplicate the hashing logic.
/// </summary>
public static class EtagHelper
{
    public static string Compute(params object?[] parts)
    {
        var raw = string.Join(':', parts.Select(p => p?.ToString() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"\"{Convert.ToBase64String(hash)}\"";
    }
}
