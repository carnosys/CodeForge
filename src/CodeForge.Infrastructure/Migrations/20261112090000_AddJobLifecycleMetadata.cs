using CodeForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeForge.Infrastructure.Migrations;

[DbContext(typeof(CodeForgeDbContext))]
[Migration("20261112090000_AddJobLifecycleMetadata")]
public partial class AddJobLifecycleMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int?>("AnalysisOutcome", "Job", nullable: true);
        migrationBuilder.AddColumn<int>("ProcessingStage", "Job", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<bool>("ReportReady", "Job", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>("RepositoryCommitSha", "Job", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("WorkerVersion", "Job", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<int>("TestSkipReason", "Job", nullable: true);
        migrationBuilder.Sql("UPDATE \"Job\" SET \"ReportReady\" = TRUE, \"ProcessingStage\" = 7 WHERE \"Status\" = 2;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AnalysisOutcome", "Job");
        migrationBuilder.DropColumn("ProcessingStage", "Job");
        migrationBuilder.DropColumn("ReportReady", "Job");
        migrationBuilder.DropColumn("RepositoryCommitSha", "Job");
        migrationBuilder.DropColumn("WorkerVersion", "Job");
        migrationBuilder.DropColumn("TestSkipReason", "Job");
    }
}
