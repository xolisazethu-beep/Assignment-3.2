# CareerHub API — Setup & Scalar Guide

A step-by-step guide to running the CareerHub API and exercising every endpoint
from the **Scalar** API reference UI (the modern replacement for Swagger UI that
ships with .NET's `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore`).

---

## 1. Prerequisites

| Tool | Version used | Check |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --version` |
| Docker Desktop | any recent | `docker --version` |
| EF Core CLI | 10.0.x | `dotnet ef --version` (install: `dotnet tool install --global dotnet-ef`) |

You do **not** need a local PostgreSQL install — it runs in Docker.

---

## 2. Start PostgreSQL

```bash
docker compose up -d
```

This starts **PostgreSQL 17** on `localhost:5544` (see `docker-compose.yml`). The
development connection string in `appsettings.Development.json` already points there:

```
Host=localhost;Port=5544;Database=CareerHub24;Username=postgres;Password=password123
```

Verify it is up:

```bash
docker ps          # look for the careerhub24-pg container, status "Up"
```

---

## 3. Run the API

```bash
dotnet run
```

On startup the app automatically:

1. **Applies all migrations** (`db.Database.MigrateAsync()`) — creates the schema,
   check constraints, indexes, the generated `tsvector` column and the GIN index.
2. **Seeds a realistic South African dataset** (`SeedData`):
   - **10 real SA employers** — Takealot, Discovery, Standard Bank, Capitec, Naspers,
     Vodacom, Shoprite, Sasol, MTN, Yoco.
   - **~6 000 job listings** with salaries in **Rand (ZAR)**, SA locations, generated
     descriptions and **minimum-requirements** text (Matric, tertiary qualification,
     work authorisation, Employment Equity language).
   - **25 applicants** with SA names, plus **applications** across all five pipeline
     statuses.
3. Seeds two **login-ready demo accounts** (see §5).

First run takes ~20–30 s because it inserts thousands of rows. Subsequent runs skip
seeding (it is idempotent — guarded on "are there already companies?").

When you see `Now listening on: http://localhost:5080`, it is ready.

> **Reset the data:** `dotnet ef database drop --force && dotnet run` re-seeds from
> scratch.

---

## 4. Open Scalar

Browse to:

```
http://localhost:5080/scalar/v1
```

(The `http` launch profile opens this automatically.) Scalar is only mapped in the
**Development** environment — see `Program.cs`:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();              // serves the OpenAPI document at /openapi/v1.json
    app.MapScalarApiReference();   // serves the Scalar UI at /scalar/v1
}
```

You will see every endpoint grouped by controller (Auth, Jobs, Applications,
Companies). Each one has a **Test Request** panel on the right where you can fill in
parameters / a JSON body and click **Send**.

---

## 5. Authenticating in Scalar (JWT Bearer)

Most write endpoints are protected. Public reads (`GET /api/jobs`, `/filter`,
`/{id}`, `/search`, `/company/{id}`, `/api/companies`) need **no** token.

**Demo accounts** (seeded with real password hashes):

| Role | Email | Password |
|---|---|---|
| Applicant (job seeker) | `demo.applicant@careerhub.co.za` | `DemoPass123!` |
| Employer (Takealot recruiter) | `demo.employer@takealot.co.za` | `DemoPass123!` |

### Steps

1. In Scalar, open **`POST /api/auth/login`** → **Test Request**.
2. Send this body:
   ```json
   { "email": "demo.employer@takealot.co.za", "password": "DemoPass123!" }
   ```
3. Copy the `token` value from the response.
4. Click the **Authentication** (lock 🔒) area at the top of Scalar, choose the
   **Bearer** scheme, and paste the token. Scalar now adds
   `Authorization: Bearer <token>` to every subsequent request.

> Log in as the **employer** to use `POST /api/jobs` and `GET /api/jobs/stats`.
> Log in as the **applicant** to use `POST /api/jobs/{id}/applications` and
> `GET /api/applications/me`. The role is baked into the token, so the two account
> types unlock different endpoints.

---

## 6. A guided walkthrough

### Public job board (no auth)

| Try | Endpoint | Expected |
|---|---|---|
| Browse the board | `GET /api/jobs` | List of active, unexpired listings (lean projection, no long description) |
| Filter by name | `GET /api/jobs/filter?title=engineer` | Only active listings whose **title** contains "engineer" |
| Filter by location | `GET /api/jobs/filter?location=Cape Town` | Only active listings located in Cape Town |
| Combine filters | `GET /api/jobs/filter?title=Scrum&location=Durban` | The Durban "Scrum Master" listing |
| Filter by type | `GET /api/jobs/filter?type=Internship` | Active internships only (`type` ∈ FullTime, PartTime, Contract, Internship, Learnership) |
| Get one listing | `GET /api/jobs/{id}` | **Full** detail — long description, **minimum requirements**, company city/province/website. `404` for an unknown id |

> Copy any `id` from the `GET /api/jobs` response and paste it into `GET /api/jobs/{id}`
> to see the rich description + minimum-requirements text.

### Full-text search proofs (no auth)

| Try | Endpoint | Expected |
|---|---|---|
| Exact-count proof | `GET /api/jobs/search?q=Kubernetes` | Exactly **3** listings (Takealot, Naspers, Yoco) |
| **Stemming** proof | `GET /api/jobs/search?q=sprint` | Matches the **"Scrum Master"** listing whose description says *sprint**ing*** — something `LIKE 'sprint'` could never do |

### Employer flow (log in as employer first)

| Try | Endpoint | Expected |
|---|---|---|
| Post a job | `POST /api/jobs` | `201 Created`; company comes from your token, not the body |
| Pipeline stats | `GET /api/jobs/stats` | Per-status counts (`Submitted`, `UnderReview`, …) and a `RANK()` — Takealot's most-applied listing is **rank 1** |

Sample `POST /api/jobs` body:

```json
{
  "title": "Senior Backend Engineer",
  "description": "Build and scale our order platform in C# and PostgreSQL.",
  "minimumRequirements": "Matric; degree in CS; 5+ years C#/.NET; valid SA work permit.",
  "location": "Sandton, Gauteng",
  "type": "FullTime",
  "salaryMin": 75000,
  "salaryMax": 110000,
  "expiresAt": "2026-12-31T00:00:00Z"
}
```

### Applicant flow (log in as applicant first)

| Try | Endpoint | Expected |
|---|---|---|
| Apply | `POST /api/jobs/{id}/applications` (body `{ "coverNote": "..." }`) | `201`; a second apply to the same listing is rejected (one application per person per listing) |
| My applications | `GET /api/applications/me` | Your application history with job titles and statuses |

### Track my applications — Applied / Pending / Accepted / Rejected (new)

Log in as the **demo applicant** (`demo.applicant@careerhub.co.za` / `DemoPass123!`).
The seed gives that account **four** applications, one in each friendly stage, so
every endpoint below returns data immediately. The friendly **stage** maps from the
internal status: `Submitted`→**Applied**, `UnderReview`/`Shortlisted`→**Pending**,
`Offered`→**Accepted**, `Rejected`→**Rejected**.

| Try | Endpoint | Expected |
|---|---|---|
| Full history | `GET /api/v1/applications/me` | Every application, newest first; **each row has both `status` and `stage`** |
| Filter by stage | `GET /api/v1/applications/me?stage=Accepted` | Only your **Accepted** (Offered) applications. `stage` is case-insensitive and accepts synonyms (`applied`, `pending`, `accepted`, `rejected`, plus `submitted`/`underview`/`offered`/`declined`…) |
| Summary | `GET /api/v1/applications/me/summary` | `{ "total":4, "applied":1, "pending":1, "accepted":1, "rejected":1 }` for the demo seed |
| Track one job | `GET /api/v1/applications/me/{jobListingId}` | That single application's `stage`, or **404** if you never applied to it |

> Copy a `jobListingId` from the `GET /api/v1/applications/me` response into the
> "track one job" endpoint. A random GUID returns **404**.

### Search applicants by qualification (new — employer/admin)

Log in as the **demo employer** (`demo.employer@takealot.co.za` / `DemoPass123!`).
This searches only the people who applied to **your** company's roles (the company
comes from your token — you cannot search another company's candidates).

| Try | Endpoint | Expected |
|---|---|---|
| By qualification | `GET /api/v1/applicants/search?qualification=kubernetes` | Paged envelope of your applicants whose **Qualifications/Headline** contain "kubernetes" (the demo applicant qualifies). `X-Total-Count` header set. |
| By experience + city | `GET /api/v1/applicants/search?minExperience=5&city=Cape Town` | Candidates with ≥ 5 years' experience whose city contains "Cape Town" |
| For one of your listings | `GET /api/v1/applicants/search?jobListingId={id}` | Only the people who applied to that specific listing of yours |
| Wrong role | `GET /api/v1/applicants/search` as an **applicant** | **403 Forbidden** — the endpoint is Employer-only |

Each result row carries the candidate's profile plus `applicationsToYourCompany`
(how many of your listings they applied to) and `latestStage` (the friendly stage
of their most recent application to you).

### Constraint proof via Scalar (Part 2)

Try `POST /api/jobs` (as employer) with `"salaryMin": 100000, "salaryMax": 50000`.
The API returns **`400 Bad Request`** (service validation), and the database
`ck_job_listings_salary_max_gt_min` check constraint would reject the same row even
if the service were bypassed. See `scripts/constraint-proofs.sql` for the raw-`psql`
proofs.

---

## 7. Useful psql commands

```bash
# open a shell in the database
docker exec -it careerhub24-pg psql -U postgres -d CareerHub24

# inside psql:
\d job_listings      -- show columns, check constraints and all Part 3 indexes
\d applications      -- show the application indexes
\q                   -- quit
```

To confirm the GIN index is actually used by search:

```sql
EXPLAIN ANALYZE
SELECT * FROM job_listings
WHERE "SearchVector" @@ to_tsquery('english', 'kubernetes')
  AND "Status" = 'Active' AND "ExpiresAt" > now();
-- look for "Bitmap Index Scan on ix_job_listings_search_vector"
```

---

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| `Npgsql ... connection refused` | `docker compose up -d` — Postgres isn't running |
| Scalar UI is `404` | You're not in Development; run `dotnet run` (the `http` profile sets `ASPNETCORE_ENVIRONMENT=Development`) |
| `401 Unauthorized` on a write | Log in (§5) and paste the Bearer token into Scalar's auth dialog |
| `403 Forbidden` on a write | You're logged in as the wrong role (employer-only vs applicant-only) |
| Port 5080 already in use | Stop the other process, or edit `Properties/launchSettings.json` |
| Want a clean dataset | `dotnet ef database drop --force` then `dotnet run` |

---

## 9. Where the OpenAPI document lives

Scalar renders the OpenAPI JSON that .NET generates. You can fetch it directly:

```
http://localhost:5080/openapi/v1.json
```

Import that URL into Postman, Insomnia, or another Scalar instance if you prefer a
different client.

---

# 10. Demonstrating the Assignment Proofs ("Proving It Works")

This is a click-by-click / command-by-command script for the 8 required proofs. Each
one lists **what it proves**, **how to run it**, and the **expected output** (captured
from this seeded database — yours will match aside from random GUIDs).

> **Two terminals help:** one running `dotnet run`, one for `psql`. Open a psql shell with:
> ```bash
> docker exec -it careerhub24-pg psql -U postgres -d CareerHub24
> ```
> The command blocks below prefix each statement with `docker exec … -c "…"` so you can
> paste them without opening a shell, but inside an interactive `psql` you'd paste only
> the SQL part.

---

## Proof 1 — Constraint enforcement (DB rejects, then API rejects)

**Proves:** the four Part 2 check constraints reject bad data *even when the API is
bypassed*, and the API returns `400` for the same data.

### 1a. Bypass the API — raw `psql` inserts (all four must ERROR)

```bash
# grab a real company / applicant / listing id to satisfy the foreign keys
CID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM companies LIMIT 1")
AID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM applicants LIMIT 1")
LID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM job_listings LIMIT 1")

# SalaryMin must be > 0
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',0,100,'Active',now(),now()+interval '10 days','$CID');"

# SalaryMax must be > SalaryMin
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',100,50,'Active',now(),now()+interval '10 days','$CID');"

# ExpiresAt must be after CreatedAt
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO job_listings (\"Id\",\"Title\",\"Description\",\"MinimumRequirements\",\"Location\",\"Type\",\"SalaryMin\",\"SalaryMax\",\"Status\",\"CreatedAt\",\"ExpiresAt\",\"CompanyId\") VALUES (gen_random_uuid(),'Bad','d','r','x','FullTime',100,200,'Active',now(),now()-interval '1 day','$CID');"

# SubmittedAt must not be in the future
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "INSERT INTO applications (\"JobListingId\",\"ApplicantId\",\"Status\",\"SubmittedAt\",\"CoverNote\") VALUES ('$LID','$AID','Submitted',now()+interval '2 days','x');"
```

**Expected — every insert is rejected:**
```
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_min_positive"
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_max_gt_min"
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_expires_after_created"
ERROR:  new row for relation "applications" violates check constraint "ck_applications_submitted_not_future"
```

### 1b. Same data through the API — `400 Bad Request` (in Scalar)

Log in as the **employer** (§5), then `POST /api/jobs` with:
```json
{ "title": "Bad", "description": "d", "minimumRequirements": "r", "location": "x",
  "type": "FullTime", "salaryMin": 100, "salaryMax": 50, "expiresAt": "2026-12-31T00:00:00Z" }
```
**Expected:** HTTP **`400 Bad Request`** with a Problem Details body — the `JobService`
validation rejects it before it ever reaches the database.

---

## Proof 2 — Index verification (`\d`)

**Proves:** every Part 3 index physically exists.

```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "\d job_listings"
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "\d applications"
```

**Expected — Indexes sections include:**
```
job_listings:
    "ix_job_listings_companyid_status"  btree ("CompanyId", "Status")
    "ix_job_listings_search_vector"     gin   ("SearchVector")
    "ix_job_listings_status_expiresat"  btree ("Status", "ExpiresAt")
applications:
    "ix_applications_applicantid_joblistingid"  btree ("ApplicantId", "JobListingId")
    "ix_applications_joblistingid_submittedat"  btree ("JobListingId", "SubmittedAt")
```
The `\d job_listings` output also shows the four check constraints and the generated
column: `"SearchVector" tsvector GENERATED ALWAYS AS (to_tsvector('english', ...)) STORED`.

---

## Proof 3 — EXPLAIN ANALYZE before vs after (active-listings query)

**Proves:** the index turns a full-table `Seq Scan` into an `Index Scan`.

### AFTER (index present — current state)
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now();"
```
**Expected:** `Bitmap Index Scan on ix_job_listings_status_expiresat` (no Seq Scan).

### BEFORE (drop the index, observe the Seq Scan, then put it back)
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"DROP INDEX ix_job_listings_status_expiresat; EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now();"

# restore it immediately
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"CREATE INDEX ix_job_listings_status_expiresat ON job_listings (\"Status\",\"ExpiresAt\");"
```
**Expected BEFORE:**
```
Seq Scan on job_listings j  (cost=0.00..1607.21 rows=603 ...)
   Filter: ((("Status")::text = 'Active'::text) AND ("ExpiresAt" > now()))
   Rows Removed by Filter: 5401
```
**What changed:** BEFORE, PostgreSQL reads all ~6 000 rows and discards 5 401 with a
filter (`Seq Scan`). AFTER, it walks `ix_job_listings_status_expiresat` to find only the
611 matching rows, then fetches just those heap pages (`Bitmap Index Scan` →
`Bitmap Heap Scan`). The README Part 4 section has both full plans side by side.

> The migration recreates this index on any fresh DB, but if you forget the restore
> step, `dotnet ef database drop --force && dotnet run` rebuilds everything.

---

## Proof 4 — Full-text search (exact matches + stemming + GIN index)

**Proves:** search returns only matching listings, stems words, and uses the GIN index.

### Matching listings (3 of the 6 000)
```bash
# via the API:
curl "http://localhost:5080/api/jobs/search?q=Kubernetes"
# or in psql:
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"SELECT j.\"Title\", c.\"Name\" FROM job_listings j JOIN companies c ON c.\"Id\"=j.\"CompanyId\" WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now() AND j.\"SearchVector\" @@ to_tsquery('english','kubernetes');"
```
**Expected:** exactly **3** rows — "Senior Platform Engineer (Kubernetes)" at **Takealot,
Yoco, Naspers**.

### Stemming (`LIKE` cannot do this)
```bash
curl "http://localhost:5080/api/jobs/search?q=sprint"
```
**Expected:** the **Scrum Master** listing, whose description says "…keeps **sprinting**
towards…". `sprint` and `sprinting` both stem to the lexeme `sprint`, so they match.

### GIN index confirmation
```bash
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c \
"EXPLAIN ANALYZE SELECT j.\"Id\" FROM job_listings j WHERE j.\"Status\"='Active' AND j.\"ExpiresAt\">now() AND j.\"SearchVector\" @@ to_tsquery('english','kubernetes');"
```
**Expected:** `Bitmap Index Scan on ix_job_listings_search_vector` (the GIN index), with
the term stemmed to `'kubernet'::tsquery`. **No Seq Scan.**

---

## Proof 5 — Compiled query confirmation

**Proves:** both hot paths use `EF.CompileAsyncQuery` as `static readonly` fields, and
the public signatures never changed.

```bash
grep -n "static readonly\|EF.CompileAsyncQuery" Repositories/JobListingRepository.cs
grep -n "static readonly\|EF.CompileAsyncQuery" Repositories/ApplicationRepository.cs
grep -n "GetActiveListingsAsync\|HasAppliedAsync" Repositories/IJobListingRepository.cs Repositories/IApplicationRepository.cs
```
**Expected:**
- `JobListingRepository.ActiveListingsQuery = EF.CompileAsyncQuery(...)` — a
  `private static readonly Func<...>` field; `GetActiveListingsAsync` enumerates it.
- `ApplicationRepository.HasAppliedQuery = EF.CompileAsyncQuery(...)` — same shape;
  `HasAppliedAsync` delegates to it.
- The interface methods are unchanged (no compiled-query types leak into the contract).

These two are the only compiled queries — the README Part 6 section justifies why each
is a genuine hot path (per-page-load board query; per-submission duplicate guard) and
estimates the call rate at 1 000 daily users.

---

## Proof 6 — Slow query interceptor

**Proves:** the interceptor logs every command over the configured threshold, and is
silent below it.

### Log everything (threshold 0)
1. In `appsettings.Development.json`, set `"SlowQueryThresholdMs": 0`.
2. `dotnet run`, then `curl http://localhost:5080/api/jobs` (or hit it in Scalar).
3. In the `dotnet run` console, every query appears as a **Warning**:
```
warn: CareerHub.Api.Infrastructure.SlowQueryInterceptor[0]
      Slow query: 12.4 ms (threshold 0 ms)
      SELECT j."Id", j."Title", ... FROM job_listings AS j
      WHERE j."Status" = 'Active' AND j."ExpiresAt" > @__now_0 ...
```

### Silent for normal queries (threshold 100)
4. Restore `"SlowQueryThresholdMs": 100`, restart, and `curl` again.
5. **Expected:** no `Slow query` warnings — every query finishes under 100 ms.

> The threshold is read from configuration, so this is a config change only — no code
> edit, no rebuild needed beyond restarting the app.

---

## Proof 7 — Raw SQL statistics (RANK() + per-status counts)

**Proves:** the `FromSql`/`SqlQuery` endpoint returns per-status counts and a correct
`RANK()`, with the most-applied listing as rank 1.

### Via the API (employer-scoped)
Log in as the **employer** (Takealot demo account), then call `GET /api/jobs/stats`.

### Via psql (the same query the repository runs)
```bash
TID=$(docker exec careerhub24-pg psql -U postgres -d CareerHub24 -tAc "SELECT \"Id\" FROM companies WHERE \"Name\"='Takealot'")
docker exec careerhub24-pg psql -U postgres -d CareerHub24 -c "
SELECT j.\"Title\", COUNT(a.\"ApplicantId\") AS total,
  COUNT(*) FILTER (WHERE a.\"Status\"='Submitted')  AS submitted,
  COUNT(*) FILTER (WHERE a.\"Status\"='UnderReview') AS underreview,
  COUNT(*) FILTER (WHERE a.\"Status\"='Shortlisted') AS shortlisted,
  COUNT(*) FILTER (WHERE a.\"Status\"='Rejected')    AS rejected,
  COUNT(*) FILTER (WHERE a.\"Status\"='Offered')     AS offered,
  RANK() OVER (ORDER BY COUNT(a.\"ApplicantId\") DESC) AS rank
FROM job_listings j LEFT JOIN applications a ON a.\"JobListingId\"=j.\"Id\"
WHERE j.\"CompanyId\"='$TID' AND j.\"Status\"='Active'
GROUP BY j.\"Id\", j.\"Title\" ORDER BY rank, j.\"Title\" LIMIT 5;"
```
**Expected:** the top row is the listing with the most applications at **rank 1** (the
seed gives Takealot a clear 18 / 11 / 5 spread), with the five status columns adding up
to the total. Listings with no applications share the last rank.

---

## Proof 8 — Connection pool configuration

**Proves:** pool sizes are set and justified.

```bash
grep -i "pool" appsettings.json
grep -i "pool" appsettings.Development.json
```
**Expected:**
- **Production** (`appsettings.json`): `Minimum Pool Size=5;Maximum Pool Size=30`.
- **Development** (`appsettings.Development.json`): `Minimum Pool Size=1;Maximum Pool Size=10`.

**Maximum Pool Size calculation (production):**
```
max_connections (PostgreSQL)        = 100
reserve for admin/monitoring        = 10
usable for the application          = 90
app instances                       = 3
=> Maximum Pool Size per instance   = 90 / 3 = 30
```
**Pool exhaustion behaviour:** when all 30 connections are checked out and another
request arrives, Npgsql does **not** error immediately — the request **waits** for a
connection up to `Timeout` (default 30 s). If one frees up, it proceeds; if the wait
elapses, the caller gets a `NpgsqlException` ("The connection pool has been exhausted").
**Observable symptom from the client:** requests hang and then fail with a 500/timeout
under sustained load — the signal to raise `Maximum Pool Size` or add capacity.

---

### Proof checklist at a glance

| # | Proof | Where |
|---|---|---|
| 1 | Check constraints reject bad data (psql + API 400) | psql + Scalar |
| 2 | All Part 3 indexes exist | `\d` |
| 3 | Seq Scan → Index Scan | `EXPLAIN ANALYZE` |
| 4 | Full-text: 3 matches, stemming, GIN index | API + `EXPLAIN ANALYZE` |
| 5 | Two compiled queries, signatures unchanged | `grep` |
| 6 | Interceptor logs > threshold, silent below | `dotnet run` console |
| 7 | RANK() + per-status counts, rank 1 correct | API + psql |
| 8 | Pool sizes set + calculation | `appsettings*.json` |

---

# 11. Full Scalar test script — start to finish (copy / paste)

This is the **one place** to test the whole API by hand, in order, from a cold start.
Every step says **which endpoint to open in Scalar**, **exactly what to paste**, and
**what you should see**. Follow it top to bottom.

> **Convention:** "Open `VERB /path`" means find that endpoint in the Scalar list on
> the left, click it, then use the **Test Request** panel on the right. Paste JSON
> into the **Body** box; type path/query values into the matching fields.

## Step 0 — Start everything

```bash
docker compose up -d          # PostgreSQL 17 on localhost:5544
dotnet run                    # migrates + seeds; serves http://localhost:5080
```

Wait for `Now listening on: http://localhost:5080`, then open:

```
http://localhost:5080/scalar/v1
```

> Fresh database first time? It auto-migrates (incl. the new `Qualifications`
> column) and seeds 10 companies, ~6 000 listings, 25 applicants **and the two demo
> accounts with demo applications**. Nothing else to set up.

## Step 1 — Browse the public board (no login needed)

| Open | Paste / fill | Expect |
|---|---|---|
| `GET /api/v1/jobs` | query: `page=1`, `pageSize=5` | `200`; a `data` array of 5 + `page/pageSize/totalCount/totalPages/links`. Note the **`X-Total-Count`** response header. |
| `GET /api/v1/jobs` | query: `location=Cape Town`, `sort=salaryMax`, `dir=desc`, `pageSize=10` | only Cape Town listings, highest salary first |
| `GET /api/v1/jobs/search` | query: `q=Kubernetes` | exactly **3** listings (Takealot, Naspers, Yoco) |
| `GET /api/v1/jobs/search` | query: `q=sprint` | the **Scrum Master** listing (stemming: matches "sprinting") |

👉 **Copy one listing's `id`** from any `GET /api/v1/jobs` response — you'll reuse it below as `<JOB_ID>`.

| Open | Paste / fill | Expect |
|---|---|---|
| `GET /api/v1/jobs/{id}` | path `id` = `<JOB_ID>` | full detail; response has an **`ETag`** header. Re-send with header `If-None-Match: <that etag>` → **304 Not Modified**. |

## Step 2 — Log in as the EMPLOYER and authorise Scalar

Open `POST /api/v1/auth/login`, paste this body, **Send**:

```json
{ "email": "demo.employer@takealot.co.za", "password": "DemoPass123!" }
```

➡️ Copy the **`token`** from the response. Click the **🔒 Authentication** area at the
top of Scalar, choose **Bearer**, and paste the token. Every request now sends
`Authorization: Bearer …`.

> You are now acting as a **Takealot recruiter** (the company is baked into the token).

## Step 3 — Employer actions

**Post a job** — open `POST /api/v1/jobs`, paste:

```json
{
  "title": "Senior Backend Engineer",
  "description": "Build and scale our order platform in C# and PostgreSQL.",
  "minimumRequirements": "Matric; degree in CS; 5+ years C#/.NET; valid SA work permit.",
  "location": "Sandton, Gauteng",
  "type": "FullTime",
  "salaryMin": 75000,
  "salaryMax": 110000,
  "expiresAt": "2026-12-31T00:00:00Z"
}
```
→ `201 Created` with the new `id`. (Company comes from your token, **not** the body.)

**Constraint/validation proof** — same endpoint, paste an inverted salary range:

```json
{ "title": "Bad", "description": "d", "minimumRequirements": "r", "location": "x",
  "type": "FullTime", "salaryMin": 100000, "salaryMax": 50000, "expiresAt": "2026-12-31T00:00:00Z" }
```
→ **`400 Bad Request`** (`problem+json`: "SalaryMax must be greater than SalaryMin").

**Pipeline stats** — open `GET /api/v1/jobs/stats` → per-status counts + `RANK()`;
Takealot's most-applied listing is **rank 1** (18 applications).

**PATCH a listing** — open `PATCH /api/v1/jobs/{id}`, path `id` = the `201` id from above, body:

```json
{ "location": "Remote (South Africa)", "salaryMax": 120000 }
```
→ `204 No Content` (only those two fields change).

## Step 4 — ⭐ Employer searches applicants by qualification (NEW)

Still authorised as the employer. Open `GET /api/v1/applicants/search`:

| Fill query | Expect |
|---|---|
| `qualification=kubernetes` | paged envelope of **your** applicants whose Qualifications/Headline contain "kubernetes" (the demo applicant is one). `X-Total-Count` set. Each row has `applicationsToYourCompany` + `latestStage`. |
| `minExperience=5` | only candidates with ≥ 5 years' experience |
| `city=Cape Town` | only candidates whose city contains "Cape Town" |
| `qualification=AWS&minExperience=3` | filters **compose** (AND) |

> **Security check:** this only ever returns people who applied to **Takealot**
> (your token's company). There is no parameter to search another company's pool.

## Step 5 — Log in as the APPLICANT and re-authorise

Open `POST /api/v1/auth/login`, paste:

```json
{ "email": "demo.applicant@careerhub.co.za", "password": "DemoPass123!" }
```
➡️ Copy the new **`token`**, open **🔒 Authentication → Bearer**, and **replace** the
employer token with this one. You are now the **job seeker**.

## Step 6 — ⭐ Applicant tracks their applications (NEW)

The demo applicant is seeded with **four** applications — one in each stage.

| Open | Paste / fill | Expect |
|---|---|---|
| `GET /api/v1/applications/me` | *(nothing)* | 4 rows; each has **`status`** (Submitted/…) **and `stage`** (Applied/Pending/Accepted/Rejected) |
| `GET /api/v1/applications/me` | query: `stage=Accepted` | only the **Accepted** (Offered) one |
| `GET /api/v1/applications/me` | query: `stage=Pending` | only UnderReview/Shortlisted ones |
| `GET /api/v1/applications/me/summary` | *(nothing)* | `{ "total":4, "applied":1, "pending":1, "accepted":1, "rejected":1 }` |
| `GET /api/v1/applications/me/{jobListingId}` | path = a `jobListingId` from the `me` list | that one application's `stage` |
| `GET /api/v1/applications/me/{jobListingId}` | path = a random GUID | **404 Not Found** |

**Apply to a new job** — open `POST /api/v1/jobs/{jobListingId}/applications`, path =
a `<JOB_ID>` you have **not** applied to yet, body:

```json
{ "coverNote": "I'm keen — my Kubernetes background fits this role." }
```
→ `201`. Re-send the exact same call → **`409 Conflict`** (one application per listing).
Now `GET /api/v1/applications/me` shows the new row with `stage` = **Applied**.

## Step 7 — Employer moves an application along the pipeline

You need a real `(jobListingId, applicantId)` pair. The easy source:
- **`applicantId`** = the **`userId`** field from the **applicant** login response (Step 5).
- **`jobListingId`** = any `jobListingId` from `GET /api/v1/applications/me` whose
  current `stage` is **Applied** or **Pending** (terminal Accepted/Rejected rows can't move).

Switch back to the **employer** token (Step 2), open
`PATCH /api/v1/applications/{jobListingId}/{applicantId}/status`, fill those two path
values, body:

```json
{ "status": "UnderReview" }
```
→ `204`. Re-check as the applicant (`GET /applications/me`) → that application is now
**Pending**. An **illegal** move (e.g. body `{ "status": "Submitted" }` on an already
`Offered`/Accepted row) → **`400`** naming both states.

## Step 8 — Cross-cutting headers (any endpoint)

| Check | How | Expect |
|---|---|---|
| Versioning | any `GET`, look at response headers | `api-supported-versions: 1.0` |
| Rate limit | spam `GET /api/v1/jobs/search?q=engineer` ~40× | eventually `429` + `Retry-After` |
| Health | open `GET /health/live` and `/health/ready` (no auth) | `200 Healthy` |

✅ That's the full surface. Every numbered ⭐ step is the new functionality added on
top of Assignment 3.1; the rest exercises the original 3.1 + 2.4 features.

---

# 12. Assignment summary

**CareerHub API** is a three-layer ASP.NET Core (.NET 10) + PostgreSQL job-board API
with a South-African dataset (ZAR salaries, real SA employers). It is built up across
two assignments plus the additions in this round.

### Assignment 2.4 — query optimisation & PostgreSQL features (foundation)
Database-enforced **check constraints**, strategic **composite + GIN indexes**, a
**stored generated `tsvector`** full-text column, **compiled queries** for hot paths,
a **slow-query interceptor**, a **raw-SQL window-function** statistics report
(`RANK()` + `COUNT(… ) FILTER`), and a tuned **Npgsql connection pool** — each proven
with `EXPLAIN ANALYZE` before/after and psql.

### Assignment 3.1 — API boundary hardening
**CORS** (named credentialed policy), **pagination** (`PagedResponse<T>` + HATEOAS
links + `X-Total-Count`), **filtering & sorting** (composable, AND semantics),
**PATCH** (partial listing update + an application-status **state machine**), URL-segment
**versioning** (`/api/v1`), **ETags** (`If-None-Match` → 304), and **rate limiting**
(global / search / apply / post-listing, with per-user partitioning). Plus extras:
ProblemDetails, global exception handler, response compression, idempotency keys,
health checks, correlation ids, and WebApplicationFactory integration tests.

### This round — applicant tracking & employer applicant search (new)
- **Applicant** can track applications by friendly **stage** — *Applied / Pending /
  Accepted / Rejected* — via `GET /applications/me` (+ `?stage=`),
  `/me/summary`, and `/me/{jobListingId}`. One `ApplicationStageMapper` keeps the
  list, filter and summary perfectly consistent.
- **Employer/admin** can search the candidates who applied to **their own** roles by
  **qualification**, experience and city via `GET /applicants/search` — paginated,
  and scoped server-side by the JWT's company so no one can read another company's
  pool. Backed by a new searchable `Qualifications` column (migration
  `AddApplicantQualifications`) and seeded demo data.

### Architecture & security in one line
Thin controllers → services (business rules) → repositories (EF Core: `IQueryable`
composition, `AsNoTracking`, flat projections, compiled/raw SQL) → `CareerHubDbContext`.
JWT bearer auth with **`Applicant`/`Employer`** roles; company-scoped endpoints read
the company from the **token, never the request**, so an account can only ever act on
or read its own data.

### How to run & verify
`docker compose up -d` then `dotnet run` (auto-migrate + seed) → Scalar at
`http://localhost:5080/scalar/v1`. `dotnet test` runs the integration suite (Postgres
must be up). Section 11 above is the hand-test script; section 10 is the 2.4 proofs.

### Demo accounts
| Role | Email | Password |
|---|---|---|
| Applicant | `demo.applicant@careerhub.co.za` | `DemoPass123!` |
| Employer (Takealot) | `demo.employer@takealot.co.za` | `DemoPass123!` |

---

# Assignment 3.2 — Testing & CI/CD: how to run everything

This part explains how the test suite is laid out, how to run each slice, and how
the CI pipeline ties it together. Full written rationale is in the
**"Assignment 3.2 — Testing & CI/CD"** section of `README.md`; captured run output
is in `docs/proof/`.

## 1. What was added

```
API.Tests/                         ← the new test project (next to CareerHub.Api.csproj)
├── Unit/Services/
│   ├── JobListingServiceTests.cs    (6 tests) salary/expiry guards, PATCH validation
│   └── ApplicationServiceTests.cs   (10 tests) legal/illegal status-transition matrix
├── Integration/
│   ├── WebApplicationFactoryFixture.cs   boots the real API (env "Testing", real DB)
│   └── JobsControllerTests.cs       (14 tests) HTTP pipeline: paging envelope, X-Total-Count,
│                                     version headers, unversioned fallback, ETag/304, auth, search
├── Repository/
│   ├── PostgreSqlContainerFixture.cs     throwaway postgres:16 via Testcontainers
│   └── JobListingRepositoryTests.cs (13 tests) paging, CHECK constraints, HasApplied compiled
│                                     query, FTS stemmer, partial PATCH
└── TestData/
    └── TestDataBuilders.cs          fluent builders for Company/JobListing/Applicant/Application

.github/workflows/ci.yml           ← GitHub Actions: build + test on every push/PR to main
appsettings.Testing.json           ← connection string + JWT key for the "Testing" environment
docs/proof/                        ← captured terminal output for each demo
```

**Total: 43 tests** — Unit 16 · Integration 14 · Repository 13.

## 2. Prerequisites for the tests

| Layer | Needs Docker? | Needs the dev Postgres (5544)? |
|---|---|---|
| Unit | no | no |
| Integration | no¹ | **yes** — `docker compose up -d` first |
| Repository | **yes** (Testcontainers pulls `postgres:16`) | no (it starts its own container) |

¹ Integration needs a reachable database, which `docker compose up -d` provides on
`localhost:5544`. The very first repository run also downloads the `postgres:16`
image (one-off, ~10s).

> ### ⚠️ If Docker is currently down
> Only the **unit** layer runs without Docker. With Docker stopped you can still:
> 1. Build the whole solution — `dotnet build CareerHub.slnx -c Release` (proves all
>    43 tests compile).
> 2. Run the 16 unit tests — `dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Unit"`.
> 3. Run the deliberate-failure demo (Proof 7 below) — it is unit-only.
>
> The **integration** and **repository** layers need a database, so run them once
> Docker is back (`docker compose up -d` for integration; the repository layer starts
> its own container). They also run automatically in **CI** on every push — that
> green run is your proof for those two layers while Docker is unavailable locally.

## 3. Run the tests

```bash
# 0. Make sure the dev database is up (needed by the integration tests):
docker compose up -d

# Unit only — fast, no infrastructure:
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Unit"

# Integration only — boots the real API against Postgres on 5544:
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Integration"

# Repository only — Testcontainers starts a throwaway postgres:16 (Docker required):
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Repository"

# Everything (all 43):
dotnet test API.Tests/API.Tests.csproj
```

Expected tail of a full run:

```
Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43
```

The `--filter "FullyQualifiedName~<Layer>"` switch matches on the namespace
(`API.Tests.Unit.*`, `API.Tests.Integration.*`, `API.Tests.Repository.*`), which is
how each slice is selected.

## 4. How each layer works

- **Unit** — `JobService` / `ApplicationService` are constructed with an
  **NSubstitute** double standing in for the repository. Nothing touches a database,
  so the business rules (inverted salary range, past expiry, the PATCH "only
  re-validate salary when a salary field changed" guard, and the legal status-
  transition state machine) are pinned in isolation and each test runs in
  milliseconds. The doubles also let the tests assert the write path
  (`AddAsync`/`SaveChangesAsync`) was *not* reached on a rejected request.
- **Integration** — `WebApplicationFactoryFixture` extends
  `WebApplicationFactory<Program>` (enabled by `public partial class Program;` at the
  bottom of `Program.cs`). It sets the environment to `Testing` so the host reads
  `appsettings.Testing.json`, and leaves `ConfigureTestServices` empty so the **real**
  configured database is used. Tests drive the API over HTTP: paging envelope, the
  ETag `If-None-Match` → `304` round-trip, `401` without a token, and full-text search.
- **Repository** — `PostgreSqlContainerFixture` starts a real `postgres:16` container,
  applies the **actual migrations** (check constraints + the stored generated
  `tsvector` column + GIN index), and the tests run the repository against it. Between
  tests the data is wiped with **Respawn**, so the suite is repeatable. This is the
  only layer that can prove the DB-level guarantees (SQLSTATE 23xxx on a bad salary
  range) and the `english` stemmer (search `"engineer"` finds `"Software Engineering
  Position"`).

## 5. The CI pipeline

`.github/workflows/ci.yml` runs one job, **`Build and Test CareerHub API`**, on every
push and pull request to `main`:

1. checkout → setup .NET `10.0.x`
2. `dotnet restore`
3. `dotnet build --no-restore --configuration Release`
4. `dotnet test --no-build --configuration Release --logger trx --collect:"XPlat Code Coverage"`
5. upload the `.trx` results as an artifact (always)

A `postgres:17` **service container** mapped to host port `5544` gives the
integration tests a database with no extra configuration (it matches
`appsettings.Testing.json`). The repository tests bring their own container via
Testcontainers using the runner's Docker.

### Make it a required check (branch protection)

GitHub → **Settings → Branches → Add branch ruleset → Target: `main` → Require
status checks to pass → select "Build and Test CareerHub API" → tick "Require
branches to be up to date before merging" → tick "Do not allow bypassing the above
settings"**. After this, nothing merges to `main` unless the full suite is green.
(The README explains *why* each tick matters.)

## 6. Reproduce the "prove it" demos

The captured output for unit-only, integration-only, repository-only, the full
suite, and the deliberate-failure demo (remove the PATCH salary guard → one test
fails → restore → green) all live in **`docs/proof/`**. Each file lists the exact
command to reproduce it.

## 7. One repo-hygiene note

`CareerHub.Api.csproj` uses the **Web SDK**, whose default glob compiles every `.cs`
under the project folder. Because both test projects live inside that folder, the
csproj explicitly excludes `API.Tests/**` and `CareerHub.Api.Tests/**` so their
sources don't get pulled into the API assembly (duplicate `Program.cs` collisions).

Three unrelated folders that previously sat here — `careerhub-api/` and `temp-3-1/`
(full working clones of this same project) and `ConferenceBookingAPI/` (a different
assignment) — were breaking the build via that same glob and have now been
**removed**, along with the stray root `run*.log` files. If any clone folder ever
reappears inside this directory, either delete it or add it back to the csproj
exclusions, or the Web-SDK glob will try to compile its duplicate `Program.cs`.

---

# 13. Demonstration walkthrough — "Proving It Works" (step by step)

This is the exact order to demo Assignment 3.2's seven required proofs. Each step
gives the command, what to say, and what success looks like. Items marked
**🐳 needs Docker** can't run while Docker is down — use the CI green run (Step 6) as
their proof in the meantime; everything else runs right now.

> Run all commands from the project root (the folder that contains `CareerHub.slnx`).

## Step 0 — Show it builds (sets the stage)

```bash
dotnet build CareerHub.slnx -c Release
```

**Say:** "The whole solution — API plus all three test layers — compiles in Release
with zero errors. That means all 43 tests at least compile against the real API."
**Success:** `Build succeeded. 0 Error(s)`.

## Step 1 — Unit tests (✅ runs without Docker)

```bash
dotnet test API.Tests/API.Tests.csproj -c Release --filter "FullyQualifiedName~Unit"
```

**Say:** "16 unit tests over the service layer with the repository mocked by
NSubstitute — no database. These pin the salary/expiry guards, the PATCH
conditional-validation rule, and the status-transition state machine."
**Success:** `Passed: 16`. (The *first* test may report a few hundred ms — that's
one-time assembly JIT warm-up, not the test logic; every test after it is single-digit ms.)

## Step 2 — Application status transitions (✅ runs without Docker)

```bash
# list the per-case [Theory] results so each From→To pair is visible
dotnet test API.Tests/API.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ApplicationServiceTests" --logger "console;verbosity=detailed"
```

**Say:** "One `[Theory]` enumerates every legal transition (Submitted→UnderReview,
UnderReview→Shortlisted/Rejected, Shortlisted→Offered/Rejected) and a second every
illegal one (Rejected→Submitted, Offered→Shortlisted, …). The illegal cases assert
the service throws and that `SaveChangesAsync` was **never** called."
**Success:** each `InlineData` case shows as its own passing line.

## Step 3 — The deliberate-failure demo (✅ runs without Docker)

This proves the tests actually test something. (Same idea as `docs/proof/05-failing-test-demo.md`.)

1. In [Services/JobService.cs](Services/JobService.cs#L75) `PatchAsync`, temporarily
   delete the salary line:
   ```csharp
   if (listing.SalaryMin is not null && listing.SalaryMax is not null && listing.SalaryMax <= listing.SalaryMin)
       throw new ArgumentException("SalaryMax must be greater than SalaryMin.");
   ```
2. Re-run the unit tests:
   ```bash
   dotnet test API.Tests/API.Tests.csproj -c Release --filter "FullyQualifiedName~Unit"
   ```
   **Success of the demo = a failure:** `PatchAsync_WhenOnlySalaryMinChanged_CallsValidation`
   fails with *"Expected an ArgumentException … none was thrown"*.
3. **Restore the line** and re-run — back to `Passed: 16`.

**Say:** "Remove the guard and exactly the test that protects it goes red; put it
back and the suite is green. The test is load-bearing, not decorative."

## Step 4 — Integration tests (🐳 needs Docker)

```bash
docker compose up -d                       # dev Postgres on localhost:5544
dotnet test API.Tests/API.Tests.csproj -c Release --filter "FullyQualifiedName~Integration"
```

**Say:** "14 tests boot the real API in-process with `WebApplicationFactory<Program>`
against a real database — `ConfigureTestServices` is empty on purpose. Highlight
`GetJobById_WithMatchingETag_Returns304`: it GETs a listing, reads the ETag,
re-requests with `If-None-Match`, and asserts `304 Not Modified` with an empty body —
a full conditional-GET round-trip through the live pipeline."
**Success:** `Passed: 14`.

## Step 5 — Repository / TestContainers tests (🐳 needs Docker)

```bash
dotnet test API.Tests/API.Tests.csproj -c Release --filter "FullyQualifiedName~Repository"
```

**Say:** "13 tests spin up a throwaway `postgres:16` via Testcontainers and run the
**real migrations** — so the `CHECK` constraints, the generated `tsvector` column and
the GIN index all exist. Point at: the constraint tests (PostgreSQL rejects an
inverted salary range with SQLSTATE 23xxx, bypassing all C# guards), and
`FullTextSearchAsync_ReturnsStemmedMatches` — searching *engineer* finds *Software
**Engineering** Position* via the english stemmer. **That last one could never pass on
the EF in-memory provider**, which has no `tsvector`, no GIN index and no text-search
config." The console log shows Testcontainers pulling/starting the image.
**Success:** `Passed: 13`.

## Step 6 — Full suite + CI (🐳 full suite needs Docker; CI is your stand-in now)

```bash
dotnet test API.Tests/API.Tests.csproj -c Release     # all 43, run twice to prove repeatability
```

**While Docker is down,** demo CI instead: push the branch, open the repo's
**Actions** tab, show the **`Build and Test CareerHub API`** job running, then the
green ✔. CI runs on `ubuntu-latest` with a `postgres` service container, so all three
layers — including the Docker-backed ones — go green there.
**Success:** `Passed: 43` locally, or a green checkmark in Actions.

## Step 7 — Branch protection (screenshot/talk-through)

GitHub → **Settings → Branches → Add ruleset → Target `main` → Require status checks
to pass → select "Build and Test CareerHub API" → tick "Require branches to be up to
date before merging" → tick "Do not allow bypassing the above settings"**.

**Say:** "This makes the green build a hard precondition for merging to `main`.
'Up to date before merging' is what catches the two-PRs-each-green-but-broken-when-
combined case; 'do not allow bypassing' applies it even to admins." (Full rationale
is in the README's Assignment 3.2 section.)

### One-line demo order

`build → unit → transitions → failing-demo (break & restore) → [Docker: integration →
repository → full] → CI green → branch-protection`. The first four and CI need no
Docker; the bracketed three run once Docker is back.
