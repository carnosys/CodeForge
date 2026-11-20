using Microsoft.AspNetCore.Identity;

namespace CodeForge.Infrastructure.Models;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    public ICollection<Job> Jobs { get; set; } = [];
}
