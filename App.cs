using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Actions.Core.Extensions;
using Actions.Core.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// Create services
using var services = new ServiceCollection()
    .AddGitHubActionsCore()
    .BuildServiceProvider();

var core = services.GetRequiredService<ICoreService>();
var is_debug = core.GetBoolInput("is-debug");

// Obtain logger
using var loggerFactory = LoggerFactory.Create(builder => builder
        .SetMinimumLevel(is_debug ? LogLevel.Debug : LogLevel.Information)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));

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
var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

logger.LogInformation("Downloading installer...");

var installerPath = Path.Join(temp, $"install.{region}.exe");
var installID = $"{LOL_PRODUCT_ID}.{patchline.ToLower()}";
{
    var url = $"https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/{patchline.ToLower()}.{config.ToLower()}.exe";
    await Common.DownloadFileAsync(url, installerPath);
}

logger.LogInformation("Installing Riot Client...");
{
    var process = Process.Start(installerPath, ["--skip-to-install"]);
    await process.WaitForExitAsync();
}

logger.LogDebug("Done. Waiting for RiotClientInstalls.json...");

// Wait for RiotClientInstalls.json to be created.
var rciPath = Path.Join(programData, "Riot Games", "RiotClientInstalls.json");
await Common.WaitForFileAsync(rciPath);

logger.LogInformation("Closing Riot Client...");
{
    var tasks = Process.GetProcessesByName("RiotClientServices").Select(process =>
    {
        process.Kill();
        return process.WaitForExitAsync();
    });
    await Task.WhenAll(tasks);
}

logger.LogInformation("Locating Riot Client...");

async Task<string> GetRiotClientPath()
{
    // NOTE: We can also probably enumerate currently running processes and get the path from there?

    using var fs = File.OpenRead(rciPath);

    var installs = await JsonSerializer.DeserializeAsync<JsonNode>(fs);
    return installs!["rc_default"]!.GetValue<string>(); // TODO: Make sure it works for PBE
}
var rcsPath = await GetRiotClientPath();
var rcsDir = Path.GetDirectoryName(rcsPath)!;
var rcsLockfile = Path.Join(localAppdata, "Riot Games", "Riot Client", "lockfile");

logger.LogDebug($"Riot Client path: {rcsPath}");
logger.LogDebug($"Riot Client directory: {rcsDir}");
logger.LogDebug($"Riot Client lockfile: {rcsLockfile}");

logger.LogInformation("Downloading and running LeagueNoVGK...");

var leagueNoVgkPath = Path.Join(temp, "league-no-vgk.exe");
{
    var url = "https://github.com/User344/LeagueNoVGK/releases/download/Latest/league-no-vgk.exe";
    await Common.DownloadFileAsync(url, leagueNoVgkPath);

    // Delete lockfile so we can wait when its created again.
    File.Delete(rcsLockfile);

    // Start process normally
    new Process()
    {
        StartInfo = new ProcessStartInfo()
        {
            FileName = leagueNoVgkPath,
            UseShellExecute = true,
            CreateNoWindow = true,
        }
    }.Start();

    // Wait untill lockfile is created.
    await Common.WaitForFileAsync(rcsLockfile);
}

var rcsAPI = await API.CreateAsync(rcsLockfile);

logger.LogInformation("Installing League Client...");
{
    while (true)
    {
        var status = await rcsAPI.GetJSONAsync($"/patch/v1/installs/{installID}/status");
        if (status!["patch"]!["state"]!.GetValue<string>() == "up_to_date")
            break;

        var progress = status!["patch"]!["progress"]!["progress"]!.GetValue<int>();
        logger.LogInformation($"Installing League Client... {progress}%", progress);

        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}

// ==================================================

logger.LogDebug("Installed!");

async Task<string> GetLeagueClientPath()
{
    var install = await rcsAPI.GetJSONAsync($"/patch/v1/installs/{installID}");
    return install!["path"]!.GetValue<string>();
}
var lcsPath = await GetLeagueClientPath();

logger.LogDebug($"League Client path: {lcsPath}");

logger.LogInformation("Done!");
