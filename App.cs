using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Actions.Core.Extensions;
using Actions.Core.Services;
using System.Diagnostics;

// Create services
using var services = new ServiceCollection()
    .AddGitHubActionsCore()
    .BuildServiceProvider();

var core = services.GetRequiredService<ICoreService>();
var is_debug = core.GetBoolInput("is-debug");

// Obtain logger
using var loggerFactory = LoggerFactory.Create(builder => builder
        .SetMinimumLevel(is_debug ? LogLevel.Debug : LogLevel.Information)
        .AddConsole());

var logger = loggerFactory.CreateLogger("App");
logger.LogInformation("Initializing...");

// Get inputs
var username = core.GetInput("username", new(Required: true));
var password = core.GetInput("password", new(Required: true));
var region = core.GetInput("region", new(Required: true));
var patchline = core.GetInput("patchline");
var config = core.GetInput("config");
var full_install = core.GetBoolInput("full-install");
var install_pengu = core.GetBoolInput("install-pengu");

// if config is not set than it is the same as region
if (String.IsNullOrWhiteSpace(config))
    config = region;

// Set default patchline
if (String.IsNullOrWhiteSpace(patchline))
    patchline = "live";

// Print input values if debug
logger.LogDebug($"Region: {region}");
logger.LogDebug($"Patchline: {patchline}");
logger.LogDebug($"Config: {config}");
logger.LogDebug($"Full install: {full_install}");
logger.LogDebug($"Install pengu: {install_pengu}");

const string LOL_PRODUCT_ID = "league_of_legends";

var temp = Environment.GetEnvironmentVariable("RUNNER_TEMP")!;
var localAppdata = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

var installerPath = Path.Join(temp, $"install.{region}.exe");
var installID = $"{LOL_PRODUCT_ID}.{patchline.ToLower()}";

logger.LogInformation("Downloading installer...");
{
    var url = $"https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/{patchline.ToLower()}.{config.ToLower()}.exe";
    using var client = new HttpClient();
    using var stream = await client.GetStreamAsync(url);
    using var fs = File.OpenWrite(installerPath);
    await stream.CopyToAsync(fs);
}

logger.LogInformation("Installing...");
{
    var process = Process.Start(installerPath, ["--skip-to-install"]);
    await process.WaitForExitAsync();
}

logger.LogInformation("Done!");
