using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class RemoteHelperCloneTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task Git_clone_svn_traz_historico_arquivos_e_config()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await repo.CommitFileAsync("pasta/b.txt", "2\r\n"u8.ToArray(), "segundo");
        await repo.CommitFileAsync("a.txt", "3"u8.ToArray(), "terceiro");
        var dir = NewDir();
        var dest = Path.Combine(dir, "dest");

        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, dest);

        Assert.True(clone.Code == 0, clone.StdErr);
        Assert.True(Directory.Exists(Path.Combine(dest, ".git")));
        var log = await HelperBinary.GitAsync(dest, "log", "--format=%s", "main");
        Assert.Equal(new[] { "terceiro", "segundo", "primeiro" },
            log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("3", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
        Assert.Equal("2\r\n"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(dest, "pasta", "b.txt")));
        Assert.Equal("false", (await HelperBinary.GitAsync(dest, "config", "core.autocrlf")).StdOut.Trim());
        Assert.Equal("true", (await HelperBinary.GitAsync(dest, "config", "branch.main.rebase")).StdOut.Trim());
        Assert.Equal("svn::" + repo.RepoUrl, (await HelperBinary.GitAsync(dest, "config", "remote.origin.url")).StdOut.Trim());
        var remoteMain = await HelperBinary.GitAsync(dest, "rev-parse", "refs/remotes/origin/main");
        var localMain = await HelperBinary.GitAsync(dest, "rev-parse", "main");
        Assert.Equal(localMain.StdOut, remoteMain.StdOut);
    }

    [Fact]
    public async Task Clone_com_autocrlf_true_nao_altera_finais_de_linha()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("lf.txt", "a\nb\n"u8.ToArray(), "lf");
        var dir = NewDir();
        var dest = Path.Combine(dir, "dest");

        var clone = await HelperBinary.GitAsync(dir, "-c", "core.autocrlf=true", "clone", "svn::" + repo.RepoUrl, dest);

        Assert.True(clone.Code == 0, clone.StdErr);
        Assert.Equal("a\nb\n"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(dest, "lf.txt")));
        Assert.Equal("", (await HelperBinary.GitAsync(dest, "status", "--porcelain")).StdOut);
    }

    [Fact]
    public async Task Git_clone_de_url_inexistente_falha_com_mensagem()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var dir = NewDir();

        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl + "/nao-existe", Path.Combine(dir, "x"));

        Assert.NotEqual(0, clone.Code);
        Assert.Contains("git-remote-svn", clone.StdErr);
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
