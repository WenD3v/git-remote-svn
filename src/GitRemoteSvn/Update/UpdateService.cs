using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
public sealed class UpdateService(UpdateOptions options, HttpClient http, string exePath)
{
    /// <summary>Nome do asset com os hashes; sem ele nada e instalado.</summary>
    public const string SumsAssetName = "SHA256SUMS";

    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

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

    /// <summary>Baixa, confere o SHA-256 e troca o executavel. Nunca lanca: o motivo vai em <see cref="UpdateResult.Message"/>.</summary>
    public async Task<UpdateResult> UpdateAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        try
        {
            if (IsFrameworkLauncher())
                return new UpdateResult(false, null,
                    "este helper esta rodando pelo 'dotnet'; instale o binario publicado para usar o update");

            var (os, arch) = Platform();
            var asset = SelectAsset(release.Assets, os, arch);
            if (asset is null)
                return new UpdateResult(false, null,
                    $"sem atualizacao para esta plataforma ({os}-{arch})");

            var sums = release.Assets.FirstOrDefault(a => a.Name == SumsAssetName);
            if (sums is null)
                return new UpdateResult(false, null,
                    "release sem SHA256SUMS; atualizacao recusada (instale manualmente)");

            var package = await DownloadAsync(asset.DownloadUrl, ct);
            var expected = ParseSums(Encoding.UTF8.GetString(await DownloadAsync(sums.DownloadUrl, ct)), asset.Name);
            if (expected is null)
                return new UpdateResult(false, null, $"SHA256SUMS nao contem {asset.Name}; atualizacao recusada");

            var actual = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            if (actual != expected)
                return new UpdateResult(false, null, $"hash de {asset.Name} nao confere; atualizacao recusada");

            Install(ExtractExecutable(package, asset.Name));
            return new UpdateResult(true, release.Version, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateResult(false, null, ex.Message);
        }
    }

    /// <summary>Apaga <c>.old</c>/<c>.new</c> deixados por uma atualizacao anterior (chamado no inicio da execucao).</summary>
    public static void CleanupLeftovers(string exePath)
    {
        foreach (var suffix in new[] { ".old", ".new" })
        {
            try
            {
                if (File.Exists(exePath + suffix)) File.Delete(exePath + suffix);
            }
            catch (Exception)
            {
                // arquivo em uso: sai na proxima execucao
            }
        }
    }

    /// <summary>OS e arquitetura no vocabulario dos assets (<c>win-x64</c>, <c>linux-arm64</c>).</summary>
    static (string Os, string Arch) Platform()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return (os, arch);
    }

    static bool IsFrameworkLauncher()
    {
        var file = Environment.ProcessPath is { } p ? Path.GetFileName(p) : null;
        return file is not null &&
            (file.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
             file.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase));
    }

    async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"git-remote-svn/{VersionUtil.Current}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>Bytes do executavel dentro do zip/tar.gz, procurando pelo nome instalado ou pelos nomes padrao.</summary>
    byte[] ExtractExecutable(byte[] package, string assetName)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(exePath), "git-remote-svn", "git-remote-svn.exe",
        };
        using var stream = new MemoryStream(package);
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => accepted.Contains(Path.GetFileName(e.FullName)));
            if (entry is null) throw new InvalidOperationException($"o pacote {assetName} nao contem o executavel");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return buffer.ToArray();
        }

        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (!accepted.Contains(Path.GetFileName(entry.Name))) continue;
            using var buffer = new MemoryStream();
            entry.DataStream!.CopyTo(buffer);
            return buffer.ToArray();
        }
        throw new InvalidOperationException($"o pacote {assetName} nao contem o executavel");
    }

    /// <summary>Troca o arquivo em uso: renomeia o antigo e posiciona o novo no lugar.</summary>
    void Install(byte[] content)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var incoming = exePath + ".new";
        File.WriteAllBytes(incoming, content);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(incoming, exePath, true);
                return;
            }

            var previous = exePath + ".old";
            try
            {
                if (File.Exists(previous)) File.Delete(previous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // sem espaco para um backup novo: segue e tenta a troca
            }
            try
            {
                File.Move(exePath, previous);
            }
            catch (FileNotFoundException)
            {
                // instalacao nova: nao ha o que preservar
            }
            try
            {
                File.Move(incoming, exePath, true);
            }
            catch
            {
                if (!File.Exists(exePath) && File.Exists(previous)) File.Move(previous, exePath);
                throw;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(incoming)) File.Delete(incoming);
            }
            catch (Exception)
            {
                // limpeza melhor esforco
            }
        }
    }
}
