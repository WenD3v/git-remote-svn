using GitRemoteSvn.Git;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class FastImportTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "grs-git-" + Guid.NewGuid().ToString("N"));
    readonly string _repo;

    public FastImportTests()
    {
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        SvnTestRepo.Run("git", _root, "init", "-b", "main", _repo);
    }

    [Fact]
    public async Task FastImport_cria_commit_com_autor_data_mensagem_e_arquivos()
    {
        var git = new GitClient(_repo);
        var bin = new byte[] { 0, 1, 2, 250, 251, 255 };
        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await using (var w = await git.BeginFastImportAsync())
        {
            await w.CommitAsync("refs/heads/main", null, ("Alice", "alice@x.com", date), "primeiro",
                new[] { new ImportFile("ola.txt", "oi"u8.ToArray()), new ImportFile("bin.dat", bin) });
        }
        await using (var w = await git.BeginFastImportAsync())
        {
            await w.CommitAsync("refs/heads/main", "refs/heads/main", ("Alice", "alice@x.com", date), "segundo",
                new[] { new ImportFile("ola.txt", null) });
        }

        var log = await git.RunAsync("log", "--format=%an|%ae|%s");
        Assert.Equal("Alice|alice@x.com|segundo\nAlice|alice@x.com|primeiro", log.Trim());
        var dates = await git.RunAsync("log", "--format=%aI", "-1", "HEAD~1");
        Assert.Equal(date, DateTimeOffset.Parse(dates.Trim()));
        var shown = await SvnTestRepo.RunCoreAsync("git", _repo, "show", "HEAD~1:bin.dat");
        Assert.Equal(0, shown.Code);
        var files = await git.RunAsync("ls-tree", "-r", "--name-only", "HEAD");
        Assert.Equal("bin.dat", files.Trim());
        Assert.True(Directory.Exists(git.GitDir));
    }

    public void Dispose() => SvnTestRepo.DeleteTree(_root);
}
