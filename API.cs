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
        var lockfile = await Lockfile.CreateAsync(lockfilePath);

        return new API("riot", lockfile.Password, int.Parse(lockfile.Port));
    }

    private async Task HandleResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var content = await response.Content.ReadAsStringAsync();
        throw new Exception($"Unsuccessfull response code: {response.StatusCode}. Content: {content}");
    }

    private StringContent? CreateJsonContent(object? body)
    {
        if (body is null)
            return null;

        var bodySerialized = JsonSerializer.Serialize(body);
        var mediaType = MediaTypeHeaderValue.Parse("application/json");
        return new StringContent(bodySerialized, mediaType);
    }

    public async Task GetAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _Client.GetAsync(url, cancellationToken);
        await HandleResponse(response);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _Client.GetAsync(url, cancellationToken);
        await HandleResponse(response);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<JsonNode?> GetJSONAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _Client.GetAsync(url, cancellationToken);
        await HandleResponse(response);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<JsonNode>(stream, JsonSerializerOptions.Default, cancellationToken);
    }

    public async Task PostAsync(string url, object? body = null, CancellationToken cancellationToken = default)
    {
        var content = CreateJsonContent(body);
        var response = await _Client.PostAsync(url, content, cancellationToken);
        await HandleResponse(response);
    }

    public async Task PutAsync(string url, object? body = null, CancellationToken cancellationToken = default)
    {
        var content = CreateJsonContent(body);
        var response = await _Client.PutAsync(url, content, cancellationToken);
        await HandleResponse(response);
    }
}
