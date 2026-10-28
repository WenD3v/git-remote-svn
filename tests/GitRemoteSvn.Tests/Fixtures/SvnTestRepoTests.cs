using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests.Fixtures;

public class SvnTestRepoTests
{
    [Fact]
    public async Task Fixture_cria_layout_e_commita()
    {
        await using var repo = await SvnTestRepo.CreateAsync();

        var rev = await repo.CommitFileAsync("ola.txt", "oi"u8.ToArray(), "primeiro");

        Assert.Equal(2, rev);
        Assert.Equal("oi", await repo.CatAsync(repo.RepoUrl + "/trunk/ola.txt"));
        Assert.True(Directory.Exists(Path.Combine(repo.WcPath, ".svn")));
    }
}
