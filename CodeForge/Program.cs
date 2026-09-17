using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<CodeForgeDbContext>(options=>options.UseNpgsql(connectionString));

var app = builder.Build();

app.MapGet("/",()=>"Online");

app.Run();

