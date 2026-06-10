using CareerHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Data;

public class CareerHubDbContext(DbContextOptions<CareerHubDbContext> options) : DbContext(options)
{
    public DbSet<JobListing> JobListings => Set<JobListing>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Applicant> Applicants => Set<Applicant>();
    public DbSet<Employer> Employers => Set<Employer>();
    public DbSet<Application> Applications => Set<Application>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── COMPANY ──────────────────────────────────────────────────────────
        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("companies");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();

            entity.Property(c => c.Name).IsRequired().HasMaxLength(150);
            entity.Property(c => c.City).IsRequired().HasMaxLength(100);
            entity.Property(c => c.Province).HasMaxLength(100);
            entity.Property(c => c.Industry).HasMaxLength(100);
            entity.Property(c => c.Website).HasMaxLength(300);

            entity.HasIndex(c => c.Name).IsUnique();
        });

        // ── APPLICANT ────────────────────────────────────────────────────────
        modelBuilder.Entity<Applicant>(entity =>
        {
            entity.ToTable("applicants");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();

            entity.Property(a => a.FullName).IsRequired().HasMaxLength(150);
            entity.Property(a => a.Email).IsRequired().HasMaxLength(200);
            entity.Property(a => a.PasswordHash).IsRequired().HasMaxLength(500);
            entity.Property(a => a.City).HasMaxLength(100);
            entity.Property(a => a.Headline).HasMaxLength(200);
            entity.Property(a => a.Qualifications).HasMaxLength(1000);

            entity.HasIndex(a => a.Email).IsUnique();
        });

        // ── EMPLOYER (recruiter account) ─────────────────────────────────────
        modelBuilder.Entity<Employer>(entity =>
        {
            entity.ToTable("employers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.FullName).IsRequired().HasMaxLength(150);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(500);

            // Unique within employers. NOTE: this cannot stop an email from also
            // existing in `applicants` — that cross-table rule is enforced in
            // AuthService at registration time.
            entity.HasIndex(e => e.Email).IsUnique();

            // Many recruiters may belong to one company. Restrict so removing a
            // company can't silently orphan/delete its recruiters (companies are
            // seed-only here anyway).
            entity.HasOne(e => e.Company)
                  .WithMany()
                  .HasForeignKey(e => e.CompanyId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.CompanyId)
                  .HasDatabaseName("ix_employers_companyid");
        });

        // ── JOB LISTING ──────────────────────────────────────────────────────
        modelBuilder.Entity<JobListing>(entity =>
        {
            entity.ToTable("job_listings", t =>
            {
                // ── PART 2: CHECK CONSTRAINTS ─────────────────────────────────
                // Enforced by PostgreSQL itself, so a bad row is rejected even
                // when the service layer is bypassed (raw INSERT, batch script,
                // a future second writer). See README "Constraint decisions".

                // A salary floor of R0 or negative is meaningless. NULL is allowed
                // ("market related" adverts omit the figure).
                t.HasCheckConstraint(
                    "ck_job_listings_salary_min_positive",
                    "\"SalaryMin\" IS NULL OR \"SalaryMin\" > 0");

                // Max must exceed min when BOTH are supplied. The NULL guards make
                // a half-open or absent range legal, but an inverted range illegal.
                t.HasCheckConstraint(
                    "ck_job_listings_salary_max_gt_min",
                    "\"SalaryMax\" IS NULL OR \"SalaryMin\" IS NULL OR \"SalaryMax\" > \"SalaryMin\"");

                // A listing cannot expire before (or at) the moment it was created.
                t.HasCheckConstraint(
                    "ck_job_listings_expires_after_created",
                    "\"ExpiresAt\" > \"CreatedAt\"");
            });

            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).ValueGeneratedNever();

            entity.Property(j => j.Title).IsRequired().HasMaxLength(200);
            entity.Property(j => j.Description).IsRequired().HasMaxLength(4000);
            entity.Property(j => j.MinimumRequirements).IsRequired().HasMaxLength(2000);
            entity.Property(j => j.Location).IsRequired().HasMaxLength(200);
            entity.Property(j => j.Type).IsRequired().HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.SalaryMin).HasColumnType("numeric(18,2)");
            entity.Property(j => j.SalaryMax).HasColumnType("numeric(18,2)");
            entity.Property(j => j.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.CreatedAt).IsRequired();
            entity.Property(j => j.ExpiresAt).IsRequired();

            entity.HasOne(j => j.Company)
                  .WithMany(c => c.JobListings)
                  .HasForeignKey(j => j.CompanyId)
                  .OnDelete(DeleteBehavior.Restrict);

            // ── PART 5: STORED GENERATED tsvector COLUMN ──────────────────────
            // PostgreSQL maintains SearchVector from Title + Description using the
            // 'english' configuration on every write. Computed in the DATABASE,
            // never in application code.
            entity.HasGeneratedTsVectorColumn(
                      j => j.SearchVector,
                      "english",
                      j => new { j.Title, j.Description })
                  .HasIndex(j => j.SearchVector)        // ── PART 3: GIN INDEX ──
                  .HasMethod("gin")
                  .HasDatabaseName("ix_job_listings_search_vector");

            // ── PART 3: COMPOSITE B-TREE INDEXES ──────────────────────────────
            // Supports GetActiveListingsAsync: WHERE Status = 'Active' AND
            // ExpiresAt > now(). Equality column (Status) leads; range column
            // (ExpiresAt) follows. See README "Index decisions".
            entity.HasIndex(j => new { j.Status, j.ExpiresAt })
                  .HasDatabaseName("ix_job_listings_status_expiresat");

            // Supports GetByCompanyAsync: WHERE CompanyId = X AND Status = 'Active'.
            // The high-selectivity equality (CompanyId) leads.
            entity.HasIndex(j => new { j.CompanyId, j.Status })
                  .HasDatabaseName("ix_job_listings_companyid_status");
        });

        // ── APPLICATION (explicit join entity) ───────────────────────────────
        modelBuilder.Entity<Application>(entity =>
        {
            entity.ToTable("applications", t =>
            {
                // ── PART 2: CHECK CONSTRAINT ──────────────────────────────────
                // An application cannot be backdated into the future.
                t.HasCheckConstraint(
                    "ck_applications_submitted_not_future",
                    "\"SubmittedAt\" <= now()");
            });

            // Composite PK = natural identity. "One application per person per
            // listing" is therefore a database guarantee, and the PK's B-tree
            // (JobListingId first) already indexes the employer-dashboard lookup
            // by listing — so we add only the indexes the PK does NOT cover.
            entity.HasKey(a => new { a.JobListingId, a.ApplicantId });

            entity.Property(a => a.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.SubmittedAt).IsRequired();
            entity.Property(a => a.CoverNote).HasMaxLength(2000);

            entity.HasOne(a => a.JobListing)
                  .WithMany(j => j.Applications)
                  .HasForeignKey(a => a.JobListingId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Applicant)
                  .WithMany(ap => ap.Applications)
                  .HasForeignKey(a => a.ApplicantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // ── PART 3: APPLICATION INDEXES ───────────────────────────────────
            // Supports HasAppliedAsync: WHERE ApplicantId = X AND JobListingId = Y.
            // The composite PK leads with JobListingId, so a probe by applicant is
            // not covered by it; this applicant-first index is. It also serves
            // "all applications by this applicant".
            entity.HasIndex(a => new { a.ApplicantId, a.JobListingId })
                  .HasDatabaseName("ix_applications_applicantid_joblistingid");

            // Supports the employer dashboard: all applications for a listing,
            // newest first. Adding SubmittedAt lets PostgreSQL return rows already
            // ordered, avoiding a Sort node the bare PK would force.
            entity.HasIndex(a => new { a.JobListingId, a.SubmittedAt })
                  .HasDatabaseName("ix_applications_joblistingid_submittedat");
        });
    }
}
