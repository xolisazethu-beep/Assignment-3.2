using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace CareerHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJobListingAndApplicationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_job_listings_CompanyId",
                table: "job_listings");

            migrationBuilder.DropIndex(
                name: "IX_applications_ApplicantId",
                table: "applications");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "job_listings",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "Description" });

            migrationBuilder.CreateIndex(
                name: "ix_job_listings_companyid_status",
                table: "job_listings",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_job_listings_search_vector",
                table: "job_listings",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_job_listings_status_expiresat",
                table: "job_listings",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ix_applications_applicantid_joblistingid",
                table: "applications",
                columns: new[] { "ApplicantId", "JobListingId" });

            migrationBuilder.CreateIndex(
                name: "ix_applications_joblistingid_submittedat",
                table: "applications",
                columns: new[] { "JobListingId", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_job_listings_companyid_status",
                table: "job_listings");

            migrationBuilder.DropIndex(
                name: "ix_job_listings_search_vector",
                table: "job_listings");

            migrationBuilder.DropIndex(
                name: "ix_job_listings_status_expiresat",
                table: "job_listings");

            migrationBuilder.DropIndex(
                name: "ix_applications_applicantid_joblistingid",
                table: "applications");

            migrationBuilder.DropIndex(
                name: "ix_applications_joblistingid_submittedat",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "job_listings");

            migrationBuilder.CreateIndex(
                name: "IX_job_listings_CompanyId",
                table: "job_listings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_ApplicantId",
                table: "applications",
                column: "ApplicantId");
        }
    }
}
