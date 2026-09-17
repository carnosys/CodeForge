using Microsoft.EntityFrameworkCore;
using Models.Job;
public class CodeForgeDbContext: DbContext
{
    public CodeForgeDbContext(DbContextOptions<CodeForgeDbContext> options): base(options)
    {
        
    }

    public DbSet<Job> Job {get; set;}
}