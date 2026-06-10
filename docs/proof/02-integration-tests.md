# Proof: Integration tests only

Boots the real API in-process with `WebApplicationFactory<Program>` (environment
`Testing`, real database via `appsettings.Testing.json`, `ConfigureTestServices`
empty) and drives it over HTTP. Requires the dev Postgres on `localhost:5544`
(`docker compose up -d`). These are honestly slower than unit tests because each
pays for a host boot, EF Core migrate+seed, JSON (de)serialisation and a transport
round-trip.

## Command

```bash
# Local: bring up the dev Postgres on localhost:5544 first
docker compose up -d
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Integration"
```

> This layer needs a running PostgreSQL (the host migrates + seeds on startup). The
> authoring environment could not start Docker, so the green run is produced by the
> CI pipeline (`ubuntu-latest`, Postgres service container) on every push — see
> [06-ci-green-run.md](06-ci-green-run.md). Expected: `Passed: 14, Failed: 0`.

## What ran (14 tests, `JobsControllerTests`)

Brief-required (Part 4):

- `GetJobs_ReturnsOk`
- `GetJobs_ResponseIsPagedEnvelope` — deserialises `PagedResponse<JobListingResponse>`; asserts `Page == 1`, `PageSize == 5`, `TotalCount >= 0`
- `GetJobs_ResponseIncludesXTotalCountHeader`
- `GetJobs_WithoutVersion_ReturnsSameStatusAsV1` — `/api/jobs` and `/api/v1/jobs` both `200`
- `GetJobs_ResponseIncludesApiSupportedVersionsHeader` — header present and contains `1.0`
- `CreateJob_WithoutAuthentication_Returns401` — `POST /api/v1/jobs` with no token (the brief's `PostJob_WithoutToken_Returns401`)
- `PostApplication_WithoutToken_Returns401` — `POST /api/v1/jobs/{id}/applications` with no token
- `GetJobById_WithValidId_DoesNotReturn500` — `200` or `404`, never `500`
- `GetJobById_ReturnsStrongETagHeader` — the brief's `GetJobById_ResponseIncludesETagHeader`
- `GetJobById_WithMatchingETag_Returns304`  ← the ETag round-trip (GET id → read
  ETag → re-GET with `If-None-Match` → assert `304 Not Modified`, empty body)

Extra regression cover:

- `GetJobs_ReturnsOkWithPagedEnvelopeAndSeedData`
- `GetJobs_HonoursPageSize`
- `GetJobById_WithUnknownId_Returns404`
- `SearchJobs_ByKeyword_ReturnsOnlyMatchingListings`

All responses are deserialised with `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`
to avoid the camelCase/PascalCase mismatch that is the classic cause of CI-only
integration failures.
