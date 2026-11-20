using CodeForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeForge.Infrastructure.Migrations;

[DbContext(typeof(CodeForgeDbContext))]
[Migration("20261110173000_AddStaticAnalysis")]
public partial class AddStaticAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ComplexityAnalysisJson",
            table: "Job",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DependencyAnalysisJson",
            table: "Job",
            type: "jsonb",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ComplexityAnalysisJson", table: "Job");
        migrationBuilder.DropColumn(name: "DependencyAnalysisJson", table: "Job");
    }
}
