using GitRemoteSvn.Update;
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
}
