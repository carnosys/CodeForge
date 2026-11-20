using System.ComponentModel.DataAnnotations;
using CodeForge.Api.Auth;
using CodeForge.Api.Controllers;
using CodeForge.Dtos;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodeForge.Tests;

public sealed class AuthenticationOwnershipTests
{
    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("first.last+codeforge@example.co.uk", true)]
    [InlineData("not-an-email", false)]
    [InlineData("missing-domain@", false)]
    [InlineData("", false)]
    public void RegistrationValidatesEmailFormat(string email, bool expectedValid)
    {
        var request = new RegisterRequestDto
        {
            Email = email,
            Password = "ValidPassword1!"
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.Equal(expectedValid, valid);
    }

    [Fact]
    public void EmailNormalizationIsCaseInsensitive()
    {
        var normalizer = new UpperInvariantLookupNormalizer();

        Assert.Equal(
            normalizer.NormalizeEmail("User@Example.com"),
            normalizer.NormalizeEmail("user@example.com"));
    }

    [Fact]
    public void PasswordHasherNeverStoresThePlainTextPassword()
    {
        var user = new ApplicationUser { Email = "user@example.com", UserName = "user@example.com" };
        const string password = "ValidPassword1!";
        var hasher = new PasswordHasher<ApplicationUser>();

        var hash = hasher.HashPassword(user, password);

        Assert.NotEqual(password, hash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(user, hash, password));
    }

    [Fact]
    public void DatabaseModelRequiresJobOwnerAndUniqueNormalizedEmail()
    {
        var options = new DbContextOptionsBuilder<CodeForgeDbContext>()
            .UseNpgsql("Host=localhost;Database=codeforge-model-test;Username=test;Password=test")
            .Options;
        using var context = new CodeForgeDbContext(options);

        var userEntity = context.Model.FindEntityType(typeof(ApplicationUser))!;
        var emailIndex = userEntity.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(ApplicationUser.NormalizedEmail)]));
        Assert.True(emailIndex.IsUnique);

        var jobEntity = context.Model.FindEntityType(typeof(Job))!;
        Assert.False(jobEntity.FindProperty(nameof(Job.OwnerId))!.IsNullable);
        var ownerRelationship = jobEntity.GetForeignKeys().Single(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.Equal(DeleteBehavior.Restrict, ownerRelationship.DeleteBehavior);
    }

    [Fact]
    public void OwnedJobQueryExcludesAnotherUsersJobs()
    {
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var jobs = new[]
        {
            new Job { RepoUrl = "https://github.com/example/owned", OwnerId = ownerId },
            new Job { RepoUrl = "https://github.com/example/other", OwnerId = otherUserId }
        }.AsQueryable();

        var visibleJobs = jobs.OwnedBy(ownerId).ToArray();

        var visibleJob = Assert.Single(visibleJobs);
        Assert.Equal(ownerId, visibleJob.OwnerId);
    }

    [Fact]
    public void JobControllerRequiresAuthentication()
    {
        Assert.NotNull(typeof(JobController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).SingleOrDefault());
    }
}
