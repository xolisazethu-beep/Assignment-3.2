# Proof: Repository tests only (Testcontainers)

Each run starts a throwaway `postgres:16` container, applies the REAL migrations
(check constraints + the stored generated `tsvector` column + GIN index), and runs
the repository against it. Between tests the data is wiped with Respawn, so the
suite passes when run repeatedly. Requires Docker.

## Command

```bash
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Repository"
```

> Requires Docker (Testcontainers starts its own `postgres:16`). The authoring
> environment could not start Docker, so the green run is produced by the CI
> pipeline (`ubuntu-latest`, Docker pre-installed) on every push — see
> [06-ci-green-run.md](06-ci-green-run.md). Expected: `Passed: 13, Failed: 0`. The
> CI log shows Testcontainers pulling/starting the image before the tests run.

## What ran (13 tests, `JobListingRepositoryTests`)

Brief-required (Part 5):

- `GetActiveListingsPagedAsync_Page1_ReturnsCorrectCount` — seeds 6 active, page 1 (size 4): `Data == 4`, `TotalCount == 6`, `HasNextPage`, `!HasPreviousPage`.
- `GetActiveListingsPagedAsync_Page2_ReturnsDifferentRows` — page 1 vs page 2 (size 3) IDs collected as `HashSet<Guid>`; intersection empty.
- `GetActiveListingsPagedAsync_ResultsAreOrderedByPostedAtDescending` — each `CreatedAt` ≥ the next (newest first).
- `GetActiveListingsPagedAsync_ExcludesExpiredListings` — 3 active + 2 expired → `TotalCount == 3`.
- `CheckConstraint_RejectsSalaryMaxLessThanSalaryMin` — bypasses the C# guard; asserts `PostgresException.SqlState` starts with `23` (integrity-constraint violation), not the locale-dependent message.
- `CheckConstraint_RejectsExpiresAtBeforeCreatedAt` — `ExpiresAt < CreatedAt`; `SaveChangesAsync` throws `DbUpdateException` (SQLSTATE 23xxx).
- `HasAppliedAsync_WhenApplicationExists_ReturnsTrue` — seeds company + listing + applicant + application; compiled query returns `true`.
- `HasAppliedAsync_WhenNoApplicationExists_ReturnsFalse` — no application seeded; returns `false`.
- `FullTextSearchAsync_ReturnsStemmedMatches` — searches `"engineer"` and finds `"Software Engineering Position"` via the `english` stemmer on the stored `tsvector`.
- `FullTextSearchAsync_DoesNotReturnNonMatchingListings` — 2 of 4 listings mention "Kubernetes"; result count is exactly 2.

Extra regression cover:

- `AddAsync_ThenSaveChanges_PersistsListingRetrievableById` — round-trips a listing through real SQL.
- `GetActiveListingsPagedAsync_ExcludesClosedAndExpiredListings` — `Status = Active AND ExpiresAt > now` against real rows.
- `PatchAsync_AppliesOnlyNonNullFields_LeavingOthersIntact` — partial update touches only supplied fields.

### Why the full-text test cannot pass on the EF in-memory provider

`FullTextSearchAsync_ReturnsStemmedMatches` relies on PostgreSQL computing the stored
`tsvector` column and `to_tsquery('english', …)` stemming "engineer" to the same
lexeme as "Engineering" — the in-memory provider has no `tsvector`, no GIN index and
no text-search configuration, so the query cannot even be translated, let alone stem.
