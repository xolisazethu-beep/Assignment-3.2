using CareerHub.Api.Models;
using CareerHub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CareerHub.Api.Data;

/// <summary>
/// Seeds a realistic South African dataset: 10 well-known SA employers, listings
/// located in SA towns with salaries in Rand (ZAR), and a spread of applications
/// across every pipeline status. Designed so the Assignment 2.4 proofs work:
///   • 200+ listings across 10 companies, mixed Active/Closed, varied expiry.
///   • The word "Kubernetes" appears in EXACTLY 3 active listings (full-text proof).
///   • One listing's description contains "sprinting" (stemming demo: search "sprint").
///   • The first company has several listings with applications in multiple
///     statuses and a clear rank-1 listing (RANK() statistics proof).
/// Deterministic: a fixed-seed Random makes every run reproducible.
/// </summary>
public static class SeedData
{
    public static async Task SeedAsync(CareerHubDbContext db)
    {
        if (await db.Companies.AnyAsync())
            return; // already seeded

        var rng = new Random(2024);
        var now = DateTime.UtcNow;

        // ── 10 REAL SOUTH AFRICAN EMPLOYERS ──────────────────────────────────
        var companies = new[]
        {
            New("Takealot",       "Cape Town",   "Western Cape", "E-commerce",        "https://www.takealot.com", 2011),
            New("Discovery",      "Sandton",     "Gauteng",      "Insurance",         "https://www.discovery.co.za", 1992),
            New("Standard Bank",  "Johannesburg","Gauteng",      "Banking",           "https://www.standardbank.co.za", 1862),
            New("Capitec Bank",   "Stellenbosch","Western Cape", "Banking",           "https://www.capitecbank.co.za", 2001),
            New("Naspers",        "Cape Town",   "Western Cape", "Technology",        "https://www.naspers.com", 1915),
            New("Vodacom",        "Midrand",     "Gauteng",      "Telecommunications","https://www.vodacom.co.za", 1994),
            New("Shoprite",       "Brackenfell", "Western Cape", "Retail",            "https://www.shoprite.co.za", 1979),
            New("Sasol",          "Secunda",     "Mpumalanga",   "Energy & Chemicals","https://www.sasol.com", 1950),
            New("MTN",            "Roodepoort",  "Gauteng",      "Telecommunications","https://www.mtn.co.za", 1994),
            New("Yoco",           "Cape Town",   "Western Cape", "Fintech",           "https://www.yoco.com", 2013),
        };
        db.Companies.AddRange(companies);

        // ── APPLICANTS (SA names) ────────────────────────────────────────────
        string[] firstNames = ["Thabo", "Lerato", "Sipho", "Nomvula", "Johan", "Aisha", "Pieter", "Zanele",
            "Kagiso", "Naledi", "Riaan", "Fatima", "Bongani", "Anele", "Mandla", "Refilwe",
            "Tshepo", "Lindiwe", "Ayanda", "Nkosana", "Chad", "Precious", "Sanele", "Karabo", "Mpho"];
        string[] lastNames = ["Nkosi", "Botha", "Dlamini", "Mokoena", "Naidoo", "Van der Merwe", "Khumalo",
            "Pillay", "Mthembu", "Jacobs", "Ndlovu", "Maritz", "Cele", "Singh", "Mahlangu"];

        var applicants = new List<Applicant>();
        for (int i = 0; i < 25; i++)
        {
            var fn = firstNames[i % firstNames.Length];
            var ln = lastNames[rng.Next(lastNames.Length)];
            var years = rng.Next(0, 15);
            applicants.Add(new Applicant
            {
                Id = Guid.NewGuid(),
                FullName = $"{fn} {ln}",
                Email = $"{fn.ToLower()}.{ln.Replace(" ", "").ToLower()}{i}@example.co.za",
                PasswordHash = "seed-pbkdf2-placeholder",
                City = SaTowns[rng.Next(SaTowns.Length)],
                Headline = $"{fn} — {Roles[rng.Next(Roles.Length)]}",
                YearsOfExperience = years,
                Qualifications = QualificationsFor(rng, years)
            });
        }
        db.Applicants.AddRange(applicants);

        // ── 200 BULK LISTINGS (20 per company), mixed status + expiry ─────────
        // 600 listings per company => 6,000 rows. A meaningful EXPLAIN ANALYZE needs
        // volume AND selectivity: only ~25% are Active and roughly half of those are
        // still unexpired, so the active-board query touches ~12% of the table —
        // selective enough that PostgreSQL prefers the index over a Seq Scan.
        const int listingsPerCompany = 600;
        var listings = new List<JobListing>();
        foreach (var company in companies)
        {
            for (int i = 0; i < listingsPerCompany; i++)
            {
                var seniority = Seniorities[rng.Next(Seniorities.Length)];
                var role = Roles[rng.Next(Roles.Length)];
                var (min, max) = SalaryFor(seniority, rng);
                var town = SaTowns[rng.Next(SaTowns.Length)];
                var createdAt = now.AddDays(-rng.Next(1, 120));
                var expiresAt = createdAt.AddDays(rng.Next(7, 90)); // some future, some already past
                var isActive = rng.NextDouble() < 0.25;            // ~25% Active, ~75% Closed

                listings.Add(new JobListing
                {
                    Id = Guid.NewGuid(),
                    Title = $"{seniority} {role}",
                    Description = DescriptionFor(company.Name, seniority, role, town, min, max),
                    MinimumRequirements = RequirementsFor(seniority, role),
                    Location = $"{town}, South Africa",
                    Type = (JobType)rng.Next(0, 5),
                    SalaryMin = min,
                    SalaryMax = max,
                    Status = isActive ? ListingStatus.Active : ListingStatus.Closed,
                    CreatedAt = createdAt,
                    ExpiresAt = expiresAt,
                    CompanyId = company.Id
                });
            }
        }

        // ── 3 SPECIAL "Kubernetes" LISTINGS (exactly three; full-text proof) ──
        var kubeCompanies = new[] { companies[0], companies[4], companies[9] }; // Takealot, Naspers, Yoco
        foreach (var c in kubeCompanies)
        {
            listings.Add(new JobListing
            {
                Id = Guid.NewGuid(),
                Title = "Senior Platform Engineer (Kubernetes)",
                Description =
                    $"{c.Name} seeks a platform engineer to run our Kubernetes clusters across multiple regions. " +
                    "Experience with container orchestration and Helm is essential.",
                MinimumRequirements =
                    "Matric plus a relevant IT qualification; 5+ years running production Kubernetes; " +
                    "Helm, Terraform and CI/CD pipeline experience; valid South African work authorisation.",
                Location = $"{c.City}, South Africa",
                Type = JobType.FullTime,
                SalaryMin = 90_000m,
                SalaryMax = 140_000m,
                Status = ListingStatus.Active,
                CreatedAt = now.AddDays(-5),
                ExpiresAt = now.AddDays(40),
                CompanyId = c.Id
            });
        }

        // ── 1 "sprinting" LISTING (stemming demo: search "sprint" matches it) ──
        listings.Add(new JobListing
        {
            Id = Guid.NewGuid(),
            Title = "Scrum Master",
            Description =
                "Lead a delivery squad that keeps sprinting towards ambitious quarterly goals. " +
                "You will facilitate ceremonies and remove blockers for the engineering team.",
            MinimumRequirements =
                "Matric; 3+ years as a Scrum Master or Agile delivery lead; recognised Scrum certification " +
                "(CSM/PSM) advantageous; strong facilitation and stakeholder-management skills.",
            Location = "Durban, South Africa",
            Type = JobType.FullTime,
            SalaryMin = 65_000m,
            SalaryMax = 95_000m,
            Status = ListingStatus.Active,
            CreatedAt = now.AddDays(-3),
            ExpiresAt = now.AddDays(50),
            CompanyId = companies[1].Id
        });

        // ── 8 CURATED, HAND-WRITTEN LISTINGS ─────────────────────────────────
        // Realistic adverts (not templated) across different SA cities, job types
        // and Rand salary bands — the kind of detail a real employer would post.
        // All Active and unexpired so they appear on the public board immediately.
        var curated = new[]
        {
            Curated(companies[2], // Standard Bank
                "Junior Software Developer", JobType.FullTime, "Johannesburg, Gauteng", 15_000m, 25_000m,
                "We are looking for a motivated junior developer to join our digital banking team. " +
                "You will work on building and maintaining customer-facing web applications, fixing bugs, " +
                "and learning from senior engineers in a supportive Agile environment.",
                "Bachelor's degree in Computer Science or a related field. Knowledge of JavaScript and basic REST APIs. " +
                "Matric with mathematics. Valid South African ID. Willingness to learn C# and .NET."),

            Curated(companies[0], // Takealot
                "Warehouse Operations Manager", JobType.FullTime, "Kempton Park, Gauteng", 45_000m, 65_000m,
                "Takealot's largest distribution centre is seeking an Operations Manager to lead a team of 80+ " +
                "pickers, packers and drivers. You will own daily throughput targets, shift planning and safety compliance " +
                "during our peak online-shopping seasons.",
                "Matric plus a Diploma/Degree in Supply Chain, Logistics or Operations Management. 5+ years' warehouse " +
                "management experience. Familiarity with WMS systems. Valid driver's licence and own transport."),

            Curated(companies[9], // Yoco
                "Mobile App Developer (Flutter)", JobType.Contract, "Cape Town, Western Cape", 55_000m, 85_000m,
                "Join Yoco's product team on a 12-month contract to build new features for our payments app used by " +
                "thousands of small South African businesses. You will ship cross-platform Flutter code and collaborate " +
                "closely with designers and backend engineers.",
                "3+ years' mobile development experience, at least 1 year with Flutter/Dart. Solid understanding of REST APIs " +
                "and Git. Matric and a relevant tertiary qualification. Portfolio of published apps advantageous."),

            Curated(companies[1], // Discovery
                "Actuarial Analyst", JobType.FullTime, "Sandton, Gauteng", 40_000m, 70_000m,
                "Discovery Health is hiring an Actuarial Analyst to support pricing and reserving for our medical-aid products. " +
                "You will build and validate models, analyse claims data and present findings to senior actuaries.",
                "BSc/BCom in Actuarial Science. Progress through actuarial exams (at least 3 exemptions). Strong Excel and " +
                "R or Python skills. Matric with distinction in mathematics. Excellent analytical and communication skills."),

            Curated(companies[5], // Vodacom
                "Network Engineer", JobType.FullTime, "Midrand, Gauteng", 50_000m, 80_000m,
                "Vodacom is seeking a Network Engineer to maintain and optimise our 4G/5G core network across Gauteng. " +
                "You will monitor performance, troubleshoot incidents and roll out capacity upgrades to keep millions of " +
                "subscribers connected.",
                "Degree in Electrical/Computer Engineering or equivalent. CCNP or equivalent certification. 4+ years in " +
                "telecoms networking. Knowledge of routing, switching and LTE/5G core. Valid driver's licence."),

            Curated(companies[6], // Shoprite
                "Retail Store Learnership", JobType.Learnership, "Bloemfontein, Free State", 5_000m, 6_000m,
                "Shoprite's 12-month retail learnership gives unemployed youth hands-on experience in store operations, " +
                "customer service and merchandising, paired with an accredited NQF Level 3 qualification and a monthly stipend.",
                "South African citizen aged 18–28. Matric (Grade 12) certificate. Currently unemployed and not studying " +
                "full-time. No prior experience required — full training provided."),

            Curated(companies[7], // Sasol
                "Chemical Process Engineer", JobType.FullTime, "Secunda, Mpumalanga", 60_000m, 95_000m,
                "Sasol's Secunda complex is recruiting a Process Engineer to optimise our synthetic-fuels production lines. " +
                "You will analyse plant data, lead efficiency projects and ensure operations meet strict safety and " +
                "environmental standards.",
                "BEng/BSc in Chemical Engineering, registered (or eligible) with ECSA. 3+ years' plant experience in " +
                "petrochemicals or energy. Matric with mathematics and physical science. Willingness to relocate to Secunda."),

            Curated(companies[4], // Naspers
                "UX/UI Designer", JobType.PartTime, "Stellenbosch, Western Cape", 30_000m, 45_000m,
                "Naspers Labs needs a part-time UX/UI Designer (3 days/week) to craft intuitive interfaces for our early-stage " +
                "ventures. You will run user research, build wireframes and prototypes in Figma, and partner with developers " +
                "to bring designs to life.",
                "Diploma/Degree in Design, HCI or related field. 2+ years' product-design experience with a strong portfolio. " +
                "Proficiency in Figma. Matric. Understanding of accessibility and mobile-first design."),
        };
        listings.AddRange(curated);

        db.JobListings.AddRange(listings);

        // ── APPLICATIONS ─────────────────────────────────────────────────────
        // Give Takealot (companies[0]) a clear statistics story: its first three
        // ACTIVE listings get a descending number of applications across statuses,
        // so the rank-1 listing is unambiguous.
        var statuses = Enum.GetValues<ApplicationStatus>();
        var takealotActive = listings
            .Where(l => l.CompanyId == companies[0].Id && l.Status == ListingStatus.Active)
            .Take(3).ToList();

        int[] appCounts = [18, 11, 5]; // listing 1 > listing 2 > listing 3
        for (int li = 0; li < takealotActive.Count; li++)
        {
            var listing = takealotActive[li];
            var shuffled = applicants.OrderBy(_ => rng.Next()).Take(appCounts[li]).ToList();
            foreach (var applicant in shuffled)
            {
                db.Applications.Add(new Application
                {
                    JobListingId = listing.Id,
                    ApplicantId = applicant.Id,
                    Status = statuses[rng.Next(statuses.Length)],
                    SubmittedAt = now.AddDays(-rng.Next(1, 30)),
                    CoverNote = "I am excited to apply and believe my experience is a strong fit."
                });
            }
        }

        // Scatter a few applications across other companies' active listings too.
        var otherActive = listings
            .Where(l => l.CompanyId != companies[0].Id && l.Status == ListingStatus.Active)
            .OrderBy(_ => rng.Next()).Take(40).ToList();
        foreach (var listing in otherActive)
        {
            var n = rng.Next(0, 4);
            var pickers = applicants.OrderBy(_ => rng.Next()).Take(n);
            foreach (var applicant in pickers)
            {
                db.Applications.Add(new Application
                {
                    JobListingId = listing.Id,
                    ApplicantId = applicant.Id,
                    Status = statuses[rng.Next(statuses.Length)],
                    SubmittedAt = now.AddDays(-rng.Next(1, 30)),
                    CoverNote = "Please find my application attached."
                });
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Login-ready demo accounts (one applicant, one employer) with REAL PBKDF2
    /// hashes — unlike the 25 bulk-seeded applicants whose placeholder hashes can
    /// never pass <see cref="PasswordHasher.Verify"/>. Run separately from
    /// <see cref="SeedAsync"/> and guarded per-account, so it also populates
    /// databases that were seeded before auth existed. The employer is bound to
    /// Takealot, which already carries the rank-1 statistics story.
    /// </summary>
    public static async Task SeedDemoAccountsAsync(CareerHubDbContext db)
    {
        const string demoPassword = "DemoPass123!";
        const string applicantEmail = "demo.applicant@careerhub.co.za";
        const string employerEmail = "demo.employer@takealot.co.za";

        if (!await db.Applicants.AnyAsync(a => a.Email == applicantEmail))
        {
            db.Applicants.Add(new Applicant
            {
                Id = Guid.NewGuid(),
                FullName = "Demo Applicant",
                Email = applicantEmail,
                PasswordHash = PasswordHasher.Hash(demoPassword),
                City = "Cape Town",
                Headline = "Demo job seeker — Platform Engineer",
                YearsOfExperience = 6,
                // Kubernetes-flavoured so the employer applicant-search demo
                // (?qualification=kubernetes) deterministically finds this account.
                Qualifications =
                    "BSc Computer Science (UCT); Certified Kubernetes Administrator (CKA); " +
                    "Skills: Kubernetes, Docker, Terraform, AWS, C#, PostgreSQL; 6 years' experience."
            });
        }

        if (!await db.Employers.AnyAsync(e => e.Email == employerEmail))
        {
            // Bind to Takealot (the company with the rank-1 stats story). It exists
            // whenever the main seed has run; guard so a half-seeded DB doesn't throw.
            var takealot = await db.Companies.FirstOrDefaultAsync(c => c.Name == "Takealot");
            if (takealot is not null)
            {
                db.Employers.Add(new Employer
                {
                    Id = Guid.NewGuid(),
                    FullName = "Demo Recruiter",
                    Email = employerEmail,
                    PasswordHash = PasswordHasher.Hash(demoPassword),
                    CompanyId = takealot.Id
                });
            }
        }

        await db.SaveChangesAsync();

        // ── DEMO APPLICATIONS (so "track my applications" is demonstrable) ────
        // Give the demo applicant one application in EACH friendly stage so
        // GET /applications/me, /me/summary and /me/{id} all return rich data out
        // of the box. Guarded on "does the demo applicant already have any?" so it
        // stays idempotent across restarts. One of these targets a Takealot listing
        // so the demo EMPLOYER's applicant-search also finds the demo applicant.
        var demoApplicant = await db.Applicants.FirstOrDefaultAsync(a => a.Email == applicantEmail);

        // Backfill: a demo applicant created BEFORE the Qualifications column existed
        // would have an empty value, which would break the employer applicant-search
        // demo. Give it the Kubernetes-flavoured profile so the demo works on upgraded
        // databases too, without needing a full reseed.
        if (demoApplicant is not null && string.IsNullOrWhiteSpace(demoApplicant.Qualifications))
        {
            demoApplicant.Qualifications =
                "BSc Computer Science (UCT); Certified Kubernetes Administrator (CKA); " +
                "Skills: Kubernetes, Docker, Terraform, AWS, C#, PostgreSQL; 6 years' experience.";
            await db.SaveChangesAsync();
        }

        if (demoApplicant is not null &&
            !await db.Applications.AnyAsync(a => a.ApplicantId == demoApplicant.Id))
        {
            var takealot = await db.Companies.FirstOrDefaultAsync(c => c.Name == "Takealot");
            var now = DateTime.UtcNow;

            // Prefer Takealot's active listings first (so the employer demo finds
            // this applicant), then top up from any other active listings.
            var targets = await db.JobListings
                .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now
                            && takealot != null && j.CompanyId == takealot.Id)
                .OrderBy(j => j.CreatedAt)
                .Take(2)
                .Select(j => j.Id)
                .ToListAsync();

            var more = await db.JobListings
                .Where(j => j.Status == ListingStatus.Active && j.ExpiresAt > now
                            && !targets.Contains(j.Id))
                .OrderBy(j => j.CreatedAt)
                .Take(4 - targets.Count)
                .Select(j => j.Id)
                .ToListAsync();
            targets.AddRange(more);

            // One application per friendly stage: Applied, Pending, Accepted, Rejected.
            ApplicationStatus[] demoStatuses =
                [ApplicationStatus.Submitted, ApplicationStatus.UnderReview,
                 ApplicationStatus.Offered, ApplicationStatus.Rejected];

            for (int i = 0; i < targets.Count && i < demoStatuses.Length; i++)
            {
                db.Applications.Add(new Application
                {
                    JobListingId = targets[i],
                    ApplicantId = demoApplicant.Id,
                    Status = demoStatuses[i],
                    SubmittedAt = now.AddDays(-(i + 1) * 3),
                    CoverNote = "Demo application — keen to contribute and grow with the team."
                });
            }

            await db.SaveChangesAsync();
        }
    }

    // ── lookup data / helpers ────────────────────────────────────────────────
    private static readonly string[] SaTowns =
        ["Cape Town", "Johannesburg", "Pretoria", "Durban", "Gqeberha", "Polokwane",
         "Bloemfontein", "Mbombela", "Kimberley", "East London", "Stellenbosch",
         "Sandton", "Midrand", "Centurion", "Soweto"];

    private static readonly string[] Seniorities =
        ["Graduate", "Junior", "Intermediate", "Senior", "Lead", "Principal"];

    private static readonly string[] Roles =
        ["Software Developer", "Data Analyst", "Backend Engineer", "Frontend Engineer",
         "DevOps Engineer", "Product Manager", "QA Engineer", "Mobile Developer",
         "Database Administrator", "Business Analyst", "UX Designer",
         "Site Reliability Engineer", "Systems Engineer", "Cloud Architect", "Security Analyst"];

    // ── Applicant qualifications (what an employer searches when shortlisting) ──
    private static readonly string[] Degrees =
        ["BSc Computer Science", "BSc Information Technology", "BCom Information Systems",
         "BEng Electrical Engineering", "National Diploma in IT", "BSc Honours Data Science",
         "Matric (NSC) with IT", "BTech Software Development"];

    private static readonly string[] Institutions =
        ["UCT", "Wits", "Stellenbosch University", "University of Pretoria", "UKZN",
         "Tshwane University of Technology", "Rhodes University", "UJ"];

    private static readonly string[] Certifications =
        ["AWS Certified Solutions Architect", "Microsoft Certified: Azure Developer",
         "Certified Kubernetes Administrator (CKA)", "Professional Scrum Master (PSM I)",
         "Oracle Certified Professional, Java", "CompTIA Security+",
         "Google Professional Data Engineer", "Cisco CCNA", "none"];

    private static readonly string[] SkillSets =
        ["C#, .NET, PostgreSQL, REST APIs", "Python, Pandas, SQL, Power BI",
         "Java, Spring Boot, Kafka, MySQL", "JavaScript, React, TypeScript, Node.js",
         "Kubernetes, Docker, Terraform, AWS", "Flutter, Dart, Firebase",
         "Azure, C#, DevOps pipelines", "SQL Server, T-SQL, SSIS, data modelling",
         "Go, gRPC, microservices, Linux"];

    // Build a realistic, searchable qualifications string: a degree from an SA
    // institution, an optional certification, a skills list, and the years of
    // experience — the free-text column the employer applicant-search runs ILIKE on.
    private static string QualificationsFor(Random rng, int years)
    {
        var degree = Degrees[rng.Next(Degrees.Length)];
        var institution = Institutions[rng.Next(Institutions.Length)];
        var cert = Certifications[rng.Next(Certifications.Length)];
        var skills = SkillSets[rng.Next(SkillSets.Length)];

        var certPart = cert == "none" ? "" : $"{cert}; ";
        return $"{degree} ({institution}); {certPart}Skills: {skills}; {years} years' experience.";
    }

    // Factory for the hand-written curated listings: Active, created a few days ago
    // and expiring well in the future so they always show on the public board.
    private static JobListing Curated(
        Company company, string title, JobType type, string location,
        decimal? salaryMin, decimal? salaryMax, string description, string requirements) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            MinimumRequirements = requirements,
            Location = location,
            Type = type,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            Status = ListingStatus.Active,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAt = DateTime.UtcNow.AddDays(60),
            CompanyId = company.Id
        };

    // Builds a realistic, SA-flavoured advert body. Salary is quoted in Rand when
    // present, or "market related" when the figures were omitted.
    private static string DescriptionFor(
        string company, string seniority, string role, string town, decimal? min, decimal? max)
    {
        var pay = min is not null && max is not null
            ? $"R{min:N0} – R{max:N0} per month (cost to company)"
            : "a market-related package";

        return
            $"{company} is hiring a {seniority} {role} to join our team in {town}. " +
            $"You will work in an Agile squad building and operating scalable systems with C#, .NET and PostgreSQL, " +
            $"collaborating closely with product and design to ship features that serve customers across South Africa. " +
            $"We offer {pay}, hybrid working, medical aid and provident-fund contributions, and a generous learning budget. " +
            $"CareerHub employers are committed to Employment Equity and B-BBEE transformation goals.";
    }

    // The non-negotiables. Tailored loosely by seniority so the bar rises sensibly,
    // and grounded in SA expectations (Matric, work authorisation, EE).
    private static string RequirementsFor(string seniority, string role)
    {
        var years = seniority switch
        {
            "Graduate"     => "0–1 years'",
            "Junior"       => "1–2 years'",
            "Intermediate" => "3–5 years'",
            "Senior"       => "5–8 years'",
            "Lead"         => "8+ years'",
            "Principal"    => "10+ years'",
            _              => "relevant"
        };

        return
            $"Matric (NSC) and a relevant tertiary qualification (Diploma/Degree in IT, Computer Science or equivalent); " +
            $"{years} experience as a {role} or in a closely related role; " +
            $"proficiency with C#/.NET, SQL and version control (Git); " +
            $"valid South African ID or work permit; " +
            $"strong communication skills in English. Preference may be given to candidates supporting our Employment Equity targets.";
    }

    private static (decimal? min, decimal? max) SalaryFor(string seniority, Random rng)
    {
        // ~15% of adverts are "market related" (no figure) — exercises the NULL paths.
        if (rng.NextDouble() < 0.15) return (null, null);

        decimal baseMin = seniority switch
        {
            "Graduate"     => 22_000m,
            "Junior"       => 30_000m,
            "Intermediate" => 45_000m,
            "Senior"       => 75_000m,
            "Lead"         => 110_000m,
            "Principal"    => 140_000m,
            _              => 40_000m
        };
        var min = baseMin + rng.Next(0, 6) * 1_000m;
        var max = min + (15_000m + rng.Next(0, 20) * 1_000m);
        return (min, max);
    }

    private static Company New(string name, string city, string province,
                               string industry, string website, int founded) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            City = city,
            Province = province,
            Industry = industry,
            Website = website,
            FoundedYear = founded
        };
}
