using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<CodeForgeDbContext>(options=>options.UseNpgsql(connectionString));
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapGet("/",()=>"Online");
app.MapControllers();

app.Run();

