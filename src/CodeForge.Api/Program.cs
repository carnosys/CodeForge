using CodeForge.Api.Auth;
using CodeForge.Infrastructure.Data;
using CodeForge.Infrastructure.Messaging;
using CodeForge.Infrastructure.Models;
using CodeForge.Infrastructure.Reports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<CodeForgeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<CodeForgeDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "CodeForge.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddDataProtection()
    .SetApplicationName("CodeForge")
    .PersistKeysToDbContext<CodeForgeDbContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddControllers();
builder.Services.AddSingleton(_ => new ServiceBusProducer(
    builder.Configuration["QUEUE_CONNECTION_STRING_SEND"]
        ?? throw new InvalidOperationException("QUEUE_CONNECTION_STRING_SEND is missing."),
    builder.Configuration["QUEUE_NAME"]
        ?? throw new InvalidOperationException("QUEUE_NAME is missing.")));
builder.Services.AddSingleton(_ => new BlobReportStore(
    new Uri(builder.Configuration["BLOB_SERVICE_URI"]
        ?? throw new InvalidOperationException("BLOB_SERVICE_URI is missing.")),
    builder.Configuration["BLOB_CONTAINER_NAME"] ?? "codeforge-reports"));

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<CodeForgeDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

await app.RunAsync();
