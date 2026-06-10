# Proof: The deliberate-failure demo

This demonstrates that the test `PatchAsync_WhenOnlyTitleChanged_DoesNotCallSalaryValidation`
is *load-bearing* — it actually guards the conditional salary-validation block in
`JobService.PatchAsync`, rather than passing vacuously.

## The change (guard temporarily removed)

In `Services/JobService.cs`, the conditional that re-validates salary **only when a
salary field was supplied** was removed so the check runs unconditionally:

```diff
- // Re-run the salary-range check only if either salary field was provided.
- if (req.SalaryMin is not null || req.SalaryMax is not null)
- {
-     if (listing.SalaryMin is <= 0)
-         throw new ArgumentException("SalaryMin must be greater than zero.");
-     if (listing.SalaryMin is not null && listing.SalaryMax is not null && listing.SalaryMax <= listing.SalaryMin)
-         throw new ArgumentException("SalaryMax must be greater than SalaryMin.");
- }
+ if (listing.SalaryMin is <= 0)
+     throw new ArgumentException("SalaryMin must be greater than zero.");
+ if (listing.SalaryMin is not null && listing.SalaryMax is not null && listing.SalaryMax <= listing.SalaryMin)
+     throw new ArgumentException("SalaryMax must be greater than SalaryMin.");
```

## Output with the guard removed (1 failure)

```bash
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Unit"
```

```
[xUnit.net]     API.Tests.Unit.Services.JobListingServiceTests.PatchAsync_WhenOnlyTitleChanged_DoesNotCallSalaryValidation [FAIL]
  Failed API.Tests.Unit.Services.JobListingServiceTests.PatchAsync_WhenOnlyTitleChanged_DoesNotCallSalaryValidation [1 s]
   Did not expect any exception, but found System.ArgumentException: SalaryMax must be greater than SalaryMin.
   at ...JobListingServiceTests.cs:line 128

Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16, Duration: 2 s - API.Tests.dll (net10.0)
```

Exactly as intended: the test seeds a tracked entity with a *deliberately invalid*
on-disk range (min 100000, max 50000) and patches only the `Title`. With the guard
in place that bad range is left untouched; with the guard removed it gets
re-validated and the patch throws — so the test fails the moment the protection
regresses.

## Output with the guard restored (all green again)

```
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 3 s - API.Tests.dll (net10.0)
```

The guard was restored immediately after capturing the failure (see the final
commit — the shipped code contains the conditional guard).
