# CareerHub API — Assignment 3.2

**REST API Maturity: CORS, Pagination, Filtering, PATCH, Versioning, ETags & Rate Limiting**

> Assignment 3.1 builds directly on the Assignment 2.4 data layer below. All 3.1
> work lives at the **API boundary** — the domain entities, database schema and
> service validation are unchanged. Jump to **[Assignment 3.1](#assignment-31--api-boundary-hardening)**
> for the new material; the 2.4 reference (query optimisation & PostgreSQL
> features) follows it.

---

## Assignment 2.4 foundation (unchanged)

**Query Optimisation & PostgreSQL Features**

This is the Assignment 2.3 three-layer CareerHub API hardened for production load.
It adds database-enforced check constraints, strategic indexes, a generated
`tsvector` full-text column, compiled queries for hot paths, a slow-query
interceptor, a raw-SQL window-function report, and a tuned Npgsql connection pool.

The data is South African end-to-end: ten real SA employers (Takealot, Discovery,
Standard Bank, Capitec, Naspers, Vodacom, Shoprite, Sasol, MTN, Yoco), listings
located in SA towns (Cape Town, Johannesburg, Polokwane, Gqeberha, Mbombela …),
and **all salaries in South African Rand (ZAR)**, monthly gross.

---

## Architecture (continued from 2.3)

```
Controllers/      JobsController — thin, one line per action, calls the service
Services/         JobService    — business rules (salary/expiry validation)
Repositories/     JobListingRepository, ApplicationRepository
                                — IQueryable composition, AsNoTracking, projections,
                                  compiled queries, raw SQL, full-text
Data/             CareerHubDbContext (constraints, indexes, tsvector), SeedData
Infrastructure/   SlowQueryInterceptor, ValidationExceptionHandler,
                  ServiceCollectionExtensions (composition root)
Models/           Company, Applicant, JobListing, Application
```

The service layer depends only on repository **interfaces**. Adding compiled
queries, raw SQL and full-text changed implementation files only — **no interface
signature changed** (Part 6 requirement).

> Model note vs 2.3: `JobListing` replaces the old `IsActive`/`PostedAt` pair with
> an explicit `Status` (`Draft`/`Active`/`Closed`) plus `CreatedAt` and `ExpiresAt`,
> which is what the 2.4 query layer filters on.

## Running it

```bash
docker compose up -d                      # Postgres 17 on localhost:5544
dotnet run                                # migrates + seeds ~6 000 listings, serves :5080
# Scalar UI: http://localhost:5080/scalar/v1
```

> **Routes are now versioned (Assignment 3.1, Part 6):** every route below is served
> under **`/api/v1/...`** (e.g. `/api/v1/jobs`). The unversioned paths in this 2.4
> table are kept for historical context — see the [Endpoint summary](#endpoint-summary)
> for the current routes.

| Endpoint | Purpose |
|---|---|
| `GET /api/jobs` | Active, unexpired job board (compiled query) |
| `GET /api/jobs/filter?title=&location=&type=` | Filter the active board by job name, location and/or type (`ILIKE` substring match; all params optional) |
| `GET /api/jobs/{id}` | Full detail of one listing — long description + minimum requirements + company info (`404` if missing) |
| `GET /api/jobs/company/{companyId}` | An employer's own postings |
| `GET /api/jobs/search?q={term}` | Full-text search (GIN index, stemmed) |
| `GET /api/jobs/stats` | Per-status application counts + `RANK()` for the **authenticated employer's own** company (companyId from token, not a query param) |
| `POST /api/jobs` | Create a listing (validated; employer-only) |
| `POST /api/auth/register/applicant` · `register/employer` · `login` | Auth |
| `POST /api/jobs/{id}/applications` | Apply to a listing (applicant-only) |
| `GET /api/applications/me` | The signed-in applicant's application history |
| `GET /api/companies` | All companies with active-listing counts |

**`/filter` vs `/search`** — `/filter` does exact case-insensitive substring matching
(`ILIKE`) on title/location, ideal for faceted browsing ("jobs in Cape Town").
`/search` does stemmed full-text matching over Title + Description via the GIN index,
so `sprint` also finds `sprinting`. They are complementary, not duplicates.

> **Note on stats:** the spec's `GET /api/jobs/stats?companyId={id}` is implemented
> as `GET /api/jobs/stats` with the company taken from the employer's JWT, so one
> employer cannot read another company's pipeline. The repository method still takes
> `companyId` exactly as the spec requires — only the controller's source of that id
> changed, for security.

Seeded volume: **10 companies, 25 applicants, 6 004 job listings** (~25 % Active,
~10 % Active-and-unexpired), **85 applications** across all five statuses.

---

# Part 1 — Written Decisions

### 1. Constraint placement

The service validates `SalaryMin <= SalaryMax` in C#, but that check only runs for
writes that travel **through `JobService.CreateAsync`**. The specific bypass
scenario: during an incident an engineer runs a manual `UPDATE job_listings SET
"SalaryMin" = ...` in psql, or a nightly bulk-import / data-migration script
`COPY`s rows straight into the table, or a second service is later written against
the same database. None of these call the C# method, so none of them see the
validation.

If the database has no constraint, the consequence is **silent permanent data
corruption**: a row with `SalaryMin = 90000, SalaryMax = 50000` is accepted and
sits there. Every downstream consumer inherits the bad data — the job board shows
"R90 000 – R50 000", salary-range filters return nonsense, analytics averages are
skewed, and nobody gets an error at write time to tell them something went wrong.
A `CHECK` constraint makes the rule an invariant of the table itself: it holds no
matter who writes, so the corrupt row is rejected at the source.

### 2. Index column ordering

| Query | Index | Leading column | Why |
|---|---|---|---|
| Active for a company: `WHERE CompanyId = X AND Status = 'Active'` | `ix_job_listings_companyid_status (CompanyId, Status)` | **CompanyId** | `CompanyId` is the high-selectivity equality (one of ten companies ≈ 600 rows). Lead with it so the index seeks straight to that company's slice, then `Status` narrows within it. |
| Expiring soon, all companies: `WHERE ExpiresAt < @t AND Status = 'Active'` | `ix_job_listings_status_expiresat (Status, ExpiresAt)` | **Status** (equality) then **ExpiresAt** (range) | The rule "equality columns before range columns" — once a range scan starts on the leading column, the index can no longer use later columns for seeking. `Status` is the equality predicate both queries share, so it leads; the `ExpiresAt` range then scans contiguously inside `Status = 'Active'`. |

PostgreSQL can only seek on a composite index using a **leftmost prefix** of its
columns. A B-tree is sorted by the first column, then the second within equal
first values, and so on. So `ix_job_listings_companyid_status` accelerates
`WHERE CompanyId = X` and `WHERE CompanyId = X AND Status = Y`, but a query that
filters **only on `Status`** (the non-leftmost column) cannot seek with it — the
`Status` values are scattered across the whole index, so the planner falls back to
a sequential scan (or a different index). That is exactly why the all-companies
"expiring soon" query needs a **separate** `(Status, ExpiresAt)` index rather than
reusing the company index.

### 3. Identifying hot paths (compiled-query candidates)

**`JobListingRepository.GetActiveListingsAsync`** — the public job board's landing
query. It runs on every visit to the CareerHub home page and every "browse jobs"
pagination request, for signed-in users *and* anonymous visitors. Frequency
matters here because the cost EF pays per call is the LINQ→expression-tree→SQL
generation, and this query carries the heaviest expression tree in the app (a
filtered, ordered, projected join). Compiling it once amortises that cost across
the single highest-volume call site in the system.

**`ApplicationRepository.HasAppliedAsync`** — the duplicate-application guard. It
fires on **every** application submission (before the insert) and again every time
a listing-detail page renders for a signed-in seeker (to show the "Already
applied" badge). It is a tiny, fixed, parameterised point lookup that runs far
more often than it changes — the textbook compiled-query case: high call frequency
× a query shape that never varies means the saved per-call planning overhead
compounds.

(Estimated call rates for a 1 000-DAU deployment are in **Part 6**.)

### 4. FromSql scope

The statistics query is impractical in LINQ because it needs two PostgreSQL
features EF Core's LINQ provider **cannot translate**:

1. **`RANK() OVER (ORDER BY COUNT(*) DESC)`** — a window function computed over an
   aggregate. EF Core has no LINQ surface for `RANK()`/`OVER`, and you cannot rank
   by an aggregate you are simultaneously grouping by without a window.
2. **`COUNT(*) FILTER (WHERE status = '…')`** — conditional aggregation. Expressing
   five per-status counts in LINQ would mean five correlated sub-queries or five
   passes, which EF either refuses to translate or turns into a far worse plan.

Both are standard SQL that PostgreSQL executes in a single grouped pass, so the
query is written with `db.Database.SqlQuery<T>` (Part 8).

---

# Part 2 — Database Constraints

All four constraints are declared with the Fluent API in
`CareerHubDbContext.OnModelCreating` (no raw migration SQL, no data annotations)
and scaffolded into migration `AddSalaryAndExpiryCheckConstraints`.

| Constraint | Table | SQL | Business rule |
|---|---|---|---|
| `ck_job_listings_salary_min_positive` | job_listings | `"SalaryMin" IS NULL OR "SalaryMin" > 0` | A salary floor, if given, must be a real positive Rand amount. `NULL` allowed ("market related"). |
| `ck_job_listings_salary_max_gt_min` | job_listings | `"SalaryMax" IS NULL OR "SalaryMin" IS NULL OR "SalaryMax" > "SalaryMin"` | If both bounds are given, max must exceed min. `NULL` guards keep an absent/half-open range legal. |
| `ck_job_listings_expires_after_created` | job_listings | `"ExpiresAt" > "CreatedAt"` | A listing cannot expire before it was created. |
| `ck_applications_submitted_not_future` | applications | `"SubmittedAt" <= now()` | An application cannot be backdated into the future. |

### Constraint decisions (scenario + corruption if absent)

- **salary_min_positive** — *Bypass:* a CSV importer maps an empty cell to `0` or a
  currency-parse bug yields a negative. *Corruption:* "R0 / month" or "–R5 000"
  listings render on the board and break salary filters/averages.
- **salary_max_gt_min** — *Bypass:* an admin hot-fix `UPDATE` in psql swaps the two
  values. *Corruption:* an inverted range no UI can render sensibly and that
  silently drops the row out of `min ≤ x ≤ max` filters.
- **expires_after_created** — *Bypass:* a back-dated migration sets `ExpiresAt` from
  a stale column. *Corruption:* a listing that is "born expired" — never visible on
  the active board, invisible to its own employer, with no error to explain why.
- **submitted_not_future** — *Bypass:* a replay/import job stamps `SubmittedAt` from
  an unsynced clock. *Corruption:* applications ordered "newest first" surface
  future-dated rows permanently at the top of every employer dashboard.

### Proof — psql rejects each violation (API bypassed)

```text
1) INSERT … SalaryMin = -5000 →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_min_positive"
DETAIL: Failing row contains (…, -5000.00, 80000.00, Active, …, 'bad':1 'salari':2 'x':3).

2) INSERT … SalaryMin = 50000, SalaryMax = 40000 →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_salary_max_gt_min"

3) INSERT … ExpiresAt = now() - 1 day →
ERROR:  new row for relation "job_listings" violates check constraint "ck_job_listings_expires_after_created"

4) INSERT … SubmittedAt = now() + 1 day →
ERROR:  new row for relation "applications" violates check constraint "ck_applications_submitted_not_future"
```

> The `DETAIL` line in (1) even shows the **auto-generated `tsvector`**
> (`'bad':1 'salari':2 'x':3`) — proof the Part 5 generated column is computed by
> PostgreSQL on every insert. A valid row with `SalaryMin/Max = NULL` still inserts
> (`INSERT 0 1`), confirming the `NULL` guards work.

### Proof — the API rejects the same data with the correct status

```text
POST /api/jobs  { salaryMin: 90000, salaryMax: 50000, … }
→ HTTP 400
  { "title":"Validation failed", "status":400,
    "detail":"SalaryMax must be greater than SalaryMin." }

POST /api/jobs  { salaryMin: 50000, salaryMax: 90000, … }
→ HTTP 201   { "id":"949f692b-…" }
```

The script `scripts/constraint-proofs.sql` reproduces all four psql rejections.

---

# Part 3 — Index Strategy

Generated in a **single** migration `AddJobListingAndApplicationIndexes`. Every
index maps to a real repository query — none are speculative.

| Index | Method | Supports | Column-order rationale |
|---|---|---|---|
| `ix_job_listings_status_expiresat` | B-tree | `GetActiveListingsAsync` (most frequent query, every board load) | Equality (`Status`) leads, range (`ExpiresAt`) follows — see Part 1.2. |
| `ix_job_listings_companyid_status` | B-tree | `GetByCompanyAsync` (employer's own postings) | High-selectivity equality (`CompanyId`) leads. |
| `ix_job_listings_search_vector` | **GIN** | `SearchAsync` full-text over `SearchVector` | GIN is the index type for `tsvector @@ tsquery`; declared with `HasMethod("gin")`. |
| `ix_applications_applicantid_joblistingid` | B-tree | `HasAppliedAsync` (every submission / detail view) | `ApplicantId` leads — the PK leads with `JobListingId`, so it does **not** cover an applicant-first probe. |
| `ix_applications_joblistingid_submittedat` | B-tree | `GetForListingAsync` (employer dashboard, newest first) | `JobListingId` then `SubmittedAt` so rows come back already ordered — no `Sort` node. |

### What each composite can and cannot accelerate

- `ix_job_listings_status_expiresat (Status, ExpiresAt)` **accelerates**
  `WHERE Status = …`, `WHERE Status = … AND ExpiresAt <> …`, and `ORDER BY
  ExpiresAt` within a status. It **cannot** accelerate a query filtering on
  `ExpiresAt` alone — `ExpiresAt` is the non-leftmost column.
- `ix_job_listings_companyid_status (CompanyId, Status)` **accelerates**
  `WHERE CompanyId = …` and `WHERE CompanyId = … AND Status = …`. It **cannot**
  serve `WHERE Status = …` alone, which is precisely why the board query gets its
  own `(Status, ExpiresAt)` index.

### Honest note on the application indexes

The composite PK `(JobListingId, ApplicantId)` already indexes the employer
"applications for a listing" lookup via its leftmost column, so
`ix_applications_joblistingid_submittedat` is justified **only** by the added
`SubmittedAt` — it lets the dashboard's "newest first" query skip a sort, which the
bare PK cannot. `HasAppliedAsync` filters on both keys (equality), so the PK could
serve it too; the applicant-first index also serves "all of an applicant's
applications", which the PK cannot.

### Proof — `\d` shows every index

```text
job_listings indexes:
  "PK_job_listings" PRIMARY KEY, btree ("Id")
  "ix_job_listings_companyid_status"  btree ("CompanyId", "Status")
  "ix_job_listings_search_vector"     gin   ("SearchVector")
  "ix_job_listings_status_expiresat"  btree ("Status", "ExpiresAt")

applications indexes:
  "PK_applications" PRIMARY KEY, btree ("JobListingId", "ApplicantId")
  "ix_applications_applicantid_joblistingid"  btree ("ApplicantId", "JobListingId")
  "ix_applications_joblistingid_submittedat"  btree ("JobListingId", "SubmittedAt")
```

---

# Part 4 — EXPLAIN ANALYZE

Both plans were captured on the seeded **6 004-row** table (593 rows match
Active + unexpired ≈ 10 %). "Before" = the relevant index dropped; "after" =
recreated.

## Query 1 — `GetActiveListingsAsync`

**SQL EF Core generates** (captured from the slow-query interceptor):

```sql
SELECT j."Id", j."Title", j."Location", j."Type"::text, j."SalaryMin", j."SalaryMax",
       j."Status"::text, j."CreatedAt", j."ExpiresAt", j."CompanyId", c."Name", c."City"
FROM job_listings AS j
INNER JOIN companies AS c ON j."CompanyId" = c."Id"
WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now
ORDER BY j."CreatedAt" DESC
```

**BEFORE** (`ix_job_listings_status_expiresat` dropped):

```text
Sort  (actual time=7.819..7.913 rows=593)
  Sort Key: j."CreatedAt" DESC
  ->  Nested Loop  (actual time=0.058..6.645 rows=593)
        ->  Seq Scan on job_listings j  (actual time=0.020..5.753 rows=593)
              Filter: (("Status")::text = 'Active' AND "ExpiresAt" > now())
              Rows Removed by Filter: 5411
        ->  Memoize → Index Scan using "PK_companies" on companies c
Execution Time: 8.131 ms
```

**AFTER** (index recreated):

```text
Sort  (actual time=4.831..4.913 rows=593)
  Sort Key: j."CreatedAt" DESC
  ->  Nested Loop  (actual time=0.478..3.834 rows=593)
        ->  Bitmap Heap Scan on job_listings j  (actual time=0.414..2.737 rows=593)
              Recheck Cond: (("Status")::text = 'Active' AND "ExpiresAt" > now())
              Heap Blocks: exact=367
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
                    Index Cond: (("Status")::text = 'Active' AND "ExpiresAt" > now())
        ->  Memoize → Index Scan using "PK_companies" on companies c
Execution Time: 5.253 ms
```

**What changed.** The leaf node flipped from a **Seq Scan** to a **Bitmap Index
Scan** feeding a **Bitmap Heap Scan**:

- **Seq Scan** (before): PostgreSQL read **all 6 004 rows** and applied the filter
  row-by-row, throwing away 5 411 (`Rows Removed by Filter: 5411`). Work is
  proportional to the whole table.
- **Bitmap Index Scan** (after): PostgreSQL walked `ix_job_listings_status_expiresat`
  to build a bitmap of *only* the 593 matching row locations…
- **Bitmap Heap Scan**: …then fetched just those rows from the 367 heap pages that
  contain them (`Heap Blocks: exact=367`). It never touches the 5 411 non-matching
  rows.

Concretely: **scanning all 6 004 rows became scanning 593 matching rows**, and the
leaf node's time dropped from ~5.75 ms to ~2.7 ms (end-to-end 8.1 ms → 5.3 ms). The
gap widens with table size — the Seq Scan grows with the table, the index scan
grows only with the number of matches.

## Query 2 — Full-text search (`SearchAsync`)

**SQL EF Core generates:**

```sql
SELECT j."Id", j."Title", j."Location", j."Type"::text, …, c."Name", c."City"
FROM job_listings AS j
INNER JOIN companies AS c ON j."CompanyId" = c."Id"
WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now
  AND j."SearchVector" @@ to_tsquery('english', @tsQuery)
```

**BEFORE** (`ix_job_listings_search_vector` GIN dropped), searching `Kubernetes`:

```text
Nested Loop  (actual time=12.389..13.616 rows=3)
  ->  Seq Scan on companies c (rows=10)
  ->  Materialize
        ->  Bitmap Heap Scan on job_listings j  (actual time=12.335..13.532 rows=3)
              Recheck Cond: ("Status" = 'Active' AND "ExpiresAt" > now())
              Filter: ("SearchVector" @@ '''kubernet'''::tsquery)
              Rows Removed by Filter: 590
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
Execution Time: 13.763 ms
```

**AFTER** (GIN recreated):

```text
Hash Join  (actual time=0.434..0.471 rows=3)
  ->  Bitmap Heap Scan on job_listings j  (actual time=0.305..0.336 rows=3)
        Recheck Cond: ("SearchVector" @@ '''kubernet'''::tsquery
                       AND "Status" = 'Active' AND "ExpiresAt" > now())
        Heap Blocks: exact=3
        ->  BitmapAnd
              ->  Bitmap Index Scan on ix_job_listings_search_vector (rows=3)
                    Index Cond: ("SearchVector" @@ '''kubernet'''::tsquery)
              ->  Bitmap Index Scan on ix_job_listings_status_expiresat (rows=593)
  ->  Hash → Seq Scan on companies c (rows=10)
Execution Time: 0.685 ms
```

**What changed.** Without the GIN index, the `@@` match could only be a **Filter**:
PostgreSQL used the B-tree to fetch the 593 active rows, then evaluated the tsvector
match on each, discarding 590 (`Rows Removed by Filter: 590`). With the GIN index,
the plan adds a **Bitmap Index Scan on `ix_job_listings_search_vector`** and
**`BitmapAnd`**s it with the status bitmap — so the text match is resolved *in the
index* down to exactly 3 row locations before touching the heap (`Heap Blocks:
exact=3`). Execution dropped **13.76 ms → 0.685 ms (~20×)**. The plan shows the
required `Bitmap Index Scan` on the GIN index, not a Seq Scan.

---

# Part 5 — Full-Text Search

- `JobListing.SearchVector` is a **stored generated column**, declared with
  `entity.HasGeneratedTsVectorColumn(j => j.SearchVector, "english", j => new {
  j.Title, j.Description })`. `\d job_listings` confirms it:
  `generated always as (to_tsvector('english', "Title" || ' ' || "Description")) stored`.
  It is computed by PostgreSQL, never in C#.
- GIN index `ix_job_listings_search_vector` from Part 3.
- `SearchAsync` matches the stored column against `EF.Functions.ToTsQuery("english", …)`
  and returns the **same `JobListingResponse` projection** as `GetActiveListingsAsync`,
  filtered to Active + unexpired.
- The controller action is one line: `=> await jobs.SearchAsync(q, ct);`.

> **Why `ToTsQuery` but not a query-time `ToTsVector`.** The brief lists both
> `EF.Functions.ToTsVector` and `ToTsQuery`, but those instructions pull against the
> harder requirement that the plan show a **Bitmap Index Scan, not a Seq Scan**. The
> GIN index covers the *stored* `SearchVector` column, so to use it the query must
> match that column (`SearchVector @@ to_tsquery(...)`). Calling
> `EF.Functions.ToTsVector("english", Title + " " + Description)` *at query time* would
> recompute a fresh vector for **every row** — which the GIN index cannot answer,
> forcing a Seq Scan and failing the proof. The vector *is* produced by
> `to_tsvector('english', …)` — once, in the database, as the generated column
> (`HasGeneratedTsVectorColumn`) — which is the index-correct place for it. So
> `ToTsQuery` is used at query time; the `to_tsvector` work lives in the column
> definition, exactly as a stored generated tsvector is meant to be used.

### Proof — exactly 3 matches

`GET /api/jobs/search?q=Kubernetes` returns exactly the **3** seeded
"Senior Platform Engineer (Kubernetes)" listings — at **Takealot, Yoco and
Naspers** — and nothing else. The EXPLAIN AFTER plan above confirms the **GIN
Bitmap Index Scan** was used.

### Proof — stemming (which `LIKE` cannot do)

`GET /api/jobs/search?q=sprint` returns the **Scrum Master** listing, whose
description contains "…keeps **sprinting** towards…" — never the literal word
"sprint". The `english` configuration stems both `sprint` and `sprinting` to the
lexeme `sprint` (visible in the plans as `'''sprint'''::tsquery`), so they match.
A `LIKE '%sprint%'` would also coincidentally match here, but it would **fail** the
reverse case and on inflections like "ran"/"running" — full-text stemming is
linguistic, `LIKE` is substring.

---

# Part 6 — Compiled Queries

Two compiled queries, each a `private static readonly Func<…>` built with
`EF.CompileAsyncQuery`, delegated to by the existing method:

```csharp
// JobListingRepository
private static readonly Func<CareerHubDbContext, DateTime, IAsyncEnumerable<JobListingResponse>>
    ActiveListingsQuery = EF.CompileAsyncQuery(…);

// ApplicationRepository
private static readonly Func<CareerHubDbContext, Guid, Guid, Task<bool>>
    HasAppliedQuery = EF.CompileAsyncQuery(…);
```

The public interface methods `GetActiveListingsAsync` and `HasAppliedAsync` are
**unchanged** — the compiled `Func` is an internal detail. (`now` is passed as a
*parameter*, not captured, so the compiled active query still filters against the
current instant on every call.)

### Hot-path justification + call-rate estimate (1 000 DAU)

**`GetActiveListingsAsync`** — the job board landing/pagination query.
Estimate: 1 000 daily users × ~3 board loads each = ~3 000 loads/day, plus
anonymous traffic. Over a ~10-hour active window that is **~5 loads/min average**,
and SA traffic peaks (lunch-time, post-work) push it to **~20–30/min**. It is the
single highest-volume query and carries the heaviest expression tree — compiling
removes the tree-build + SQL-generation cost from every one of those calls.

**`HasAppliedAsync`** — duplicate-application guard + "Already applied" badge.
Estimate: ~2 000 submissions/day (the guard) **plus** every signed-in listing-detail
view (~10 listings viewed/user/day × 1 000 = ~10 000) ⇒ **~12 000 calls/day ≈
20/min average**, higher at peak. A tiny, unchanging point lookup at that frequency
is the ideal compiled-query target.

**Not compiled:** `SearchAsync`, `GetByCompanyAsync`, `GetApplicationStatsAsync`.
The stats report runs maybe a few times per hour from an employer dashboard — far
too infrequent to justify a static cached delegate, and its raw-SQL/`SqlQuery`
shape isn't a LINQ tree to compile anyway. Adding a compiled query there would be
complexity for no measurable gain.

---

# Part 7 — Slow Query Interceptor

`Infrastructure/SlowQueryInterceptor.cs` extends `DbCommandInterceptor`, overrides
both `ReaderExecuted` (sync) and `ReaderExecutedAsync`, and logs at **Warning** when
`eventData.Duration` exceeds the threshold, including elapsed ms and the full SQL.
The threshold is read from `SlowQueryThresholdMs` in config (**default 100 ms** when
absent). It is registered **Singleton** and wired into the DbContext via
`AddInterceptors` inside `AddCareerHubInfrastructure` (an `IServiceCollection`
extension), not directly in `Program.cs`.

### Proof — threshold 0 logs every query

With `"SlowQueryThresholdMs": 0` in `appsettings.Development.json`, every command is
logged:

```text
warn: …SlowQueryInterceptor[0]  Slow query: 85.0 ms (threshold 0 ms)
      SELECT j."Id", … FROM job_listings AS j INNER JOIN companies …
      WHERE j."Status" = 'Active' AND j."ExpiresAt" > @now ORDER BY j."CreatedAt" DESC
warn: …SlowQueryInterceptor[0]  Slow query: 10.4 ms (threshold 0 ms)
      SELECT j."Id", … WHERE … AND j."SearchVector" @@ to_tsquery('english', @tsQuery)
```

### Proof — threshold 100 is silent for normal queries

Restored to `100`, hitting `GET /api/jobs` and `GET /api/jobs/search` produces
**zero** new warnings (both run in <100 ms). The only warning in the whole run is a
single startup line:

```text
Slow query: 170.4 ms (threshold 100 ms)
SELECT "MigrationId", "ProductVersion"
```

That is the **first** database round-trip after launch — a *cold* connection paying
TCP + TLS + auth. It is not a normal query, and it is the real-world motivation for
the **Minimum Pool Size** in Part 9.

---

# Part 8 — Raw SQL with `FromSql`

`GetApplicationStatsAsync(Guid companyId)` uses `db.Database.SqlQuery<JobListingStatsResponse>`
with an interpolated `FormattableString`. The SQL uses `RANK() OVER (ORDER BY
COUNT(*) DESC)` for the rank and `COUNT(*) FILTER (WHERE "Status" = '…')` for the
per-status breakdown, with `companyId` passed as a parameter.

### Proof — `GET /api/jobs/stats?companyId={Takealot}`

```json
[
  { "title":"Lead QA Engineer",            "totalApplications":18,
    "submitted":4,"underReview":3,"shortlisted":5,"rejected":2,"offered":4, "rank":1 },
  { "title":"Intermediate Frontend Engineer","totalApplications":11,
    "submitted":3,"underReview":2,"shortlisted":2,"rejected":1,"offered":3, "rank":2 },
  { "title":"Lead DevOps Engineer",        "totalApplications":5,
    "submitted":2,"underReview":2,"shortlisted":0,"rejected":0,"offered":1, "rank":3 },
  { "title":"Graduate Backend Engineer",   "totalApplications":0, …, "rank":4 },
  …
]
```

The per-status counts sum to the total (4+3+5+2+4 = 18 ✓) and `RANK()` correctly
makes the most-applied-to listing **rank 1**, with the zero-application listings
tying at rank 4.

### Parameterisation safety

- **Interpolation inside `SqlQuery<T>($"… {companyId} …")` is injection-safe**
  because EF Core treats the `FormattableString`'s holes as **parameters** — it
  sends `WHERE "CompanyId" = $1` plus the value `$1` separately, so the value is
  never parsed as SQL.
- **`string.Format`/`+` concatenation is unsafe** because it splices the value into
  the SQL *text* before EF sees it, so EF runs the whole string verbatim and a
  crafted input (e.g. `'… ; DROP TABLE applications; --`) executes as SQL.

(Here `companyId` is a `Guid` so it could not carry an injection payload anyway —
but the rule must hold for the `string` parameters this pattern will meet next.)

---

# Part 9 — Connection Pool Configuration

Pool settings live in the connection string.

**Maximum Pool Size = 30** — calculation:

```
PostgreSQL max_connections      = 100
reserve for admin / monitoring  = 10
                                ------
usable by the application       = 90
application instances           = 3
Maximum Pool Size per instance  = 90 / 3 = 30
```

3 instances × 30 = 90 ≤ 90, so even at full saturation the app never starves the
admin/monitoring reserve.

**Minimum Pool Size = 5 (production)** — keeps 5 connections open per instance
through quiet periods so the first requests after a lull skip the cold-connection
penalty. We measured that penalty directly: the first DB round-trip after launch
took **170 ms** (TCP + TLS + auth) versus <10 ms once warm (Part 7). 5 × 3 = 15 warm
connections is comfortably within the 90 budget.

**Development differs** (`appsettings.Development.json`): `Minimum Pool Size=1`,
`Maximum Pool Size=10`. The constraints are different — one developer, one
instance, a laptop Postgres — so a large warm pool would just waste connections.

### What happens when the pool is exhausted

When all 30 connections are checked out and a new request needs one, Npgsql does
**not** open a 31st — it **blocks the request**, queuing it until a connection is
returned to the pool. If none frees within the connection `Timeout` (default 15 s),
Npgsql throws *"The connection pool has been exhausted…"*.

**Observable symptom from the client:** requests that normally return in tens of
milliseconds suddenly **hang** for up to ~15 s; under sustained overload they then
fail with **HTTP 500** as the timeout fires. Latency percentiles spike and error
rate climbs while CPU may look idle — the classic signature of pool exhaustion
rather than slow queries.

---

# Proving It Works — checklist

| # | Requirement | Where |
|---|---|---|
| 1 | Each constraint rejected in psql **and** by the API (correct status) | Part 2 |
| 2 | `\d job_listings` / `\d applications` show every index | Part 3 |
| 3 | `GetActiveListingsAsync` plan: before = Seq Scan, after ≠ Seq Scan | Part 4 Q1 |
| 4 | Search term in exactly 3 listings; GIN used; `sprint` matches `sprinting` | Parts 4 Q2, 5 |
| 5 | Both compiled queries are `static readonly` fields; interfaces unchanged | Part 6 |
| 6 | Interceptor logs all at threshold 0; silent at 100 | Part 7 |
| 7 | Stats endpoint returns per-status counts + correct rank 1 | Part 8 |
| 8 | Connection string pool settings + Max Pool Size arithmetic | Part 9 |

---

## Suggested commits

1. Add check constraints for salary range and expiry date with migration
2. Add indexes for active-listing query, company scope, and application hot paths
3. Add GIN index and full-text search implementation
4. Implement compiled queries for hot-path repository methods
5. Add slow-query interceptor with configurable threshold
6. Add application-statistics endpoint using FromSql with window function
7. Configure Npgsql connection-pool settings

---

# Assignment 3.1 — API Boundary Hardening

Everything below is new for 3.1. Naming maps to the 2.4 entities: the assignment's
**PostedAt** is `JobListing.CreatedAt`, its **EmploymentType** is `JobListing.Type`
(`JobType`), and the listing lifecycle is `JobListing.Status` (`ListingStatus`).
`Application` has a **composite key** `(JobListingId, ApplicantId)` — there is no
surrogate id — so the per-application routes address that pair (documented in
Part 5B / Part 7 below) rather than inventing a column, which the ground rules
forbid.

## Part 2 — CORS

A single named policy, **`CareerHubFrontend`**, registered in `Program.cs`:

```csharp
builder.Services.AddCors(options =>
    options.AddPolicy("CareerHubFrontend", policy => policy
        .WithOrigins("http://localhost:3000", "https://careerhub.example.com")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders("X-Total-Count", "ETag", "api-supported-versions", "Retry-After")));
```

`app.UseCors("CareerHubFrontend")` is placed **before** `UseAuthentication()` and
`UseAuthorization()` so that even a 401/403 response carries the `Access-Control-*`
headers — otherwise the browser turns a real auth failure into an opaque CORS
error the SPA can't read.

### Why `AllowAnyOrigin()` + `AllowCredentials()` throws at startup

The CORS specification **forbids** returning the wildcard
`Access-Control-Allow-Origin: *` together with
`Access-Control-Allow-Credentials: true`. If any origin could send cookies/Authorization
headers and have them honoured, a malicious site could ride a logged-in user's
credentials against the API — so the combination is illegal by design. ASP.NET
Core enforces this **eagerly**: `CorsPolicyBuilder` throws an
`InvalidOperationException` ("The CORS protocol does not allow specifying a wildcard
(any) origin and credentials at the same time") when the policy is built at
startup, rather than silently producing headers a browser would reject. The fix is
to enumerate explicit origins (as above), which is exactly what lets us keep
`AllowCredentials()`.

### Exposed headers

`WithExposedHeaders(...)` whitelists `X-Total-Count` (pagination), `ETag`
(conditional GETs), `api-supported-versions` (versioning) and `Retry-After`
(rate limiting) so browser `fetch` code can actually read them off a cross-origin
response — by default only a handful of "simple" response headers are visible.

## Part 3 — Pagination

`PagedResponse<T>` (in `DTOs/Dtos.cs`) is the envelope for every list response:

```jsonc
{
  "data": [ /* JobListingResponse[] */ ],
  "page": 1, "pageSize": 20, "totalCount": 593, "totalPages": 30,
  "hasNextPage": true, "hasPreviousPage": false,
  "links": { "self": "...", "next": "...", "previous": null, "first": "...", "last": "..." }
}
```

`TotalPages`, `HasNextPage`, `HasPreviousPage` are **derived** by the
`PagedResponse<T>.Create(...)` factory, so the envelope can never be internally
inconsistent.

`IJobListingRepository.GetActiveListingsPagedAsync(JobListingFilterQuery)` does
the work with **exactly one `CountAsync` and one `ToListAsync` over the same
composed `IQueryable`** (`PageAsync` helper):

```csharp
var totalCount = await filtered.CountAsync(ct);          // ① one count of all matches
var data = await ApplySort(filtered, f)                  // OrderBy BEFORE Skip/Take
    .Skip((page - 1) * pageSize).Take(pageSize)
    .Select(Project).ToListAsync(ct);                    // ② one materialisation of the page
```

- **Default sort is `PostedAt DESC`** (`CreatedAt DESC`) and `OrderBy` is always
  applied before `Skip`/`Take`.
- Both `GET /api/v1/jobs` and `GET /api/v1/jobs/company/{companyId}` are paginated
  (the company endpoint keeps its "any status" semantics; the board stays
  active-and-unexpired).
- The controller binds `page`/`pageSize` from the query (defaults 1 / 20) and
  **clamps `pageSize` to a max of 100** (`Math.Clamp(query.PageSize, 1, 100)`),
  re-clamped defensively in the repository.
- **`X-Total-Count`** is written on every paginated response.

## Part 4 — Filtering + Sorting

`JobListingFilterQuery` carries all filters, the sort key/direction and the page
window. Filters **compose** — each non-null parameter adds one `.Where(...)`
(AND semantics); null parameters never reach the SQL:

| Param | Effect |
|---|---|
| `location` | `ILIKE %location%` substring |
| `employmentType` | `Type == value` (maps to 2.4 `JobType`) |
| `salaryMin` | listing's `COALESCE(SalaryMax, SalaryMin) >= salaryMin` ("pays at least") |
| `salaryMax` | listing's `COALESCE(SalaryMin, SalaryMax) <= salaryMax` ("pays at most") |
| `companyId` | `CompanyId == value` |
| `q` *(extra)* | GIN-indexed full-text match, composed with the rest |
| `postedSince` *(extra)* | `CreatedAt >= postedSince` |
| `remoteOnly` *(extra)* | `ILIKE %remote%` on `Location` |

Sorting is a **switch expression** mapping `sort` + `dir` to
`OrderBy`/`OrderByDescending`, with a **default case that is always `PostedAt
DESC`**:

| `sort` | Behaviour |
|---|---|
| `postedAt` (default) | `CreatedAt`, honours `dir` |
| `salaryMin` / `salaryMax` / `title` | honours `dir` |
| `company` *(extra)* | `Company.Name` ascending |
| `expiresAt` *(extra)* | `ExpiresAt` ascending = expiring-soonest first |
| `relevance` *(extra)* | falls back to `PostedAt DESC` (results already narrowed by `q`) |
| *anything else* | `PostedAt DESC` (default case) |

### Extras beyond the assignment

`q` (full-text), `postedSince`, `remoteOnly`, and the `company`/`expiresAt`/`relevance`
sort keys are all additions on top of the required four sort keys and five filters.
`q` reuses the Assignment 2.4 GIN-indexed `SearchVector`, so a full-text search
still rides the index and then Bitmap-ANDs with the other filters.

## Part 5 — PATCH

### Part A — partial listing update

`UpdateJobListingRequest` has **all-nullable** fields. `PatchAsync` lives in both
the repository and the service:

- **Repository** `PatchAsync(id, req)` fetches the **tracked** entity via
  `GetEntityByIdAsync` and applies **only the non-null fields**, returning the
  still-tracked entity (not yet saved).
- **Service** `PatchAsync(id, req)` then **re-validates only what changed** and
  commits: the salary-range check runs only if `SalaryMin` **or** `SalaryMax` was
  supplied; the `ExpiresAt > now` check runs only if `ExpiresAt` was supplied. It
  reuses the **same exception types** (`ArgumentException` → 400,
  `NotFoundException` → 404) as the rest of the service.

`PATCH /api/v1/jobs/{id}` carries the **same `[Authorize(Roles = "Employer")]`** as
create, and the controller action just delegates to the service.

> Stale-fingerprint note (relevant to Part 7): because PATCH can change
> `Description` **without** touching `CreatedAt`/`SalaryMin`, the Part 7 ETag —
> which is computed only from `Id`, `PostedAt` and `SalaryMin` — will **not** change
> even though the content did. See Part 7 for the proper fix.

### Part B — application status transitions

`UpdateApplicationStatusRequest { ApplicationStatus Status }` drives
`PATCH /api/v1/applications/{jobListingId}/{applicantId}/status` (composite key —
the `Application` entity has no surrogate id, so the generic assignment's `{id}` is
the key pair here). Employer-only.

A `private static readonly Dictionary<ApplicationStatus, HashSet<ApplicationStatus>>`
encodes the **legal** transitions:

| From | May move to |
|---|---|
| `Submitted` | `UnderReview`, `Rejected` |
| `UnderReview` | `Shortlisted`, `Rejected`, `Offered` |
| `Shortlisted` | `Offered`, `Rejected` |
| `Rejected` | **— (terminal)** |
| `Offered` | **— (terminal)** |

`Rejected` and `Offered` map to **empty sets**, so they can never move back to
`Submitted` (or anywhere). An illegal transition throws `ArgumentException` → **400**
with a message that **names the from-state and the to-state**, e.g. *"Illegal status
transition from Offered to Submitted. Offered is a terminal state and cannot be
changed."*

## Part 6 — Versioning

`Asp.Versioning.Mvc` + `Asp.Versioning.Mvc.ApiExplorer` are configured in
`Program.cs`:

```csharp
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = true;   // /api/v1 is assumed if omitted
    o.ReportApiVersions = true;                      // adds api-supported-versions header
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddApiExplorer(o => { o.GroupNameFormat = "'v'VVV"; o.SubstituteApiVersionInUrl = true; });
```

Every controller carries `[ApiVersion("1.0")]` and routes on
`api/v{version:apiVersion}/[controller]` (the application endpoints, which are
nested method-level routes, carry the same `{version:apiVersion}` segment). No
service or repository signature changed — versioning is purely a routing/explorer
concern. `ReportApiVersions = true` means every response advertises
`api-supported-versions: 1.0`.

### Introducing v2 — the deprecation lifecycle

When a breaking change is needed we **add** `v2` rather than mutate `v1`:

1. **Parallel run.** Ship `v2` controllers/actions alongside `v1`. Both are live;
   `[ApiVersion("1.0")]` and `[ApiVersion("2.0")]` co-exist. Clients migrate at
   their own pace. A typical window is **~3–6 months** depending on how many
   external consumers there are.
2. **Deprecate v1.** Mark it `[ApiVersion("1.0", Deprecated = true)]`. With
   `ReportApiVersions` the response then carries `api-deprecated-versions: 1.0`
   (alongside `api-supported-versions: 2.0`), a passive signal clients can log/alert
   on without anything breaking yet.
3. **Announce a sunset.** Return a **`Sunset`** header (RFC 8594) on `v1` responses
   giving the exact date `v1` will be removed, plus a `Link: rel="sunset"` to the
   migration guide. This is the contractual "it goes away on this date" notice.
4. **Remove v1.** After the sunset date (and once telemetry shows ~zero `v1`
   traffic), delete the `v1` actions. Requests to `/api/v1/...` then return 404 /
   `UnsupportedApiVersion` 400.

The URL-segment reader makes each step unambiguous: the version a client is on is
always visible in the path, in logs, and in the dashboard, so we can measure
migration progress before pulling `v1`.

## Part 7 — ETags

`Infrastructure/EtagHelper.Compute(params object[])` joins the fingerprint parts
with `:`, SHA-256 hashes them, base64-encodes, and wraps the result in double
quotes (the entity-tag grammar requires the quotes). Both controllers use it so
the logic is never duplicated:

| Endpoint | ETag fingerprint |
|---|---|
| `GET /api/v1/jobs/{id}` | `SHA256("{Id}:{PostedAt.Ticks}:{SalaryMin}")` |
| `GET /api/v1/applications/{jobListingId}/{applicantId}` | `SHA256("{JobListingId}:{ApplicantId}:{Status}")` |

Each single-resource GET:

1. checks `If-None-Match`; on a match it returns **`304 Not Modified` with no
   body**,
2. otherwise sets `Response.Headers.ETag` (and `Cache-Control: private,
   must-revalidate` — extra #6 — so the browser actually issues the conditional
   request) and returns `Ok(dto)`.

Both GET actions return `IActionResult` so they can return either `304` or `200`.

### The stale-304 problem and the proper fix

The job ETag is derived from only `Id`, `PostedAt` and `SalaryMin`. That is a
**deliberately weak fingerprint** chosen to match the assignment, and it has a real
failure mode: a **`PATCH` that changes only `Description`** (Part 5A) leaves `Id`,
`CreatedAt` and `SalaryMin` untouched, so the ETag is **unchanged** — a client
holding the old ETag gets a `304` and keeps showing stale content even though the
listing's body actually changed.

The proper fix is a **single column that changes on *every* write**:

- a **`RowVersion`** (`byte[]`) mapped to PostgreSQL's system **`xmin`** column
  (`[Timestamp]` / `IsRowVersion()`), which Postgres bumps on every row update, or
- an application-maintained **`UpdatedAt`** timestamp set in `SaveChanges`.

Hashing that one value (`EtagHelper.Compute(listing.RowVersion)`) makes the ETag a
true content fingerprint: any update — including a `Description`-only PATCH — moves
it, so conditional GETs can never serve stale data. Adding that column is a schema
change, which Assignment 3.1's ground rules put out of scope, so it is documented
here rather than implemented.

## Part 8 — Rate Limiting

`AddRateLimiter` defines four policies; `app.UseRateLimiter()` sits **right after
`UseCors`, before `UseAuthentication`**:

| Policy | Algorithm | Limit | Queue | Applied to |
|---|---|---|---|---|
| `global` | fixed window | **200 / 60 s** | 0 | `MapControllers().RequireRateLimiting("global")` — the whole surface |
| `search` | sliding window (6 segments) | **30 / 60 s** | 0 | `GET /api/v1/jobs/search` |
| `apply` | fixed window | **5 / 60 min** | 0 | `POST /api/v1/jobs/{id}/applications` |
| `post-listing` | fixed window | **10 / 60 min** | 0 | `POST /api/v1/jobs` |

### `OnRejected`

A rejected request gets **HTTP 429**, a **`Retry-After`** header taken from the
lease metadata (`MetadataName.RetryAfter`, rounded up to whole seconds, defaulting
to 60s if the limiter supplies none), and a **plain-text** body:

```
Rate limit exceeded. Please retry after {N} seconds.
```

### EXTRA — per-user partitioning of `apply`

The `apply` policy is built with `RateLimitPartition.GetFixedWindowLimiter` and a
**key selector**: the JWT **`sub`** claim when the caller is authenticated
(falling back to `ClaimTypes.NameIdentifier`, then the client IP). So each
applicant gets their **own** 5-per-hour budget rather than sharing one global
bucket — one prolific applicant can't exhaust everyone else's ability to apply,
and an unauthenticated caller is still bounded by IP.

The `global`, `search` and `post-listing` policies use the framework's
`AddFixedWindowLimiter`/`AddSlidingWindowLimiter` helpers (single shared bucket per
policy); only `apply` needs the custom partitioning.

---

# Applicant Tracking & Employer Applicant Search

Two feature sets added on top of Assignment 3.1: a **job seeker** can now track the
status of every application they have made, and an **employer** can search the
people who applied to *their* roles by qualification. No existing endpoint changed
its meaning; these are additive.

## A. Applicant — "track my applications" (Applied / Pending / Accepted / Rejected)

The five internal pipeline statuses (`Submitted`, `UnderReview`, `Shortlisted`,
`Rejected`, `Offered`) are recruiter language. A seeker thinks in four buckets, so
a single mapper (`Infrastructure/ApplicationStageMapper`) collapses them into a
friendly **`Stage`** — and *every* applicant endpoint uses that one mapper, so the
list, the filter and the summary can never disagree:

| Friendly `Stage` | Internal status(es) | Meaning to the seeker |
|---|---|---|
| **Applied** | `Submitted` | Received, not yet looked at |
| **Pending** | `UnderReview`, `Shortlisted` | Actively being considered |
| **Accepted** | `Offered` | You got the role |
| **Rejected** | `Rejected` | Unsuccessful |

Three new applicant-only endpoints (all under `GET /api/v1/applications/me`):

| Endpoint | Returns |
|---|---|
| `GET /applications/me` | Full history, newest first. Each row carries **both** the raw `status` and the friendly `stage`. |
| `GET /applications/me?stage=Pending` | The same history filtered to one stage. `stage` is case-insensitive and accepts synonyms (`applied`/`submitted`, `pending`/`inreview`/`underreview`/`shortlisted`, `accepted`/`offered`, `rejected`/`declined`). An unrecognised value is treated as "no filter". |
| `GET /applications/me/summary` | A per-stage count + total: `{ total, applied, pending, accepted, rejected }` — what a dashboard renders without paging the list. |
| `GET /applications/me/{jobListingId}` | Track **one** job: the stage of *your* application to that listing, or **404** if you never applied. The literal `me/summary` route is matched first; the `:guid` constraint means "summary" can never be read as an id. |

The stage filter is pushed into SQL as `WHERE Status IN (…)` (the mapper turns
`Pending` into `('UnderReview','Shortlisted')`); the status→stage label is applied
in memory on the tiny result page. The `me` and `me/{id}` queries ride the existing
`ix_applications_applicantid_joblistingid` index (applicant column leads).

## B. Employer — search applicants who applied, by qualification

A new `Qualifications` column on `Applicant` (free text — degree, certifications,
skills, e.g. *"BSc Computer Science (UCT); Certified Kubernetes Administrator;
Skills: Kubernetes, Docker, AWS; 6 years' experience."*) is the field employers
search. Added via migration **`AddApplicantQualifications`** and populated by the
seed for all 25 applicants plus the demo accounts.

**`GET /api/v1/applicants/search`** — Employer-only. Composable filters:

| Param | Effect |
|---|---|
| `qualification` | `ILIKE %…%` over the applicant's **Qualifications** *and* **Headline** |
| `minExperience` | `YearsOfExperience >= value` |
| `city` | `ILIKE %…%` on the applicant's city |
| `jobListingId` | restrict to applicants who applied to that one listing of yours |
| `page` / `pageSize` | paginated (pageSize clamped ≤ 100), same `PagedResponse<T>` envelope + `X-Total-Count` + HATEOAS links as the job board |

Each result row is the candidate's profile plus how they relate to your roles:
`applicationsToYourCompany` (how many of your listings they applied to) and
`latestStage` (the friendly stage of their most recent application to you).

**Security — the same model as the Part 8 stats endpoint.** The company is read
from the employer's **JWT**, never a query parameter, and the candidate pool is
filtered server-side to `WHERE JobListing.CompanyId = <your company>`. So an
employer can only ever search the people who applied to *their own* roles — there
is no parameter that could widen the search to another company's candidate pool.

### How the query is built (and stays translatable)

The search is rooted on `Applicants` so the profile filters are plain `Where`s, and
the per-applicant aggregates (`applicationsToYourCompany`, `latestStage`) are
correlated sub-queries EF Core translates cleanly — no client-side `GroupBy`, no
"load everything then filter in memory". The `qualification` filter uses
`EF.Functions.ILike`, so a case-insensitive substring match runs in PostgreSQL.

> **Scaling note (documented, not implemented).** `ILIKE %term%` cannot use a plain
> B-tree index. At today's candidate volume that is irrelevant, but the index-correct
> fix for a large applicant base is a **`pg_trgm` GIN trigram index** on
> `Qualifications` (`CREATE EXTENSION pg_trgm; CREATE INDEX … USING gin (…
> gin_trgm_ops)`), which *does* accelerate substring `ILIKE`. It is left as a
> documented next step to keep this change schema-light, mirroring the Part 7 ETag
> `RowVersion` note.

---

# What's new / extras

Ten improvements layered on top of the assignment requirements:

1. **HATEOAS-lite links** — every `PagedResponse<T>` carries a `links` object
   (`self`/`next`/`previous`/`first`/`last`) built from the current request by
   `Infrastructure/PaginationLinks`, preserving the active filters/sort across pages.
2. **ProblemDetails everywhere** — `AddProblemDetails()` is registered and every
   400/404/409 flows through `ValidationExceptionHandler`, so error bodies share one
   RFC 7807 shape (`type`/`title`/`status`/`detail`).
3. **Global exception handler** — `ValidationExceptionHandler` (registered with
   `AddExceptionHandler` + `app.UseExceptionHandler()`) maps the domain exceptions
   (`NotFoundException`, `ConflictException`, `DuplicateApplicationException`,
   `ArgumentException`, EF check-constraint violations) to ProblemDetails. Controllers
   contain **no try/catch**.
4. **OpenAPI/Scalar polish** — each controller is grouped with `[Tags(...)]`
   (`Jobs`/`Applications`/`Auth`/`Companies`) and the single-resource GET and apply
   endpoints declare `[ProducesResponseType]` for 200/304/400/404/409/429 so the
   Scalar UI documents every outcome. *(Note: `[SwaggerParameter]` belongs to
   Swashbuckle; this project uses the native `Microsoft.AspNetCore.OpenApi` + Scalar
   stack, so query parameters are documented via XML doc comments and the
   `JobListingFilterQuery` shape rather than the Swashbuckle annotation.)*
5. **Response compression** — `AddResponseCompression` with **Brotli + Gzip** for
   `application/json` (and `application/problem+json`), enabled before the endpoints.
6. **Cache-Control** — the single-resource GETs send `Cache-Control: private,
   must-revalidate` alongside the ETag, so browsers issue conditional requests.
7. **Idempotency-Key** — `POST /api/v1/applications` accepts an `Idempotency-Key`
   header; the outcome is stored in an in-memory `IDistributedCache` keyed by
   `idempotency:{userId}:{key}` for 24h, so a retried request replays the prior result
   instead of creating a duplicate application.
8. **Health checks** — `AddHealthChecks().AddNpgSql(...)` exposes **`/health/live`**
   (liveness — runs no dependency checks) and **`/health/ready`** (readiness — pings
   PostgreSQL via the `ready`-tagged check).
9. **Structured logging / correlation id** — `CorrelationIdMiddleware` reads or mints
   an `X-Correlation-Id`, echoes it on the response, and opens a logging scope so every
   log line for the request is tagged with it.
10. **Integration tests** — `CareerHub.Api.Tests` (WebApplicationFactory) covers the
    pagination envelope, filter composition, PATCH partial update, the ETag 304
    round-trip, the rate-limit 429 + Retry-After, the CORS preflight, the applicant
    application-tracking endpoints and the employer applicant-search. See the
    *Testing* section below.

---

# Testing

`CareerHub.Api.Tests` boots the real API in-process with `WebApplicationFactory<Program>`
and exercises it over HTTP. The host runs migrations + the SA seed on startup, so
**Postgres must be running first**:

```bash
docker compose up -d        # Postgres on localhost:5544
dotnet test                 # runs the integration suite (CareerHub.slnx)
```

| Test | Proves |
|---|---|
| `Board_returns_paged_envelope_…` | `PagedResponse<T>` shape + `X-Total-Count` header + HATEOAS links |
| `PageSize_is_clamped_to_100` | `pageSize` clamp (Part 3) |
| `Filters_compose_…` | filter composition — every returned row matches the filter (Part 4) |
| `Patch_partial_update_…` | PATCH changes only supplied fields; others untouched (Part 5A) |
| `Patch_with_inverted_salary_range_…` | re-validation → 400 ProblemDetails (Part 5A) |
| `Etag_round_trip_…` | `If-None-Match` → 304 with no body (Part 7) |
| `Search_exceeding_30_per_minute_…` | sliding-window 429 + `Retry-After` body (Part 8) |
| `Cors_preflight_…` | preflight echoes `Access-Control-Allow-Origin`/`-Credentials` (Part 2) |
| `New_applicant_starts_with_empty_history_…` | fresh applicant: empty history + zero summary |
| `Applying_shows_up_as_Applied_stage_…` | apply → `me`, `me?stage=`, `me/summary`, `me/{id}` all show stage **Applied** |
| `Tracking_a_listing_never_applied_to_returns_404` | `me/{id}` 404 when not applied |
| `Employer_search_is_scoped_and_filters_compose` | applicant search scoped to listing + `minExperience` filter composes |
| `Applicant_search_is_forbidden_for_applicants` | `applicants/search` is Employer-only → 403 |

---

# Endpoint summary

| Verb | Route | Auth | Rate-limit policy |
|---|---|---|---|
| GET | `/api/v1/jobs` | anonymous | global (200/60s) |
| GET | `/api/v1/jobs/filter` | anonymous | global |
| GET | `/api/v1/jobs/{id}` | anonymous | global (ETag, Cache-Control) |
| GET | `/api/v1/jobs/company/{companyId}` | anonymous | global |
| GET | `/api/v1/jobs/search?q=` | anonymous | **search** (sliding 30/60s) |
| GET | `/api/v1/jobs/stats` | Employer | global |
| POST | `/api/v1/jobs` | Employer | **post-listing** (10/60min) |
| PATCH | `/api/v1/jobs/{id}` | Employer | global |
| POST | `/api/v1/jobs/{jobListingId}/applications` | Applicant | **apply** (5/60min, per-user) |
| GET | `/api/v1/applications/me` · `?stage=` | Applicant | global · **track my applications** |
| GET | `/api/v1/applications/me/summary` | Applicant | global · **per-stage counts** |
| GET | `/api/v1/applications/me/{jobListingId}` | Applicant | global · **track one job (404 if not applied)** |
| GET | `/api/v1/applicants/search` | Employer | global · **search applicants by qualification** |
| GET | `/api/v1/applications/{jobListingId}/{applicantId}` | authenticated | global (ETag) |
| PATCH | `/api/v1/applications/{jobListingId}/{applicantId}/status` | Employer | global |
| POST | `/api/v1/auth/register/applicant` · `register/employer` · `login` | anonymous | global |
| GET | `/api/v1/companies` | anonymous | global |
| GET | `/health/live` · `/health/ready` | anonymous | — |

---

# Prove it works — curl commands

> Set `BASE=http://localhost:5080` (and a `$TOKEN` from a login response where auth
> is needed). All paths are `/api/v1/...`.

**Part 2 — CORS preflight** (note the echoed origin + credentials header):
```bash
curl -i -X OPTIONS "$BASE/api/v1/jobs" \
  -H "Origin: http://localhost:3000" \
  -H "Access-Control-Request-Method: GET"
# → Access-Control-Allow-Origin: http://localhost:3000
#   Access-Control-Allow-Credentials: true
```

**Part 3 — pagination** (envelope + X-Total-Count):
```bash
curl -i "$BASE/api/v1/jobs?page=2&pageSize=5"
# → X-Total-Count: 593 ; body has data/page/pageSize/totalCount/totalPages/links
```

**Part 4 — filtering + sorting** (compose filters, sort by salary):
```bash
curl "$BASE/api/v1/jobs?location=Cape%20Town&employmentType=FullTime&salaryMin=40000&sort=salaryMax&dir=desc&pageSize=10"
curl "$BASE/api/v1/jobs?q=engineer&remoteOnly=true&sort=expiresAt"
```

**Part 5A — PATCH a listing** (employer token):
```bash
curl -i -X PATCH "$BASE/api/v1/jobs/<JOB_ID>" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"location":"Remote (South Africa)","salaryMax":120000}'
# → 204 No Content   (inverted salary range → 400 problem+json)
```

**Part 5B — application status transition** (employer token):
```bash
curl -i -X PATCH "$BASE/api/v1/applications/<JOB_ID>/<APPLICANT_ID>/status" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"status":"UnderReview"}'
# legal → 204 ; illegal (e.g. Offered→Submitted) → 400 naming both states
```

**Part 6 — versioning** (reports supported versions):
```bash
curl -i "$BASE/api/v1/jobs?pageSize=1"   # → api-supported-versions: 1.0
```

**Part 7 — ETag 304 round-trip**:
```bash
ETAG=$(curl -s -D - "$BASE/api/v1/jobs/<JOB_ID>" -o /dev/null | grep -i '^etag:' | cut -d' ' -f2- | tr -d '\r')
curl -i "$BASE/api/v1/jobs/<JOB_ID>" -H "If-None-Match: $ETAG"   # → 304 Not Modified
```

**Part 8 — rate limit 429** (trip the search sliding window):
```bash
for i in $(seq 1 40); do curl -s -o /dev/null -w "%{http_code}\n" "$BASE/api/v1/jobs/search?q=engineer"; done | tail
# → 200 … then 429 ; the 429 response body: "Rate limit exceeded. Please retry after N seconds."
```

**Extra #7 — idempotent apply** (same key twice = one application; applicant token):
```bash
curl -i -X POST "$BASE/api/v1/jobs/<JOB_ID>/applications" \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: abc-123" \
  -H "Content-Type: application/json" -d '{"coverNote":"Keen!"}'   # 201, then 201 replay (no duplicate)
```

**Extra #8 — health checks**:
```bash
curl -i "$BASE/health/live"    # → 200 Healthy (no dependencies)
curl -i "$BASE/health/ready"   # → 200 Healthy (Postgres reachable)
```

**Applicant — track my applications** (log in as the demo applicant first):
```bash
# APP_TOKEN from logging in as demo.applicant@careerhub.co.za / DemoPass123!
curl -s "$BASE/api/v1/applications/me" -H "Authorization: Bearer $APP_TOKEN"
# → rows with both "status" (Submitted/…) and "stage" (Applied/Pending/Accepted/Rejected)

curl -s "$BASE/api/v1/applications/me?stage=Accepted" -H "Authorization: Bearer $APP_TOKEN"
# → only the Accepted (Offered) applications

curl -s "$BASE/api/v1/applications/me/summary" -H "Authorization: Bearer $APP_TOKEN"
# → { "total":4, "applied":1, "pending":1, "accepted":1, "rejected":1 }  (demo seed)

curl -i "$BASE/api/v1/applications/me/<JOB_ID>" -H "Authorization: Bearer $APP_TOKEN"
# → 200 with that application's stage, or 404 if you never applied to <JOB_ID>
```

**Employer — search applicants by qualification** (log in as the demo employer first):
```bash
# EMP_TOKEN from logging in as demo.employer@takealot.co.za / DemoPass123!
curl -i "$BASE/api/v1/applicants/search?qualification=kubernetes&pageSize=10" \
  -H "Authorization: Bearer $EMP_TOKEN"
# → X-Total-Count header + paged envelope of applicants (to Takealot's roles) whose
#   Qualifications/Headline contain "kubernetes" — the demo applicant is one of them.

curl -s "$BASE/api/v1/applicants/search?minExperience=5&city=Cape%20Town" \
  -H "Authorization: Bearer $EMP_TOKEN"
# → composes: ≥5 years' experience AND city contains "Cape Town"

# Scoping proof: an APPLICANT token is forbidden (Employer-only) → 403
curl -i "$BASE/api/v1/applicants/search" -H "Authorization: Bearer $APP_TOKEN"   # → 403
```

---

# Testing in Scalar — click-by-click guide

This is the **point-and-click** version of the curl section above: how to demonstrate
every Assignment 3.1 requirement from the **Scalar UI** in the browser, with the exact
endpoint, the exact JSON body, and where each filter/search/sort lives.

### 0. Start the app and open Scalar

```bash
docker compose up -d        # Postgres 17 on localhost:5544
dotnet run                  # migrates + seeds, serves on :5080
```

Open **`http://localhost:5080/scalar/v1`**. The left sidebar groups every endpoint by
tag — **Auth · Jobs · Applications · Applicants · Companies**. To send a request:
pick an endpoint → fill the **Body** / **Query parameters** / **Headers** fields →
click **Send**. The response (status, headers, JSON) appears on the right.

> Every route is versioned: the paths all begin with **`/api/v1/...`**. Scalar fills
> the `v1` segment for you.

### 1. Log in to get a token (do this first — most demos need it)

Two seeded demo accounts exist (password **`DemoPass123!`** for both):

| Role | Email | Use it for |
|---|---|---|
| Applicant | `demo.applicant@careerhub.co.za` | applying, "track my applications" |
| Employer (Takealot) | `demo.employer@takealot.co.za` | posting, PATCH, stats, applicant search |

1. Sidebar → **Auth** → **`POST /api/v1/auth/login`** → **Body**:
   ```json
   { "email": "demo.employer@takealot.co.za", "password": "DemoPass123!" }
   ```
2. **Send** → copy the **`token`** value from the response.
3. Click the **Authorize** / 🔒 button at the top of Scalar, paste the token as a
   **Bearer** token. Every subsequent request now sends `Authorization: Bearer …`
   automatically. (Log in as the **applicant** instead when an endpoint is
   Applicant-only — re-authorize with that token.)

> No account yet? Use **`POST /api/v1/auth/register/applicant`** with body
> `{ "fullName":"Test User", "email":"you@test.co.za", "password":"Pass123!" }`.
> For an employer you also need a `companyId` — grab one from **`GET /api/v1/companies`**.

### 2. Search jobs — **`GET /api/v1/jobs/search`**

Sidebar → **Jobs** → **`GET /api/v1/jobs/search`**. In **Query parameters** set
**`q`** = `Kubernetes` → **Send**. Returns exactly the **3** Kubernetes platform
listings (Takealot, Yoco, Naspers). Try `q` = `sprint` to see stemming — it matches
the **Scrum Master** listing whose text says "*sprinting*". *(This endpoint is rate-
limited to 30/min — see step 9.)*

### 3. Filter "by specification" — **`GET /api/v1/jobs`**

The job board (**`GET /api/v1/jobs`**) takes all the filters as **query parameters**;
each one you fill in is ANDed with the rest. Leave a box empty to ignore it.

| Query param | Example value | Meaning |
|---|---|---|
| `location` | `Cape Town` | location contains (case-insensitive) |
| `employmentType` | `FullTime` | exact type — `FullTime` / `PartTime` / `Contract` / `Internship` / `Temporary` |
| `salaryMin` | `40000` | pays **at least** R40 000 |
| `salaryMax` | `120000` | pays **at most** R120 000 |
| `companyId` | *(a GUID)* | one company's listings |
| `q` | `engineer` | full-text search, composed with the filters |
| `remoteOnly` | `true` | only listings whose location says "remote" |
| `postedSince` | `2026-01-01` | posted on/after this date |

Example: set `location=Cape Town`, `employmentType=FullTime`, `salaryMin=40000`,
**Send** → only full-time Cape Town roles paying ≥ R40 000.

### 4. Filter / sort "by alphabet" — `sort` + `dir` on **`GET /api/v1/jobs`**

Same endpoint, two more query parameters control ordering:

| `sort` value | Orders by |
|---|---|
| `title` | **alphabetical by job title** ← "by alphabet" |
| `company` | alphabetical by company name |
| `salaryMin` / `salaryMax` | salary |
| `postedAt` *(default)* | newest first |
| `expiresAt` | expiring soonest first |

`dir` = `asc` (A→Z) or `desc` (Z→A). **To list jobs A→Z:** set `sort=title`,
`dir=asc`, **Send**. The response is the `PagedResponse` envelope — note the
`X-Total-Count` header and the `links` block (HATEOAS).

### 5. Pagination — **`GET /api/v1/jobs`**

Set `page=2` and `pageSize=5` → **Send**. The body shows
`page/pageSize/totalCount/totalPages/hasNextPage/links`, and the response **headers**
include `X-Total-Count`. Try `pageSize=500` to prove the **clamp**: it comes back as
`pageSize: 100`.

### 6. Post a listing — **`POST /api/v1/jobs`** *(Employer token)*

Sidebar → **Jobs** → **`POST /api/v1/jobs`** → **Body**:

```json
{
  "title": "Senior Backend Engineer",
  "description": "Build and scale our order pipeline on .NET and PostgreSQL.",
  "minimumRequirements": "5+ years C#, EF Core, PostgreSQL.",
  "location": "Cape Town",
  "type": "FullTime",
  "salaryMin": 70000,
  "salaryMax": 110000,
  "expiresAt": "2026-12-31T00:00:00Z"
}
```

**Send** → **201 Created** with the new `id`. (`companyId` is **not** in the body —
it comes from your employer token.) Demonstrate validation: swap to
`"salaryMin": 90000, "salaryMax": 50000` → **400** with
*"SalaryMax must be greater than SalaryMin."* *(Post-listing is limited to 10/hour.)*

### 7. PATCH a listing — **`PATCH /api/v1/jobs/{id}`** *(Employer token)*

Put a job's `id` in the **path** field, then send only the fields you want to change:

```json
{ "location": "Remote (South Africa)", "salaryMax": 120000 }
```

**Send** → **204 No Content**; everything you didn't send is untouched. An inverted
salary range in the body → **400 problem+json**.

### 8. Application status transition — **`PATCH /api/v1/applications/{jobListingId}/{applicantId}/status`** *(Employer token)*

`Application` has a **composite key**, so the path takes **both** ids. Body:

```json
{ "status": "UnderReview" }
```

Legal move → **204**. An illegal one (e.g. body `{ "status": "Submitted" }` on an
already-`Offered` application) → **400** naming both states. Legal flow:
`Submitted → UnderReview → Shortlisted → Offered` (`Rejected`/`Offered` are terminal).

### 9. Rate limiting (429) — **`GET /api/v1/jobs/search`**

Hit **Send** on the search endpoint **rapidly ~35 times**. After 30 within the minute
you get **429 Too Many Requests**, a **`Retry-After`** header, and the plain-text body
*"Rate limit exceeded. Please retry after N seconds."*

### 10. ETags / 304 — **`GET /api/v1/jobs/{id}`**

1. Send `GET /api/v1/jobs/{id}` for any job → copy the **`ETag`** value from the
   response headers.
2. Send the **same** request again, adding a **Header** `If-None-Match` = that ETag →
   **304 Not Modified** with no body.

### 11. Versioning — any endpoint

Any response's headers carry **`api-supported-versions: 1.0`** (because
`ReportApiVersions` is on). Visible on, e.g., `GET /api/v1/jobs?pageSize=1`.

### 12. Apply to a job — **`POST /api/v1/jobs/{jobListingId}/applications`** *(Applicant token)*

Re-authorize as the **applicant** first. Put a job `id` in the path, body:

```json
{ "coverNote": "Keen to join — strong PostgreSQL background." }
```

**Send** → **201**. To prove **idempotency** (extra #7): add a **Header**
`Idempotency-Key` = `abc-123` and send twice — the second send replays the first
result instead of creating a duplicate. *(Apply is limited to 5/hour per user.)*

### 13. Track my applications — **Applications** tag *(Applicant token)*

| Endpoint | What to do | Shows |
|---|---|---|
| `GET /api/v1/applications/me` | just Send | full history; each row has raw `status` **and** friendly `stage` |
| `GET /api/v1/applications/me` + query `stage=Pending` | set `stage` | only that stage (`Applied`/`Pending`/`Accepted`/`Rejected`) |
| `GET /api/v1/applications/me/summary` | just Send | `{ total, applied, pending, accepted, rejected }` |
| `GET /api/v1/applications/me/{jobListingId}` | put a job id in the path | that one application's stage, or **404** if you never applied |

### 14. Employer applicant search — **`GET /api/v1/applicants/search`** *(Employer token)*

Re-authorize as the **employer**. Query parameters (all optional, all compose):

| Query param | Example | Meaning |
|---|---|---|
| `qualification` | `kubernetes` | substring over the applicant's Qualifications **and** Headline |
| `minExperience` | `5` | ≥ 5 years' experience |
| `city` | `Cape Town` | city contains |
| `jobListingId` | *(a GUID)* | only people who applied to that listing of yours |

**Send** → paged envelope of candidates **who applied to your company's roles** (the
pool is scoped to your company via the token — there is no parameter to widen it).
Each row includes `applicationsToYourCompany` and `latestStage`. Proof it's locked
down: send the same request with an **applicant** token → **403 Forbidden**.

### 15. Employer stats & companies

- **`GET /api/v1/jobs/stats`** *(Employer token)* — per-status counts + `RANK()` for
  **your** company (no params; company comes from the token).
- **`GET /api/v1/companies`** *(anonymous)* — every company with its active-listing
  count; handy for grabbing a `companyId` for filters or employer registration.

### 16. Health checks (no auth)

Browse to **`/health/live`** (process up) and **`/health/ready`** (Postgres
reachable) — both return **200 Healthy**.

> **Quick demo order for a marker:** log in (1) → search (2) → filter+A–Z sort
> (3–4) → pagination (5) → post (6) → PATCH (7) → status transition (8) → 429 (9) →
> ETag 304 (10) → apply (12) → track (13) → applicant search + 403 (14). That walks
> every Assignment 3.1 part in one pass.

---

# Assignment 3.2 — Testing & CI/CD

This section documents the test suite (`API.Tests/`) and the GitHub Actions
pipeline (`.github/workflows/ci.yml`) added on top of the Assignment 3.1 API. The
suite is split into three layers — unit, integration and repository — each chosen
for the *kind* of bug it can catch, and the pipeline runs all three on every push
and pull request to `main`.

## 1. Part 1 — Written decisions

### 1.1 What belongs in a unit test vs an integration test

| Behaviour | Test type | Why — and what the *other* type cannot verify |
|---|---|---|
| Salary-range validation in `JobService.CreateAsync` | **Unit** (NSubstitute) | It is pure C# branching over the request, with no I/O. A mock lets us assert both the thrown exception *and* that `AddAsync`/`SaveChangesAsync` were never reached after a rejection. An integration test could observe the 400 but **cannot prove the write path was skipped** — a service that validated *after* writing would still return 400 yet corrupt data, and only the `DidNotReceive()` assertion catches that. |
| `[Authorize]` on `POST /api/v1/jobs` | **Integration** (`WebApplicationFactory`) | Authorisation is enforced by middleware in the HTTP pipeline, not by the service. Only a real request with no token exercises authentication → `401`. A unit test calls the service method directly, so it **never runs the `[Authorize]` filter at all** and cannot tell whether the attribute is present, has the right role, or was accidentally deleted. |
| `SalaryMax > SalaryMin` CHECK constraint | **Integration / Repository** (TestContainers, real PostgreSQL) | A CHECK constraint lives in DDL and only fires inside a real database engine. The repository test inserts a bad row straight through the `DbContext`, bypassing the service guard, and asserts PostgreSQL rejects it (SQLSTATE 23xxx). A unit test (or any in-memory-provider test) has **no SQL engine, so the constraint does not exist** and the bad row is silently accepted. |
| `api-supported-versions: 1.0` header on every response | **Integration** | The header is emitted by the API-versioning middleware (`ReportApiVersions = true`) as the response leaves the pipeline. Only a real round-trip can read it off `HttpResponseMessage.Headers`. A unit test produces no `HttpResponse` and **cannot see response headers** the framework adds. |
| `HasAppliedAsync` compiled query returns the correct boolean | **Repository** (TestContainers) | The query is an `EF.CompileAsyncQuery` expression tree translated to SQL once and reused; a translation bug is silent until it runs against real PostgreSQL. The repository test seeds an application and asserts `true`/`false`. A unit test would have to mock the method itself — it **tests the mock, not the compiled SQL**, so a mistranslation never surfaces. |

The recurring theme: a unit test verifies *decisions* (branches, guard ordering,
which collaborator was called) but is blind to *anything the framework or database
does* — routing, filters, response headers, SQL translation, constraints. The
higher layers verify exactly those, but pay a host-boot or container round-trip per
test and cannot easily force internal failure branches.

### 1.2 Why the in-memory EF Core provider is insufficient for CareerHub

Three CareerHub behaviours the EF Core in-memory provider **cannot** verify (see
section 4 for the full write-up):

1. **The `CHECK` constraints from Assignment 2.4** (`SalaryMin > 0`, `SalaryMax > SalaryMin`, `ExpiresAt > CreatedAt`). *Limitation:* the in-memory provider is a LINQ-over-dictionaries store with **no relational schema and no constraint enforcement**, so it accepts rows a real database rejects.
2. **Full-text search via the stored `tsvector` + `to_tsquery('english', …)`.** *Limitation:* the in-memory provider has **no `tsvector` type, no GIN index and no text-search configuration**, so `SearchVector.Matches(...)` cannot be translated — it throws instead of stemming "engineer" → "engineering".
3. **The stored *generated* `SearchVector` column (and the raw-SQL `RANK()` stats query).** *Limitation:* the column is computed **by PostgreSQL** on write; the in-memory provider **never runs computed-column or raw-`FromSql` logic**, so the column is always empty and the stats query cannot execute.

### 1.3 Test isolation

A test is **isolated** when its result depends only on its own arranged state — not
on what other tests did, nor on the order tests happen to run in. Isolation matters
because without it a suite becomes non-deterministic: a test can pass alone and fail
in a full run (or vice-versa), and a failure no longer localises the bug.

The concrete hazard for repository tests: suppose test A seeds 6 active listings and
asserts `TotalCount == 6`, while test B seeds 2 listings and asserts `TotalCount == 2`,
**both against the same database**. Run B-then-A and A now sees 8 rows and fails; run
A-then-B and B sees 8 and fails. The assertions are correct — the *shared mutable
state* is the bug, and which test "fails" is decided by run order, not by any real
defect.

TestContainers + per-test seeding removes this: each test class gets its **own
throwaway PostgreSQL container**, and `IAsyncLifetime.InitializeAsync` runs Respawn
to wipe every table (preserving the migrated schema) **before each test**, so every
test starts from an empty, known database and seeds exactly the rows it asserts on.
No row outlives the test that created it, so order can never change an outcome — the
suite passes identically run forwards, backwards, or twice in a row.

### 1.4 The purpose of a CI pipeline

Running tests locally proves the suite passes *on one developer's machine, against
the code in their working tree, with their tools and environment*. A CI pipeline
runs the same suite on a **clean, neutral machine from a fresh checkout** on every
push and pull request — so it catches what local runs structurally cannot:
uncommitted files the author forgot to add, "works because of a tool installed only
on my laptop", non-Release-config breakage, and platform differences (the suite runs
on `ubuntu-latest`, not the author's Windows box).

Most importantly it catches the **parallel-merge** failure: developer A and
developer B each branch from the same `main`, each writes a feature, each runs tests
locally and is green. A's branch renames `IJobService.PatchAsync`; B's branch adds a
new caller of the *old* signature. Both PRs are individually green because neither
local run ever saw the other's change. Merge both and `main` no longer compiles —
yet no single local run could have caught it, because the breaking combination never
existed on any one machine. CI plus "**Require branches to be up to date before
merging**" forces the second PR to rebuild against the post-merge `main` and re-run
the suite on the *combined* code, where the break finally surfaces and the merge is
blocked (see section 6).

### 1.5 Defence in depth — testing both enforcement layers

The same salary and expiry rules are enforced **twice** — as C# guards in
`JobService` and as PostgreSQL `CHECK` constraints — and the suite tests each
independently. The unit tests prove the C# guard rejects an inverted range before it
reaches the database; the repository test `CheckConstraint_RejectsSalaryMaxLessThanSalaryMin`
bypasses that guard entirely and proves PostgreSQL *also* rejects it, so a future
writer that skips the service layer (a raw script, a second service, a bulk import)
still cannot persist a bad row. Testing only one layer would let the other rot
silently.

## 2. Test pyramid (real counts from a clean run)

Counts are taken from a clean `dotnet test` run of `API.Tests` (see
`docs/proof/`).

```
                    ┌──────────────────────────────┐
                    │   Repository tests:  13       │   real PostgreSQL (Testcontainers)
                    │   (slowest, fewest)           │   migrations · CHECK constraints · FTS
                    └──────────────────────────────┘
              ┌────────────────────────────────────────┐
              │      Integration tests:  14             │   real HTTP pipeline + real DB
              │      (WebApplicationFactory<Program>)   │   routing · auth · ETag · paging
              └────────────────────────────────────────┘
        ┌──────────────────────────────────────────────────────┐
        │            Unit tests:  16                            │   service logic, repo substituted
        │   (fastest, most numerous — < 100ms each)            │   salary/expiry/PATCH/state machine
        └──────────────────────────────────────────────────────┘

        Total: 43 tests   (Unit 16  ·  Integration 14  ·  Repository 13)
```

The shape is bottom-heavy by cost, not by raw count: the 16 unit tests are the
cheapest and run in single-digit milliseconds with no Docker, so they carry the
business-rule load; the 14 integration and 13 repository tests are each far more
expensive (a host boot or a container round-trip per test), so although their
counts are close to the unit band, they are reserved for the seams — real routing,
auth, ETags, SQL translation, CHECK constraints and full-text stemming — that
cannot be proven at the unit level. If wall-clock time rather than test count were
plotted, the unit layer would dwarf the other two.

## 3. Branch-protection setup (and why each tick matters)

GitHub → **Settings → Branches → Add branch ruleset → Target branches: `main` →
Require status checks to pass → search for and select "Build and Test CareerHub
API" → tick "Require branches to be up to date before merging" → tick "Do not allow
bypassing the above settings"**.

- **Require status checks to pass → "Build and Test CareerHub API".** This binds
  the merge button to the CI job by its exact name. Until the pipeline reports
  success for the PR's head commit, the PR cannot merge — so broken or untested
  code is physically unable to land on `main`. The name must match the `name:` of
  the job in `ci.yml` character-for-character, which is why the workflow pins it.
- **Require branches to be up to date before merging.** This forces a PR to be
  rebuilt against the current tip of `main` before it can merge. It closes the
  "semantic merge conflict" gap where two PRs each pass CI in isolation but break
  once combined (e.g. one renames a method the other starts calling): the second
  PR must re-run CI on the merged result, catching the break before it reaches
  `main` rather than after.
- **Do not allow bypassing the above settings.** Without this, administrators (and
  anyone with bypass permission) can merge straight past a red build, which makes
  the protection advisory rather than enforced. Ticking it applies the rules to
  *everyone*, so the green build is a genuine invariant of `main`, not a polite
  suggestion.

## 4. In-memory provider limitations (three CareerHub features it cannot test)

The repository tests use a real PostgreSQL container precisely because the EF Core
in-memory provider cannot model the following CareerHub behaviours:

1. **The salary/expiry `CHECK` constraints.** The in-memory provider is a LINQ-over-
   dictionaries store with no SQL engine and no constraint enforcement, so it
   silently accepts `SalaryMax < SalaryMin` or `ExpiresAt <= CreatedAt`. The
   database-level guarantee (`ck_job_listings_salary_max_gt_min` etc.) therefore
   *cannot* be exercised against it — only a real Postgres rejects the bad row with
   SQLSTATE 23514.
2. **Full-text search with the `english` stemmer.** `SearchAsync` matches against a
   stored generated `tsvector` column via `to_tsquery('english', …)`, so a search
   for "engineer" matches a listing titled "Software **Engineering** Position"
   through stemming. The in-memory provider has no `tsvector` type, no GIN index and
   no text-search configuration, so it cannot translate `SearchVector.Matches(...)`
   at all — the query throws rather than stems.
3. **The stored generated `tsvector` column itself (and the raw-SQL `RANK()`
   statistics).** `SearchVector` is computed *by PostgreSQL* on every insert/update;
   the application never writes it. An in-memory store never runs that computed-column
   logic, so the column is always empty, and the Part 8 statistics query
   (`COUNT(*) FILTER (...)` + `RANK() OVER (...)` via `FromSql`) is raw PostgreSQL the
   provider cannot execute.

## 5. `public partial class Program {}` — what it does and why it's needed

`WebApplicationFactory<TEntryPoint>` needs a public type from the API assembly to
locate the application's entry point and host configuration. With top-level
statements (as in `Program.cs`), the compiler generates the `Program` class as
**internal**, so a separate test assembly cannot name it as the `TEntryPoint`
generic argument. Adding `public partial class Program;` at the bottom of
`Program.cs` re-declares that same generated class as **public** (a partial merges
with the compiler's half) without changing a line of startup behaviour. That single
public handle is what lets `WebApplicationFactoryFixture : WebApplicationFactory<Program>`
boot the real pipeline in-process. (In this repo the line was already present, so
no API change was required for the integration tests.)

## 6. The merge-queue / parallel-PR scenario

Suppose two PRs are both green in isolation. PR-A renames `IJobService.PatchAsync`'s
signature; PR-B adds a new caller of the old signature. Each passed CI against the
`main` they branched from, so each looks safe. If `main` only required "status
checks to pass", they could both merge — and `main` would now be broken, because
B's new call no longer compiles against A's rename, yet no single PR ever tested
the combination. **"Require branches to be up to date before merging"** prevents
exactly this: after A merges, B is forced to update to the new `main` and re-run CI
on the combined code, where the build breaks and the merge is blocked. A **merge
queue** generalises this — it serialises pending PRs, builds each one on top of the
others' already-queued changes, and only fast-forwards `main` for the combinations
that stay green, so several PRs can merge per hour without anyone manually rebasing
while still never landing an untested combination.

## 7. Naming conventions (and what `Test1/Test2/Test3` throws away)

Three examples from the suite, each in `Method_Condition_ExpectedOutcome` form:

1. `CreateAsync_WhenSalaryMaxLessThanSalaryMin_ThrowsInvalidSalaryException`
2. `UpdateStatusAsync_WhenTransitionIsIllegal_ReturnsBadRequest`
3. `GetJobById_WithMatchingETag_Returns304`

Each name states the unit under test, the precondition, and the expected result, so
a failing test in the CI log reads as a sentence: *"CreateAsync, when SalaryMax is
less than SalaryMin, throws."* That tells a developer what broke and under what
condition **without opening the test body**, doubles as living documentation of the
system's rules, and makes a red run triageable at a glance. Named `Test1/Test2/Test3`,
all of that is lost: a failure says only "Test2 failed", forcing the reader to open
the source, reverse-engineer the intent, and guess which business rule regressed —
and two tests can no longer be told apart by anything but a meaningless ordinal.

## 8. Coverage analysis (Part 7)

- **Two things the unit tests miss.** (i) *Routing, model binding and auth.* They
  call `JobService` directly, so a broken `[Route]`, a wrong `[Authorize]` role, or
  a JSON-binding mismatch is invisible — the integration tests catch these. (ii)
  *Real SQL translation.* The repository is substituted, so a LINQ query that can't
  be translated to SQL, a bad migration, or a constraint violation never surfaces —
  only the repository tests run the actual SQL.
- **One thing the integration tests miss.** *Hard-to-reach failure branches.* Driving
  the system through HTTP against seeded data, it is awkward to force conditions like
  "the repository threw a transient `DbUpdateConcurrencyException`" or a specific
  illegal state-machine edge; the unit tests reach those branches trivially by
  arranging the substitute.
- **One thing the TestContainers repository tests miss.** *The HTTP and
  authorisation layer.* They construct `JobListingRepository` over a `DbContext`
  directly, so they never exercise controllers, routing, the ETag/`If-None-Match`
  handling, rate limiting or `[Authorize]` — a repository test passes even if the
  endpoint above it returns the wrong status code or leaks data across tenants.

## How to run the suite

```bash
# Unit only (no Docker, no DB needed):
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Unit"

# Integration only (needs the dev Postgres on localhost:5544 — `docker compose up -d`):
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Integration"

# Repository only (needs Docker; Testcontainers starts its own postgres:16):
dotnet test API.Tests/API.Tests.csproj --filter "FullyQualifiedName~Repository"

# Everything:
dotnet test API.Tests/API.Tests.csproj
```

Captured terminal output for each of these runs — plus the deliberate-failure demo
described in Part 8 — lives in `docs/proof/`.


