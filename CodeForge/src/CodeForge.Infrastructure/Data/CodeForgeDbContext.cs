using CodeForge.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeForge.Infrastructure.Data;

public class CodeForgeDbContext : DbContext
{
    public CodeForgeDbContext(DbContextOptions<CodeForgeDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Job => Set<Job>();
}
