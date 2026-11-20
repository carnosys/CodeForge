using System.Security.Claims;

namespace CodeForge.Api.Auth;

public interface ICurrentUser
{
    Guid UserId { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid UserId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId)
                ? userId
                : throw new InvalidOperationException("The authenticated request does not contain a valid user ID.");
        }
    }
}
