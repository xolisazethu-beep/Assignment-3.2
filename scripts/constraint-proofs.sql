-- Assignment 2.4 — Part 2 constraint proofs.
-- Run each block with psql; every block must FAIL with a check-constraint error,
-- proving PostgreSQL enforces the rule even when the API/service layer is bypassed.
--
--   docker exec -i careerhub24-pg psql -U postgres -d CareerHub24 < scripts/constraint-proofs.sql
--
-- Replace :cid / :aid / :lid below, or run interactively. The \set lines pull live IDs.

\set cid (SELECT "Id" FROM companies WHERE "Name" = 'Takealot')
\set aid (SELECT "Id" FROM applicants LIMIT 1)
\set lid (SELECT "Id" FROM job_listings WHERE "Status" = 'Closed' LIMIT 1)

-- 1) ck_job_listings_salary_min_positive  (SalaryMin must be > 0 when provided)
INSERT INTO job_listings ("Id","Title","Description","Location","Type","SalaryMin","SalaryMax","Status","CreatedAt","ExpiresAt","CompanyId")
VALUES (gen_random_uuid(),'Bad Salary','x','Cape Town','FullTime',-5000,80000,'Active',now(),now()+interval '30 days', (SELECT "Id" FROM companies WHERE "Name"='Takealot'));

-- 2) ck_job_listings_salary_max_gt_min  (max must exceed min when both provided)
INSERT INTO job_listings ("Id","Title","Description","Location","Type","SalaryMin","SalaryMax","Status","CreatedAt","ExpiresAt","CompanyId")
VALUES (gen_random_uuid(),'Inverted Range','x','Durban','FullTime',50000,40000,'Active',now(),now()+interval '30 days', (SELECT "Id" FROM companies WHERE "Name"='Takealot'));

-- 3) ck_job_listings_expires_after_created  (expiry must be after creation)
INSERT INTO job_listings ("Id","Title","Description","Location","Type","Status","CreatedAt","ExpiresAt","CompanyId")
VALUES (gen_random_uuid(),'Time Travel','x','Pretoria','FullTime','Active',now(),now()-interval '1 day', (SELECT "Id" FROM companies WHERE "Name"='Takealot'));

-- 4) ck_applications_submitted_not_future  (cannot backdate into the future)
INSERT INTO applications ("JobListingId","ApplicantId","Status","SubmittedAt","CoverNote")
VALUES ((SELECT "Id" FROM job_listings WHERE "Status"='Closed' LIMIT 1), (SELECT "Id" FROM applicants LIMIT 1), 'Submitted', now()+interval '1 day','future app');
