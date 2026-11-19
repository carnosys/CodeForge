using System.ComponentModel.DataAnnotations;

namespace CodeForge.Dtos;

public sealed class RegisterRequestDto
{
    [Required]
    [EmailAddress]
    [StringLength(254)]
    public required string Email { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 12)]
    public required string Password { get; init; }
}

public sealed class LoginRequestDto
{
    [Required]
    [EmailAddress]
    [StringLength(254)]
    public required string Email { get; init; }

    [Required]
    [StringLength(128)]
    public required string Password { get; init; }
}

public sealed record CurrentUserDto(Guid Id, string Email);
