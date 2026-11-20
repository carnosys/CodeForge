using CodeForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeForge.Infrastructure.Migrations;

[DbContext(typeof(CodeForgeDbContext))]
[Migration("20261111160000_AddDotNetArchitecture")]
public partial class AddDotNetArchitecture : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("DotNetArchitectureJson", "Job", "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("DotNetArchitectureJson", "Job");
}
