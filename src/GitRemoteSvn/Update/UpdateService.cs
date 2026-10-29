using System.Text.Json;

namespace GitRemoteSvn.Update;

/// <summary>Asset publicado no release do GitHub.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl);

/// <summary>Release mais recente; <see cref="Version"/> ja normalizada (sem <c>v</c>).</summary>
public sealed record ReleaseInfo(string Tag, string Version, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Resultado de uma tentativa de atualizacao: mensagem preenchida quando nada foi instalado.</summary>
public sealed record UpdateResult(bool Updated, string? Version, string? Message);

/// <summary>
/// Consulta o release do GitHub e instala o binario da plataforma. Nenhuma falha de rede pode escapar:
/// a checagem roda no fim de um fetch/push e so pode, no maximo, deixar de avisar.
/// </summary>
public sealed class UpdateService(UpdateOptions options, HttpClient http, string exePath, TextWriter? stderr = null)
{
    /// <summary>Nome do asset com os hashes; sem ele nada e instalado.</summary>
    public const string SumsAssetName = "SHA256SUMS";

    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Release mais recente, ou <c>null</c> em rate limit, pre-release, sem release ou falha de rede.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{options.ApiBase}/repos/{options.Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"git-remote-svn/{VersionUtil.Current}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = doc.RootElement;
            // pre-release nunca e oferecida: pode nem ter binario estavel
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { Length: > 0 } tag) return null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = item.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name is not null && url is not null) assets.Add(new ReleaseAsset(name, url));
                }
            }
            return new ReleaseInfo(tag, VersionUtil.Normalize(tag), assets);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Asset cujo nome termina em <c>-{os}-{arch}.zip</c> ou <c>.tar.gz</c>.</summary>
    public static ReleaseAsset? SelectAsset(IEnumerable<ReleaseAsset> assets, string os, string arch)
    {
        var suffixes = new[] { $"-{os}-{arch}.zip", $"-{os}-{arch}.tar.gz" };
        return assets.FirstOrDefault(a => a.Name != SumsAssetName &&
            suffixes.Any(s => a.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Hash (minusculo) do asset no texto do <c>SHA256SUMS</c>, ou <c>null</c> se ausente.</summary>
    public static string? ParseSums(string text, string assetName)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1] == assetName) return parts[0].ToLowerInvariant();
        }
        return null;
    }
}
