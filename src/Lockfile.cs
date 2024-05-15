public class Lockfile
{
    public string Password { get; init; }
    public string Port { get; init; }

    private Lockfile(string content)
    {
        var parts = content.Split(':');
        Password = parts[3];
        Port = parts[2];
    }

    public static async Task<Lockfile> CreateAsync(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var content = await sr.ReadToEndAsync();

        return new Lockfile(content);
    }
}
