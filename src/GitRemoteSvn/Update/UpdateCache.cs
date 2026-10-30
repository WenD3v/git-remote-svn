using System.Text.Json;

namespace GitRemoteSvn.Update;

/// <summary>Resultado guardado da ultima checagem; <c>LatestVersion</c> nulo significa "nenhuma novidade".</summary>
public sealed record UpdateCacheEntry(DateTimeOffset CheckedAtUtc, string? LatestVersion, string? AssetName);

/// <summary>
/// Cache da checagem no diretorio do usuario, para nao consultar o GitHub a cada sessao. Falha de escrita ou
/// arquivo corrompido nunca atrapalham a sessao: vale como "nunca checado".
/// </summary>
public sealed class UpdateCache(string path)
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Path { get; } = path;

    public static string DefaultPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(local, "git-remote-svn", "update.json");
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var baseDir = string.IsNullOrWhiteSpace(xdg)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                : xdg;
            return System.IO.Path.Combine(baseDir, "git-remote-svn", "update.json");
        }
    }

    public UpdateCacheEntry? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<UpdateCacheEntry>(File.ReadAllText(Path), JsonOpts);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsFresh(UpdateCacheEntry entry, TimeSpan ttl)
    {
        var idade = DateTimeOffset.UtcNow - entry.CheckedAtUtc;
        return idade >= TimeSpan.Zero && idade < ttl;
    }

    public void Write(UpdateCacheEntry entry)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry, JsonOpts));
            File.Move(tmp, Path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // cache e opcional: sem ele a proxima sessao so consulta o GitHub de novo
        }
    }
}
