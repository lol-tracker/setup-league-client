using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public class API
{
    private HttpClient _Client;

    private API(string username, string password, int port)
    {
        var handler = new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _Client = new HttpClient(handler);
        _Client.BaseAddress = new Uri($"https://127.0.0.1:{port}");

        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));
        _Client.DefaultRequestHeaders.Authorization = auth;
    }

    public static async Task<API> CreateAsync(string lockfilePath)
    {
        using var fs = File.Open(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var content = await sr.ReadToEndAsync();
        var parts = content.Split(':');

        return new API("riot", parts[3], int.Parse(parts[2]));
    }

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        return _Client.GetStringAsync(url, cancellationToken);
    }

    public async Task<JsonNode?> GetJSONAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _Client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = response.Content.ReadAsStringAsync();
            throw new Exception($"Unsuccessfull response code: ${response.StatusCode}. Content: ${content}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<JsonNode>(stream, JsonSerializerOptions.Default, cancellationToken);
    }
}
