using GitRemoteSvn.Tests.Fixtures;
using GitRemoteSvn.Update;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GitRemoteSvn.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.2")]
    [InlineData("1.0.2", "1.0.2")]
    [InlineData("1.0.2+abc123", "1.0.2")]
    [InlineData("v2.1.0-beta.1", "2.1.0-beta.1")]
    public void Normalize_tira_prefixo_v_e_metadata(string tag, string esperado) =>
        Assert.Equal(esperado, VersionUtil.Normalize(tag));

    [Theory]
    [InlineData("1.0.2", "1.0.1", true)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0", "1.0.0", false)]
    public void IsNewer_compara_por_componente_numerico(string candidato, string atual, bool esperado) =>
        Assert.Equal(esperado, VersionUtil.IsNewer(candidato, atual));

    [Fact]
    public void IsPrerelease_reconhece_sufixo() =>
        Assert.True(VersionUtil.IsPrerelease("v2.1.0-beta.1"));

    [Fact]
    public void Current_tem_tres_componentes() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", VersionUtil.Current);

    [Fact]
    public void Cache_roundtrip_e_ttl()
    {
        var path = Path.Combine(Path.GetTempPath(), "grs-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cache = new UpdateCache(path);
            Assert.Null(cache.Read());
            var agora = DateTimeOffset.UtcNow;
            cache.Write(new UpdateCacheEntry(agora, "1.0.2", null));
            var lido = cache.Read();
            Assert.Equal("1.0.2", lido!.LatestVersion);
            Assert.True(cache.IsFresh(lido, TimeSpan.FromHours(24)));
            Assert.False(cache.IsFresh(lido, TimeSpan.Zero));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cache_invalido_e_lixo_devolve_null()
    {
        var path = Path.Combine(Path.GetTempPath(), "grs-cache-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ nao e json");
        try { Assert.Null(new UpdateCache(path).Read()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultPath_fica_sob_diretorio_do_usuario() =>
        Assert.Contains("git-remote-svn", UpdateCache.DefaultPath);

    [Fact]
    public void Options_le_ambiente_e_usa_padroes()
    {
        var padrao = UpdateOptions.FromEnvironment(_ => null);
        Assert.Equal("WenD3v/git-remote-svn", padrao.Repo);
        Assert.Equal("https://api.github.com", padrao.ApiBase);
        Assert.False(padrao.Quiet);

        var custom = UpdateOptions.FromEnvironment(k => k switch
        {
            "GIT_REMOTE_SVN_UPDATE_REPO" => "eu/outro",
            "GIT_REMOTE_SVN_UPDATE_API" => "https://espelho/api/",
            "GIT_REMOTE_SVN_QUIET" => "1",
            _ => null,
        });
        Assert.Equal("eu/outro", custom.Repo);
        Assert.Equal("https://espelho/api", custom.ApiBase);
        Assert.True(custom.Quiet);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData(null, false)]
    public void AutoUpdate_vem_do_ambiente(string? valor, bool esperado) =>
        Assert.Equal(esperado, UpdateOptions.AutoUpdateRequested(_ => valor));

    const string ReleaseJson = """
    {"tag_name":"v1.0.2","prerelease":false,"assets":[
      {"name":"git-remote-svn-v1.0.2-win-x64.zip","browser_download_url":"https://x/win.zip"},
      {"name":"SHA256SUMS","browser_download_url":"https://x/SHA256SUMS"}]}
    """;

    static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new FakeHandler(responder));

    [Fact]
    public async Task Check_le_tag_e_assets()
    {
        var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null),
            Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson) }),
            "git-remote-svn.exe");
        var rel = await svc.CheckAsync();
        Assert.Equal("1.0.2", rel!.Version);
        Assert.Equal(2, rel.Assets.Count);
        Assert.Equal("https://x/win.zip", rel.Assets[0].DownloadUrl);
    }

    [Fact]
    public async Task Check_devolve_null_em_rate_limit()
    {
        var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null),
            Http(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), "git-remote-svn.exe");
        Assert.Null(await svc.CheckAsync());
    }

    [Fact]
    public async Task Check_devolve_null_para_prerelease()
    {
        var json = ReleaseJson.Replace("\"prerelease\":false", "\"prerelease\":true");
        var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null),
            Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }),
            "git-remote-svn.exe");
        Assert.Null(await svc.CheckAsync());
    }

    [Fact]
    public async Task Check_engole_falha_de_rede()
    {
        var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null),
            Http(_ => throw new HttpRequestException("sem rede")), "git-remote-svn.exe");
        Assert.Null(await svc.CheckAsync());
    }

    [Theory]
    [InlineData("win", "x64", "git-remote-svn-v1.0.2-win-x64.zip")]
    [InlineData("linux", "x64", "git-remote-svn-v1.0.2-linux-x64.tar.gz")]
    public void SelectAsset_escolhe_pelo_sufixo(string os, string arch, string esperado)
    {
        var assets = new[]
        {
            new ReleaseAsset("git-remote-svn-v1.0.2-win-x64.zip", "a"),
            new ReleaseAsset("git-remote-svn-v1.0.2-linux-x64.tar.gz", "b"),
            new ReleaseAsset("SHA256SUMS", "c"),
        };
        Assert.Equal(esperado, UpdateService.SelectAsset(assets, os, arch)!.Name);
    }

    [Fact]
    public void SelectAsset_sem_asset_da_plataforma_devolve_null()
    {
        var assets = new[] { new ReleaseAsset("git-remote-svn-v1.0.2-win-x64.zip", "a") };
        Assert.Null(UpdateService.SelectAsset(assets, "linux", "arm64"));
    }

    [Fact]
    public void ParseSums_encontra_a_linha_do_asset()
    {
        var text = "abc123  outra.zip\nDEF456  git-remote-svn-v1.0.2-win-x64.zip\n";
        Assert.Equal("def456", UpdateService.ParseSums(text, "git-remote-svn-v1.0.2-win-x64.zip"));
        Assert.Null(UpdateService.ParseSums(text, "nao-existe.zip"));
    }

    static byte[] ZipCom(string nomeInterno, string texto)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = zip.CreateEntry(nomeInterno).Open();
            s.Write(Encoding.UTF8.GetBytes(texto));
        }
        return ms.ToArray();
    }

    static byte[] TarGzCom(string nomeInterno, string texto)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.NoCompression, true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, nomeInterno)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(texto)),
            });
        }
        return ms.ToArray();
    }

    static string Sha(byte[] conteudo) => Convert.ToHexString(SHA256.HashData(conteudo)).ToLowerInvariant();

    /// <summary>Dir temporario com um executavel "antigo" e um service apontando para ele.</summary>
    static (string Dir, string Exe, UpdateService Service) Cenario(Dictionary<string, byte[]> respostas,
        UpdateOptions? options = null)
    {
        var dir = Directory.CreateTempSubdirectory("grs-up-").FullName;
        var exe = Path.Combine(dir, "git-remote-svn.exe");
        File.WriteAllText(exe, "antigo");
        var svc = new UpdateService(options ?? UpdateOptions.FromEnvironment(_ => null),
            Http(req => respostas.TryGetValue(req.RequestUri!.ToString(), out var corpo)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corpo) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)),
            exe);
        return (dir, exe, svc);
    }

    [Fact]
    public async Task Update_recusa_sem_sha256sums()
    {
        var (dir, exe, svc) = Cenario(new());
        var rel = new ReleaseInfo("v9.9.9", "9.9.9",
            new[] { new ReleaseAsset("git-remote-svn-v9.9.9-win-x64.zip", "https://x/a.zip") });
        try
        {
            var r = await svc.UpdateAsync(rel);
            Assert.False(r.Updated);
            Assert.Contains("SHA256SUMS", r.Message);
            Assert.Equal("antigo", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Update_recusa_hash_divergente()
    {
        var zip = ZipCom("git-remote-svn.exe", "novo");
        var (dir, exe, svc) = Cenario(new()
        {
            ["https://x/a.zip"] = zip,
            ["https://x/SHA256SUMS"] = Encoding.UTF8.GetBytes(new string('0', 64) + "  git-remote-svn-v9.9.9-win-x64.zip\n"),
        });
        var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[]
        {
            new ReleaseAsset("git-remote-svn-v9.9.9-win-x64.zip", "https://x/a.zip"),
            new ReleaseAsset("SHA256SUMS", "https://x/SHA256SUMS"),
        });
        try
        {
            var r = await svc.UpdateAsync(rel);
            Assert.False(r.Updated);
            Assert.Contains("hash", r.Message);
            Assert.Equal("antigo", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Update_troca_o_executavel_quando_hash_confere()
    {
        var zip = ZipCom("git-remote-svn.exe", "novo");
        var (dir, exe, svc) = Cenario(new()
        {
            ["https://x/a.zip"] = zip,
            ["https://x/SHA256SUMS"] = Encoding.UTF8.GetBytes(Sha(zip) + "  git-remote-svn-v9.9.9-win-x64.zip\n"),
        });
        var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[]
        {
            new ReleaseAsset("git-remote-svn-v9.9.9-win-x64.zip", "https://x/a.zip"),
            new ReleaseAsset("SHA256SUMS", "https://x/SHA256SUMS"),
        });
        try
        {
            var r = await svc.UpdateAsync(rel);
            Assert.True(r.Updated, r.Message);
            Assert.Equal("9.9.9", r.Version);
            Assert.Equal("novo", File.ReadAllText(exe));
            Assert.Equal("antigo", File.ReadAllText(exe + ".old"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Update_extrai_tar_gz()
    {
        var tar = TarGzCom("pasta/git-remote-svn.exe", "novo-tar");
        var (dir, exe, svc) = Cenario(new()
        {
            ["https://x/a.tar.gz"] = tar,
            ["https://x/SHA256SUMS"] = Encoding.UTF8.GetBytes(Sha(tar) + "  git-remote-svn-v9.9.9-win-x64.tar.gz\n"),
        });
        var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[]
        {
            new ReleaseAsset("git-remote-svn-v9.9.9-win-x64.tar.gz", "https://x/a.tar.gz"),
            new ReleaseAsset("SHA256SUMS", "https://x/SHA256SUMS"),
        });
        try
        {
            var r = await svc.UpdateAsync(rel);
            Assert.True(r.Updated, r.Message);
            Assert.Equal("novo-tar", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Update_sem_asset_da_plataforma_nao_baixa_nada()
    {
        var (dir, exe, svc) = Cenario(new()
        {
            ["https://x/SHA256SUMS"] = Encoding.UTF8.GetBytes("x  outra-coisa.zip\n"),
        });
        var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[]
        {
            new ReleaseAsset("git-remote-svn-v9.9.9-plataforma-inexistente.zip", "https://x/nada.zip"),
            new ReleaseAsset("SHA256SUMS", "https://x/SHA256SUMS"),
        });
        try
        {
            var r = await svc.UpdateAsync(rel);
            Assert.False(r.Updated);
            Assert.Contains("plataforma", r.Message);
            Assert.Equal("antigo", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CleanupLeftovers_apaga_old_da_atualizacao_anterior()
    {
        var dir = Directory.CreateTempSubdirectory("grs-old-").FullName;
        var exe = Path.Combine(dir, "git-remote-svn.exe");
        File.WriteAllText(exe, "novo");
        File.WriteAllText(exe + ".old", "antigo");
        try
        {
            UpdateService.CleanupLeftovers(exe);
            Assert.False(File.Exists(exe + ".old"));
            Assert.Equal("novo", File.ReadAllText(exe));
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Task 6: comandos e checagem ao final da sessao ---

    [Fact]
    public async Task Update_em_instalacao_de_desenvolvimento_recusa()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var svc = new UpdateService(UpdateOptions.FromEnvironment(_ => null), http,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var rel = new ReleaseInfo("v9.9.9", "9.9.9", new[] { new ReleaseAsset("SHA256SUMS", "https://x/s") });
        var r = await svc.UpdateAsync(rel);
        Assert.False(r.Updated);
        Assert.Contains("desenvolvimento", r.Message);
    }

    static string AssetDaPlataforma(string versao) =>
        $"git-remote-svn-v{versao}-{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-" +
        $"{(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : "arm64")}" +
        (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");

    static byte[] PacoteDaPlataforma(string texto) =>
        OperatingSystem.IsWindows() ? ZipCom("git-remote-svn.exe", texto) : TarGzCom("git-remote-svn", texto);

    /// <summary>Servidor falso: `releases/latest` devolve <paramref name="rel"/> e as URLs extras vem de <paramref name="extras"/>.</summary>
    static HttpClient HttpDoRelease(ReleaseInfo rel, Dictionary<string, byte[]>? extras = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = rel.Tag,
            prerelease = false,
            assets = rel.Assets.Select(a => new { name = a.Name, browser_download_url = a.DownloadUrl }),
        });
        extras ??= new();
        return new HttpClient(new FakeHandler(r =>
        {
            var url = r.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            if (extras.TryGetValue(url, out var bytes))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
    }

    static (string Dir, string Exe, UpdateCache Cache) CenarioNotice()
    {
        var dir = Directory.CreateTempSubdirectory("grs-notice-").FullName;
        return (dir, Path.Combine(dir, OperatingSystem.IsWindows() ? "git-remote-svn.exe" : "git-remote-svn"),
            new UpdateCache(Path.Combine(dir, "update.json")));
    }

    [Fact]
    public async Task Notice_avisa_quando_ha_versao_nova()
    {
        var (dir, exe, cache) = CenarioNotice();
        var rel = new ReleaseInfo("v99.0.0", "99.0.0",
            new[] { new ReleaseAsset(AssetDaPlataforma("99.0.0"), "https://x/pacote") });
        try
        {
            var msg = await UpdateNotice.CheckAndApplyAsync(UpdateOptions.FromEnvironment(_ => null),
                HttpDoRelease(rel), exe, cache, () => Task.FromResult(false));
            Assert.Contains("nova versao v99.0.0", msg);
            Assert.Equal("99.0.0", cache.Read()!.LatestVersion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Notice_nao_consulta_quando_o_cache_esta_fresco()
    {
        var (dir, exe, cache) = CenarioNotice();
        cache.Write(new UpdateCacheEntry(DateTimeOffset.UtcNow, null, null));
        var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("nao devia consultar")));
        try
        {
            var msg = await UpdateNotice.CheckAndApplyAsync(UpdateOptions.FromEnvironment(_ => null),
                http, exe, cache, () => Task.FromResult(false));
            Assert.Null(msg);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Notice_grava_negativo_quando_esta_na_ultima_versao()
    {
        var (dir, exe, cache) = CenarioNotice();
        var rel = new ReleaseInfo("v" + VersionUtil.Current, VersionUtil.Current, Array.Empty<ReleaseAsset>());
        try
        {
            var msg = await UpdateNotice.CheckAndApplyAsync(UpdateOptions.FromEnvironment(_ => null),
                HttpDoRelease(rel), exe, cache, () => Task.FromResult(false));
            Assert.Null(msg);
            var entry = cache.Read()!;
            Assert.Null(entry.LatestVersion);
            Assert.True(cache.IsFresh(entry, TimeSpan.FromHours(24)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Notice_auto_atualiza_quando_ligado()
    {
        var (dir, exe, cache) = CenarioNotice();
        var versao = "99.1.0";
        var nome = AssetDaPlataforma(versao);
        var pacote = PacoteDaPlataforma("novo");
        var sums = Encoding.UTF8.GetBytes($"{Sha(pacote)}  {nome}\n");
        var rel = new ReleaseInfo("v" + versao, versao, new[]
        {
            new ReleaseAsset(nome, "https://x/pacote"),
            new ReleaseAsset("SHA256SUMS", "https://x/sums"),
        });
        File.WriteAllText(exe, "antigo");
        try
        {
            var msg = await UpdateNotice.CheckAndApplyAsync(UpdateOptions.FromEnvironment(_ => null),
                HttpDoRelease(rel, new() { ["https://x/pacote"] = pacote, ["https://x/sums"] = sums }),
                exe, cache, () => Task.FromResult(true));
            Assert.Contains($"atualizado para v{versao}", msg);
            Assert.Equal("novo", File.ReadAllText(exe));
            Assert.Equal(versao, cache.Read()!.LatestVersion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Version_imprime_versao_e_rid()
    {
        var (code, stdout, _) = await RunHelperAsync(null, "--version");
        Assert.Equal(0, code);
        Assert.StartsWith("git-remote-svn " + VersionUtil.Current, stdout);
        Assert.Contains("(", stdout);
    }

    [Fact]
    public async Task Update_check_sem_rede_nao_falha()
    {
        var (code, stdout, stderr) = await RunHelperAsync(
            new() { ["GIT_REMOTE_SVN_UPDATE_API"] = "http://127.0.0.1:9" }, "update", "--check");
        Assert.Equal(0, code);
        Assert.Contains("nao foi possivel", stderr + stdout);
    }

    [Fact]
    public async Task Update_usa_cache_e_nao_consulta_o_github_quando_fresco()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "grs-cli-cache-" + Guid.NewGuid().ToString("N") + ".json");
        new UpdateCache(cachePath).Write(new UpdateCacheEntry(DateTimeOffset.UtcNow, "99.2.0", null));
        try
        {
            var (code, stdout, stderr) = await RunHelperAsync(new()
            {
                ["GIT_REMOTE_SVN_UPDATE_API"] = "http://127.0.0.1:9",
                ["GIT_REMOTE_SVN_UPDATE_CACHE"] = cachePath,
            }, "update", "--check");
            Assert.Equal(0, code);
            Assert.Contains("99.2.0", stdout + stderr);
        }
        finally { File.Delete(cachePath); }
    }

    /// <summary>Roda o executavel publicado do helper (fora do git) com variaveis de ambiente extras.</summary>
    static async Task<(int Code, string StdOut, string StdErr)> RunHelperAsync(
        Dictionary<string, string?>? env, params string[] args)
    {
        var dir = await HelperBinary.GetDirAsync();
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "git-remote-svn.exe" : "git-remote-svn");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }
}

/// <summary>Respostas HTTP de teste, sem abrir porta nem depender de rede.</summary>
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(responder(request));
}
