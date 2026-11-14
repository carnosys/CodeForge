using CodeForge.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Infrastructure.Data;

public class CodeForgeDbContext : DbContext
{
    public CodeForgeDbContext(DbContextOptions<CodeForgeDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Job => Set<Job>();
    public DbSet<OutboxMessage> OutboxMessage => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>()
            .Property(job => job.ComplexityAnalysisJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<Job>()
            .Property(job => job.DependencyAnalysisJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<Job>()
            .Property(job => job.BuildFindingsJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<Job>()
            .Property(job => job.CoverageAnalysisJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<Job>()
            .Property(job => job.DotNetArchitectureJson)
            .HasColumnType("jsonb");

        modelBuilder.Entity<Job>()
            .OwnsOne(job => job.TestSummary, summary =>
            {
                summary.Property(result => result.Outcome).HasColumnName("TestOutcome");
                summary.Property(result => result.Executed).HasColumnName("TestExecuted");
                summary.Property(result => result.Passed).HasColumnName("TestPassed");
                summary.Property(result => result.Failed).HasColumnName("TestFailed");
                summary.Property(result => result.Skipped).HasColumnName("TestSkipped");
                summary.Property(result => result.IsPartial).HasColumnName("TestIsPartial");
                summary.Property(result => result.SkipReason).HasColumnName("TestSkipReason");
            });

        modelBuilder.Entity<Job>()
            .HasOne(j => j.OutboxMessage)
            .WithOne(m => m.Job)
            .HasForeignKey<OutboxMessage>(m => m.JobId)
            .OnDelete(DeleteBehavior.Cascade); // Automatically deletes outbox message if job is deleted
    }

}
