using ScreenSharing.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.SingleLine = true;
});

// Kestrel binds via standard configuration: ASPNETCORE_URLS env var,
// appsettings Kestrel:Endpoints, or the --urls CLI arg. Default when nothing is set
// is all interfaces on port 5000 so a VPS deploy works out of the box.
// launchSettings.json pins Development to http://localhost:5000.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5000");
}

ServerHost.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
ServerHost.ConfigureEndpoints(app);
app.Run();

// Exposed so WebApplicationFactory<Program> can target this entry point in tests.
public partial class Program { }
