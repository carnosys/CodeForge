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
            .HasOne(j => j.OutboxMessage)
            .WithOne(m => m.Job)
            .HasForeignKey<OutboxMessage>(m => m.JobId)
            .OnDelete(DeleteBehavior.Cascade); // Automatically deletes outbox message if job is deleted
    }

}
