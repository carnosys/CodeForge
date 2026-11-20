using CodeForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeForge.Infrastructure.Migrations;

[DbContext(typeof(CodeForgeDbContext))]
[Migration("20261110160000_AddJobTestSummary")]
public partial class AddJobTestSummary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "RunTests",
            table: "Job",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "TestExecuted",
            table: "Job",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TestFailed",
            table: "Job",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "TestIsPartial",
            table: "Job",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TestOutcome",
            table: "Job",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TestPassed",
            table: "Job",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TestSkipped",
            table: "Job",
            type: "integer",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RunTests", table: "Job");
        migrationBuilder.DropColumn(name: "TestExecuted", table: "Job");
        migrationBuilder.DropColumn(name: "TestFailed", table: "Job");
        migrationBuilder.DropColumn(name: "TestIsPartial", table: "Job");
        migrationBuilder.DropColumn(name: "TestOutcome", table: "Job");
        migrationBuilder.DropColumn(name: "TestPassed", table: "Job");
        migrationBuilder.DropColumn(name: "TestSkipped", table: "Job");
    }
}
