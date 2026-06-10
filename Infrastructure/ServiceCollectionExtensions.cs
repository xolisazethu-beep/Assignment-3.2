using CareerHub.Api.Data;
using CareerHub.Api.Repositories;
using CareerHub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Infrastructure;

/// <summary>
/// Composition root helpers. Program.cs calls these instead of registering each
/// type by hand, which keeps wiring (including the Part 7 interceptor) in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCareerHubInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Interceptor is stateless -> Singleton (Part 7 requirement).
        services.AddSingleton<SlowQueryInterceptor>();

        services.AddDbContext<CareerHubDbContext>((sp, options) =>
            options
                .UseNpgsql(configuration.GetConnectionString("DefaultConnection"))
                // Wire the slow-query interceptor into every command on this context.
                .AddInterceptors(sp.GetRequiredService<SlowQueryInterceptor>()));

        return services;
    }

    public static IServiceCollection AddCareerHubRepositories(this IServiceCollection services)
    {
        services.AddScoped<IJobListingRepository, JobListingRepository>();
        services.AddScoped<IApplicationRepository, ApplicationRepository>();
        services.AddScoped<ICompanyRepository, CompanyRepository>();
        return services;
    }

    public static IServiceCollection AddCareerHubServices(this IServiceCollection services)
    {
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IApplicationService, ApplicationService>();
        return services;
    }
}
