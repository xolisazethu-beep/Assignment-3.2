using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareerHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalaryAndExpiryCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_job_listings_expires_after_created",
                table: "job_listings",
                sql: "\"ExpiresAt\" > \"CreatedAt\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_listings_salary_max_gt_min",
                table: "job_listings",
                sql: "\"SalaryMax\" IS NULL OR \"SalaryMin\" IS NULL OR \"SalaryMax\" > \"SalaryMin\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_listings_salary_min_positive",
                table: "job_listings",
                sql: "\"SalaryMin\" IS NULL OR \"SalaryMin\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_applications_submitted_not_future",
                table: "applications",
                sql: "\"SubmittedAt\" <= now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_job_listings_expires_after_created",
                table: "job_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_listings_salary_max_gt_min",
                table: "job_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_listings_salary_min_positive",
                table: "job_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_applications_submitted_not_future",
                table: "applications");
        }
    }
}
