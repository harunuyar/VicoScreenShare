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
//
// "*" binds BOTH IPv4 and IPv6 (Kestrel walks every address family); the
// previous "0.0.0.0" was IPv4-only, which made every client connect to
// "localhost" eat a ~3s SYN timeout on ::1 before falling back to
// 127.0.0.1. Each Create/Join opens a fresh WebSocket, so that delay
// hit every room operation in the published exe.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://*:5000");
}

ServerHost.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
ServerHost.ConfigureEndpoints(app);
app.Run();

// Exposed so WebApplicationFactory<Program> can target this entry point in tests.
public partial class Program { }
