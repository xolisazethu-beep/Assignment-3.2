using Asp.Versioning;
using CareerHub.Api.DTOs;
using CareerHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CareerHub.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Tags("Auth")]
[AllowAnonymous] // registration and login must be reachable without a token
public class AuthController(IAuthService auth) : ControllerBase
{
    /// <summary>Create a job-seeker account and return a token.</summary>
    [HttpPost("register/applicant")]
    public async Task<AuthResponse> RegisterApplicant(RegisterApplicantRequest request, CancellationToken ct)
        => await auth.RegisterApplicantAsync(request, ct);

    /// <summary>Create a recruiter account bound to a company and return a token.</summary>
    [HttpPost("register/employer")]
    public async Task<AuthResponse> RegisterEmployer(RegisterEmployerRequest request, CancellationToken ct)
        => await auth.RegisterEmployerAsync(request, ct);

    /// <summary>Verify credentials (either account type) and return a token.</summary>
    [HttpPost("login")]
    public async Task<AuthResponse> Login(LoginRequest request, CancellationToken ct)
        => await auth.LoginAsync(request, ct);
}
