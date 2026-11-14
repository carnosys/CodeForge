using CodeForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeForge.Infrastructure.Migrations;

[DbContext(typeof(CodeForgeDbContext))]
[Migration("20261111113000_AddBuildFindings")]
public partial class AddBuildFindings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BuildFindingsJson",
            table: "Job",
            type: "jsonb",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "BuildFindingsJson", table: "Job");
    }
}
