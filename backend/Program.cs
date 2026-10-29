using Microsoft.EntityFrameworkCore;
using Telecom.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddProblemDetails();
builder.Services.AddDbContext<NetworkDb>(o => {
    if (builder.Configuration["Database:Provider"] == "Sqlite")
        o.UseSqlite(builder.Configuration.GetConnectionString("Network") ?? "Data Source=telecom.db");
    else o.UseMySql(builder.Configuration.GetConnectionString("Network") ?? throw new InvalidOperationException("ConnectionStrings:Network is required"), new MariaDbServerVersion(new Version(11,4,0)));
});
builder.Services.AddOpenApi();
var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapGet("/api/health", async (NetworkDb db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status="ok" }) : Results.StatusCode(503));
app.MapNetworkApi();
using (var scope = app.Services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<NetworkDb>();
    await db.Database.EnsureCreatedAsync();
    if (app.Configuration.GetValue("SeedDemo", true)) await Demo.Seed(db);
}
app.Run();
public partial class Program { }
