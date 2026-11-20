using CodeForge.Dtos;
using CodeForge.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CodeForge.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
    {
        var email = request.Email.Trim();
        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Email already registered",
                Detail = "An account with this email address already exists.",
                Status = StatusCodes.Status409Conflict
            });
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            CreatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Email already registered",
                    Detail = "An account with this email address already exists.",
                    Status = StatusCodes.Status409Conflict
                });
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(nameof(request.Password), error.Description);
            }

            return ValidationProblem(ModelState);
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return StatusCode(
            StatusCodes.Status201Created,
            new CurrentUserDto(user.Id, user.Email!));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return InvalidCredentials();
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);

        return result.Succeeded
            ? Ok(new CurrentUserDto(user.Id, user.Email!))
            : InvalidCredentials();
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !user.IsActive)
        {
            await signInManager.SignOutAsync();
            return Unauthorized();
        }

        return Ok(new CurrentUserDto(user.Id, user.Email!));
    }

    private UnauthorizedObjectResult InvalidCredentials() => Unauthorized(new ProblemDetails
    {
        Title = "Invalid credentials",
        Detail = "The email address or password is incorrect.",
        Status = StatusCodes.Status401Unauthorized
    });
}
