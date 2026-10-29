using GitRemoteSvn.Update;
using System.Net;
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
}

/// <summary>Respostas HTTP de teste, sem abrir porta nem depender de rede.</summary>
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(responder(request));
}
