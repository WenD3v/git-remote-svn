using GitRemoteSvn.Svn;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class SvnClientTests
{
    [Fact]
    public async Task Info_retorna_url_uuid_e_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var svn = new SvnClient();

        var info = await svn.InfoAsync(repo.RepoUrl + "/trunk");

        Assert.Equal(repo.RepoUrl + "/trunk", info.Url);
        Assert.Equal(repo.RepoUrl, info.RepoRootUrl);
        Assert.False(string.IsNullOrWhiteSpace(info.Uuid));
        Assert.Equal(1, info.Revision);
    }

    [Fact]
    public async Task Log_retorna_autor_data_mensagem_e_paths()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("ola.txt", "oi"u8.ToArray(), "primeiro");
        await repo.CommitFileAsync("ola.txt", "oi2"u8.ToArray(), "segundo");
        var svn = new SvnClient();

        var revs = await svn.LogAsync(repo.RepoUrl + "/trunk", 2, 3, false);

        Assert.Equal(new long[] { 2, 3 }, revs.Select(r => r.Revision).ToArray());
        Assert.Equal("alice", revs[0].Author);
        Assert.Equal("primeiro", revs[0].Message);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - revs[0].Date).TotalMinutes) < 10);
        var change = Assert.Single(revs[1].Paths);
        Assert.Equal('M', change.Action);
        Assert.EndsWith("/trunk/ola.txt", change.Path);
        Assert.Equal("file", change.Kind);
    }

    [Fact]
    public async Task Log_com_copyfrom_expoe_origem()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("ola.txt", "oi"u8.ToArray(), "primeiro");
        await repo.CopyAsync(repo.RepoUrl + "/trunk@2", repo.RepoUrl + "/branches/b1", "branch");
        var svn = new SvnClient();

        var revs = await svn.LogAsync(repo.RepoUrl + "/branches/b1", 3, 3, true);

        var rev = Assert.Single(revs);
        var change = Assert.Single(rev.Paths);
        Assert.Equal('A', change.Action);
        Assert.EndsWith("/trunk", change.CopyFromPath);
        Assert.Equal(2, change.CopyFromRev);
    }

    [Fact]
    public async Task Cat_retorna_bytes_exatos()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await repo.CommitFileAsync("bin.dat", bytes, "binario");
        var svn = new SvnClient();

        var got = await svn.CatAsync(repo.RepoUrl + "/trunk/bin.dat@2");

        Assert.Equal(bytes, got);
    }
}
