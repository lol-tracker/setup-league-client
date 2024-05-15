public static class Common
{
    public static async Task DownloadFileAsync(string url, string path)
    {
        using var client = new HttpClient();
        using var stream = await client.GetStreamAsync(url);
        using var fs = File.OpenWrite(path);
        await stream.CopyToAsync(fs);
    }

    public static async Task WaitForFileAsync(string path)
    {
        while (!File.Exists(path))
            await Task.Delay(TimeSpan.FromMilliseconds(500));
    }
}
