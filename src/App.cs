using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Actions.Core.Extensions;
using Actions.Core.Services;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

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
await Common.WaitForFileAsync(rciPath, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);

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
var rcsLockfile = Path.Join(localAppdata, "Riot Games", "Riot Client", "Config", "lockfile");

logger.LogDebug($"Riot Client path: {rcsPath}");
logger.LogDebug($"Riot Client directory: {rcsDir}");
logger.LogDebug($"Riot Client lockfile: {rcsLockfile}");

logger.LogInformation("Closing Riot Client...");
{
    var tasks = Process.GetProcessesByName("RiotClientServices").Select(process =>
    {
        process.Kill();
        return process.WaitForExitAsync();
    });
    await Task.WhenAll(tasks);
}

logger.LogInformation("Copying settings and cookies...");
{
    async Task DecodeAndWrite(string path, string data)
    {
        var decoded = Convert.FromBase64String(data);
        await File.WriteAllBytesAsync(path, decoded);
    }

    var basePath = Path.Join(localAppdata, "Riot Games", "Riot Client");
    var tasks = new List<Task>() {
        DecodeAndWrite(Path.Join(basePath, "Config", "RiotClientSettings.yaml"), core.GetInput("FILE_CLIENT_SETTINGS_CONTENT")),
        DecodeAndWrite(Path.Join(basePath, "Data", "RiotGamesPrivateSettings.yaml"), core.GetInput("FILE_PRIVATE_SETTINGS_CONTENT")),
        DecodeAndWrite(Path.Join(basePath, "Config", "Cookies", "Cookie"), core.GetInput("FILE_COOKIES_CONTENT")),
    };

    await Task.WhenAll(tasks);
}

logger.LogInformation("Downloading and running LeagueNoVGK...");

var leagueNoVgkPath = Path.Join(temp, "league-no-vgk.exe");
{
    var url = "https://github.com/User344/LeagueNoVGK/releases/download/1.0.1/league-no-vgk.exe";
    await Common.DownloadFileAsync(url, leagueNoVgkPath);

    // Delete lockfile so we can wait when its created again.
    File.Delete(rcsLockfile);

    // Start process normally
    new Process()
    {
        StartInfo = new ProcessStartInfo()
        {
            FileName = leagueNoVgkPath,
            Arguments = $"--launch-product={LOL_PRODUCT_ID} --launch-patchline={patchline} --region={region}",
            UseShellExecute = true,
            CreateNoWindow = true,
        }
    }.Start();

    // Wait untill lockfile is created.
    await Common.WaitForFileAsync(rcsLockfile, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);

    // Wait 1 second to make sure the lockfile is written.
    await Task.Delay(TimeSpan.FromSeconds(1));
}

var rcsAPI = await API.CreateAsync(rcsLockfile);

logger.LogInformation("Installing League Client...");
{
    await Task.Delay(TimeSpan.FromSeconds(5));

    var url = $"/patch-proxy/v1/priority-patch-jobs/products/{LOL_PRODUCT_ID}/patchlines/{patchline}";
    await rcsAPI.PutAsync(url, new
    {
        createShortcut = false,
        installPath = "C:\\Riot Games",
    });

    while (true)
    {
        var status = await rcsAPI.GetJSONAsync($"/patch/v1/installs/{installID}/status");
        if (status is null)
        {
            logger.LogDebug("Number of RCS open: " + Process.GetProcessesByName("RiotClientServices").Length);
            throw new Exception("Failed to get RCS status!");
        }

        if (status["patch"]!["state"]!.GetValue<string>() == "up_to_date")
            break;

        var progress = status["patch"]!["progress"]!["progress"]!.GetValue<float>();
        logger.LogInformation($"Installing League Client... {progress}%", progress);

        await Task.Delay(TimeSpan.FromSeconds(15));
    }

    logger.LogInformation("Successfully installed!");
}

logger.LogInformation("Locating Riot Client...");

async Task<string> GetLeagueClientPath()
{
    var install = await rcsAPI.GetJSONAsync($"/patch/v1/installs/{installID}");
    return install!["path"]!.GetValue<string>();
}
var lcuPath = await GetLeagueClientPath();
var lcuExe = Path.Combine(lcuPath, "LeagueClient.exe");
var lcuLockfile = Path.Combine(lcuPath, "lockfile");

logger.LogDebug($"League Client path: {lcuPath}");
logger.LogDebug($"League Client exe: {lcuExe}");

var penguPath = Path.Combine(temp, "pengu-loader.zip");
var penguDir = Path.Combine(temp, "pengu-loader");
var penguExe = Path.Combine(penguDir, "Pengu Loader.exe");
if (install_pengu)
{
    logger.LogInformation("Downloading and activating Pengu Loader...");

    var url = "https://github.com/PenguLoader/PenguLoader/releases/download/v1.1.0/pengu-loader-v1.1.0.zip";
    await Common.DownloadFileAsync(url, penguPath);

    await Task.Run(() => ZipFile.ExtractToDirectory(penguPath, penguDir));

    var startInfo = new ProcessStartInfo()
    {
        WorkingDirectory = penguDir,
        FileName = penguExe,
        Arguments = "--install"
    };

    await Process.Start(startInfo)!.WaitForExitAsync();
}

logger.LogInformation("Downloading and running lcu-patcher...");
{
    var patcherUrl = "https://github.com/lol-tracker/lcu-patcher/releases/download/v1.1.0/lcu-patcher-win64.zip";
    var patcherFilePath = Path.Join(temp, "lcu-patcher.zip");
    var patcherPath = Path.Join(temp, "lcu-patcher");
    var patcherExe = Path.Join(patcherPath, "lcu-patcher.exe");

    if (!File.Exists(patcherExe))
    {
        await Common.DownloadFileAsync(patcherUrl, patcherFilePath);

        await Task.Run(() => ZipFile.ExtractToDirectory(patcherFilePath, patcherPath));

        await Process.Start(patcherExe, [lcuExe]).WaitForExitAsync();
    }
}

/* logger.LogInformation("Logging in..."); */
/* { */
/*     await rcsAPI.PostAsync("/rso-auth/v1/authorization/gas", new */
/*     { */
/*         username = username, */
/*         password = password, */
/*     }); */
/*     await Task.Delay(TimeSpan.FromSeconds(5)); */
/* } */

logger.LogInformation("Accepting EULA...");
{
    await rcsAPI.PutAsync("/eula/v1/agreement/acceptance");
    await Task.Delay(TimeSpan.FromSeconds(5));
}

/* logger.LogDebug("Deleting old LCU lockfile..."); */
/* File.Delete(lcuLockfile); */
/**/
/* logger.LogInformation("Starting LCU..."); */
/* { */
/*     await rcsAPI.PostAsync($"/product-launcher/v1/products/league_of_legends/patchlines/{patchline}"); */
/*     await Task.Delay(TimeSpan.FromSeconds(5)); */
/* } */

await Common.WaitForFileAsync(lcuLockfile, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);

var lcuAPI = await API.CreateAsync(lcuLockfile);
await Task.Delay(TimeSpan.FromSeconds(10));

logger.LogDebug("Burning first request...");
await lcuAPI.GetAsync("/lol-patch/v1/products/league_of_legends/state");
await Task.Delay(TimeSpan.FromSeconds(10));

if (full_install)
{
    logger.LogInformation("Installing League of Legends...");

    while (true)
    {
        var state = await lcuAPI.GetJSONAsync("/lol-patch/v1/products/league_of_legends/state");
        if (state is null)
        {
            throw new Exception("Failed to get LCU state!");
        }

        if (state["action"]!.GetValue<string>() == "Idle")
            break;

        var bytesPerSecond = state["components"]![0]!["progress"]!["network"]!["bytesPerSecond"]!.GetValue<float>();
        var bytesRequired = state["components"]![0]!["progress"]!["total"]!["bytesRequired"]!.GetValue<ulong>();
        var bytesComplete = state["components"]![0]!["progress"]!["total"]!["bytesComplete"]!.GetValue<ulong>();

        var mbps = bytesPerSecond / 1000000;
        var left = bytesRequired - bytesComplete;

        var progress = bytesRequired > 0 ? (int)(((float)bytesComplete / (float)bytesRequired) * 100) : 0;
        logger.LogInformation($"LCU updating: {progress}% ({left} bytes left - {mbps} mbps)");

        await Task.Delay(TimeSpan.FromSeconds(15));
    }
}

logger.LogInformation("Setting output...");
{
    var lockfile = await Lockfile.CreateAsync(rcsLockfile);
    await core.SetOutputAsync("rcs-password", lockfile.Password);
    await core.SetOutputAsync("rcs-port", lockfile.Port);
    await core.SetOutputAsync("rcs-directory", rcsPath);

    lockfile = await Lockfile.CreateAsync(lcuLockfile);
    await core.SetOutputAsync("lcu-password", lockfile.Password);
    await core.SetOutputAsync("lcu-port", lockfile.Port);
    await core.SetOutputAsync("lcu-directory", lcuPath);

    await core.SetOutputAsync("pengu-directory", penguDir);
}

logger.LogInformation("Done!");
