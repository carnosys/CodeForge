using CodeForge.Infrastructure.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Infrastructure.Data;

public class CodeForgeDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    public CodeForgeDbContext(DbContextOptions<CodeForgeDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Job => Set<Job>();
    public DbSet<OutboxMessage> OutboxMessage => Set<OutboxMessage>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>()
            .HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique();

        modelBuilder.Entity<Job>()
            .HasOne(job => job.Owner)
            .WithMany(user => user.Jobs)
            .HasForeignKey(job => job.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

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
