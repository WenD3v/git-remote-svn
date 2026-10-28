using GitRemoteSvn.Sync;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class LayoutTests
{
    [Fact]
    public async Task Discover_com_trunk_branches_tags()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("ola.txt", "oi"u8.ToArray(), "primeiro");
        await repo.CopyAsync(repo.RepoUrl + "/trunk@2", repo.RepoUrl + "/branches/b1", "branch");
        await repo.CopyAsync(repo.RepoUrl + "/trunk@2", repo.RepoUrl + "/tags/t1", "tag");

        var layout = await ProjectLayout.DiscoverAsync(new SvnClient(), repo.RepoUrl);

        Assert.True(layout.Standard);
        Assert.Equal(repo.RepoUrl, layout.RootUrl);
        Assert.Equal(repo.RepoUrl + "/trunk", layout.TrunkUrl);
        Assert.Equal(new[] { "b1" }, layout.Branches);
        Assert.Equal(new[] { "t1" }, layout.Tags);
    }

    [Fact]
    public async Task Discover_pasta_simples()
    {
        await using var repo = await SvnTestRepo.CreateAsync();

        var layout = await ProjectLayout.DiscoverAsync(new SvnClient(), repo.RepoUrl + "/trunk");

        Assert.False(layout.Standard);
        Assert.Equal(repo.RepoUrl + "/trunk", layout.TrunkUrl);
        Assert.Empty(layout.Branches);
        Assert.Empty(layout.Tags);
    }
}

public class SvnIdTests
{
    [Fact]
    public void SvnId_roundtrip_e_parse_da_ultima_linha()
    {
        var line = SvnId.Format("https://x/svn/p/trunk", 42, "uuid-1");
        var message = "minha mensagem\n\n" + line;

        Assert.True(SvnId.TryParse(message, out var url, out var rev));
        Assert.Equal("https://x/svn/p/trunk", url);
        Assert.Equal(42, rev);
        Assert.False(SvnId.TryParse("mensagem sem trailer", out _, out _));
    }

    [Fact]
    public void AuthorMap_padrao_e_arquivo()
    {
        var def = AuthorMap.Load(null);
        Assert.Equal(("alice", "alice@svn.local"), def.Resolve("alice"));

        var file = Path.Combine(Path.GetTempPath(), "autores-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "alice = Alice Silva <alice@exemplo.com>\n# comentario\n");
        try
        {
            var map = AuthorMap.Load(file);
            Assert.Equal(("Alice Silva", "alice@exemplo.com"), map.Resolve("alice"));
            Assert.Equal(("bob", "bob@svn.local"), map.Resolve("bob"));
        }
        finally { File.Delete(file); }
    }
}
