using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CareerHub.Api.Data;
using CareerHub.Api.DTOs;
using CareerHub.Api.Exceptions;
using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CareerHub.Api.Services;

/// <summary>
/// Registration, login and JWT issuance for the two account types. It talks to
/// the <see cref="CareerHubDbContext"/> directly (auth has no repository in this
/// assignment — the queries are trivial and not reused). Roles are constants so
/// the same strings flow into the token and into <c>[Authorize(Roles=...)]</c>.
/// </summary>
public class AuthService(CareerHubDbContext db, IConfiguration config) : IAuthService
{
    public const string ApplicantRole = "Applicant";
    public const string EmployerRole = "Employer";

    public async Task<AuthResponse> RegisterApplicantAsync(RegisterApplicantRequest request, CancellationToken ct = default)
    {
        await EnsureEmailAvailableAsync(request.Email, ct);

        var applicant = new Applicant
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName,
            Email = request.Email,
            PasswordHash = PasswordHasher.Hash(request.Password)
        };
        db.Applicants.Add(applicant);
        await db.SaveChangesAsync(ct);

        return BuildResponse(applicant.Id, applicant.Email, ApplicantRole, companyId: null);
    }

    public async Task<AuthResponse> RegisterEmployerAsync(RegisterEmployerRequest request, CancellationToken ct = default)
    {
        await EnsureEmailAvailableAsync(request.Email, ct);

        var companyExists = await db.Companies.AnyAsync(c => c.Id == request.CompanyId, ct);
        if (!companyExists)
            throw new NotFoundException("The selected company does not exist.");

        var employer = new Employer
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName,
            Email = request.Email,
            PasswordHash = PasswordHasher.Hash(request.Password),
            CompanyId = request.CompanyId
        };
        db.Employers.Add(employer);
        await db.SaveChangesAsync(ct);

        return BuildResponse(employer.Id, employer.Email, EmployerRole, employer.CompanyId);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        // Resolve the email across both account types. Email is globally unique
        // (enforced at registration), so at most one of these matches.
        var applicant = await db.Applicants.FirstOrDefaultAsync(a => a.Email == request.Email, ct);
        if (applicant is not null && PasswordHasher.Verify(request.Password, applicant.PasswordHash))
            return BuildResponse(applicant.Id, applicant.Email, ApplicantRole, companyId: null);

        var employer = await db.Employers.FirstOrDefaultAsync(e => e.Email == request.Email, ct);
        if (employer is not null && PasswordHasher.Verify(request.Password, employer.PasswordHash))
            return BuildResponse(employer.Id, employer.Email, EmployerRole, employer.CompanyId);

        // Identical message whether the email is unknown or the password is wrong —
        // never reveal which, nor which table an account lives in.
        throw new UnauthorizedException("Invalid email or password.");
    }

    /// <summary>
    /// Two per-table unique indexes cannot guarantee an email is unique across
    /// BOTH tables, which the single-login resolver depends on. So we check both
    /// here at registration time.
    /// </summary>
    private async Task EnsureEmailAvailableAsync(string email, CancellationToken ct)
    {
        var taken = await db.Applicants.AnyAsync(a => a.Email == email, ct)
                 || await db.Employers.AnyAsync(e => e.Email == email, ct);
        if (taken)
            throw new ConflictException("An account with this email already exists.");
    }

    private AuthResponse BuildResponse(Guid userId, string email, string role, Guid? companyId) =>
        new(GenerateToken(userId, email, role, companyId), userId, email, role, companyId);

    private string GenerateToken(Guid userId, string email, string role, Guid? companyId)
    {
        var jwt = config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            // ClaimTypes.Role makes [Authorize(Roles="Employer")] work with no
            // extra RoleClaimType configuration.
            new(ClaimTypes.Role, role)
        };

        // Only employers carry a company. The protected endpoints read this back
        // from the token — the client cannot choose which company it acts as.
        if (companyId is not null)
            claims.Add(new Claim("companyId", companyId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
