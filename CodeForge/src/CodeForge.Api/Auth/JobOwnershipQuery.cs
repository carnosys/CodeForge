using CodeForge.Infrastructure.Models;

namespace CodeForge.Api.Auth;

public static class JobOwnershipQuery
{
    public static IQueryable<Job> OwnedBy(this IQueryable<Job> jobs, Guid userId) =>
        jobs.Where(job => job.OwnerId == userId);
}
