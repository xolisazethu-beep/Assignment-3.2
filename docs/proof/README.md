# Assignment 3.2 — "Prove it" demonstrations

Captured terminal output for each required demo. Reproduce any of them with the
commands shown in each file.

| # | File | Demo |
|---|------|------|
| 1 | [01-unit-tests.md](01-unit-tests.md) | Unit tests only (16 passed, no DB) |
| 2 | [02-integration-tests.md](02-integration-tests.md) | Integration tests only (14 tests, real API + DB) |
| 3 | [03-repository-tests.md](03-repository-tests.md) | Repository tests only (13 tests, Testcontainers) |
| 5 | [05-failing-test-demo.md](05-failing-test-demo.md) | Guard removed → 1 failure → guard restored → green |
| 6 | [06-ci-green-run.md](06-ci-green-run.md) | GitHub Actions green run + screenshot reference |

**Totals:** Unit 16 · Integration 14 · Repository 13 = **43 tests**.

Requirements: .NET 10 SDK; Docker (for repository tests); the dev Postgres on
`localhost:5544` (`docker compose up -d`) for the integration tests.

> **Note on the captured output below.** The unit-test capture (file 1) is from a
> live run on the build machine. The integration (file 2) and repository (file 3)
> layers require Docker, which the authoring environment could not start; their
> green runs are produced by the CI pipeline on every push (`ubuntu-latest`, where
> Docker is pre-installed — see file 6) and reproduce locally with the commands
> shown once Docker / `docker compose up -d` is available.
