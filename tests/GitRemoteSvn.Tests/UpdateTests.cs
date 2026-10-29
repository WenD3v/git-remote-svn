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
}
