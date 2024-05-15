public static class Common
{
    public static async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        using var stream = await client.GetStreamAsync(url, cancellationToken);
        using var fs = File.OpenWrite(path);
        await stream.CopyToAsync(fs, cancellationToken);
    }

    public static async Task WaitForFileAsync(string path, CancellationToken cancellationToken = default)
    {
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }
}
