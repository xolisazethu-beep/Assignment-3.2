# Proof: Unit tests only

Pure service-layer tests with the repository substituted by NSubstitute. No Docker
and no database are required, and every test completes in well under 100ms.

## Command

```bash
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Unit"
```

## Output

```
  Determining projects to restore...
  CareerHub.Api -> .../bin/Debug/net10.0/CareerHub.Api.dll
  API.Tests -> .../API.Tests/bin/Debug/net10.0/API.Tests.dll
Test run for .../API.Tests/bin/Debug/net10.0/API.Tests.dll (.NETCoreApp,Version=v10.0)

Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 3 s - API.Tests.dll (net10.0)
```

## What ran (16 tests)

`JobListingServiceTests` (6):
- `CreateAsync_WhenSalaryMaxLessThanSalaryMin_ThrowsInvalidSalaryException`
- `CreateAsync_WhenExpiresAtIsInThePast_ThrowsInvalidListingException`
- `CreateAsync_WhenValid_CallsAddAsyncExactlyOnce`
- `PatchAsync_WhenOnlySalaryMinChanged_CallsValidation`
- `PatchAsync_WhenOnlyTitleChanged_DoesNotCallSalaryValidation`
- `PatchAsync_WhenListingNotFound_ThrowsNotFoundException`

`ApplicationServiceTests` (10 = 5 legal + 4 illegal theory rows + 1 not-found):
- `UpdateStatusAsync_WhenTransitionIsLegal_CallsUpdateAsync` (Submitted→UnderReview,
  UnderReview→Shortlisted, UnderReview→Rejected, Shortlisted→Offered, Shortlisted→Rejected)
- `UpdateStatusAsync_WhenTransitionIsIllegal_ReturnsBadRequest` (Rejected→Submitted,
  Offered→Submitted, Rejected→UnderReview, Offered→Shortlisted)
- `UpdateStatusAsync_WhenApplicationNotFound_ThrowsNotFoundException`

> Note: the real `JobService` throws `ArgumentException` for salary/expiry
> violations (there is no custom `InvalidSalaryException`/`InvalidListingException`
> in the codebase) and the illegal-transition case throws `ArgumentException` that
> the global `ValidationExceptionHandler` maps to HTTP 400. The brief's test names
> are preserved; the assertions target the real exception types.
