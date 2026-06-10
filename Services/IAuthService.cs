using CareerHub.Api.DTOs;

namespace CareerHub.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterApplicantAsync(RegisterApplicantRequest request, CancellationToken ct = default);
    Task<AuthResponse> RegisterEmployerAsync(RegisterEmployerRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}
