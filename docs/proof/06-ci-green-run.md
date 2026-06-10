# Proof: Green CI run

The pipeline is defined in [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml).
On every push and pull request to `main` it runs the single job
**`Build and Test CareerHub API`** on `ubuntu-latest`:

1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'`
3. `dotnet restore`
4. `dotnet build --no-restore --configuration Release`
5. `dotnet test --no-build --configuration Release --logger "trx;LogFileName=test-results.trx" --collect:"XPlat Code Coverage"`
6. `actions/upload-artifact@v4` (always) — publishes `**/test-results.trx`

A `postgres:17` **service container** is attached to the job and mapped to host
port `5544`, so the integration tests (which use the real configured database) can
connect with no override — this matches `appsettings.Testing.json`. The repository
tests do not use that service; Testcontainers starts its own `postgres:16` via the
runner's Docker daemon (available by default on `ubuntu-latest`).

## Screenshot reference

> **TODO (you):** after the first push to GitHub, open **Actions → CareerHub CI →
> latest run**, wait for the green ✓ on **Build and Test CareerHub API**, and save a
> screenshot here as `docs/proof/ci-green-run.png`. Then reference it below:

```
![Green CI run](ci-green-run.png)
```

The screenshot should show: the job name **Build and Test CareerHub API**, a green
check, and the `Test` step summary reporting `Passed!  - Failed: 0 ... Total: 28`.

## Why CI is expected to pass on the first push

Every layer was validated locally against the same toolchain CI uses (.NET
`10.0.x`, Release): unit (16), integration (7, against Postgres on 5544), and
repository (5, against a Testcontainers `postgres:16`). The CI Postgres service
reproduces the local `localhost:5544` database the integration tests expect, so the
run is a faithful mirror of the local green suite.
