using System.Xml.Linq;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class RecoveryTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<(SvnTestRepo Repo, string A)> TresCommitsLocaisAsync()
    {
        var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "0\n"u8.ToArray(), "base");
        var dir = NewDir();
        var a = Path.Combine(dir, "a");
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, a);
        Assert.True(clone.Code == 0, clone.StdErr);
        await HelperBinary.GitAsync(a, "config", "user.name", "Dev A");
        await HelperBinary.GitAsync(a, "config", "user.email", "a@exemplo.com");
        await PushLinearTests.CommitAsync(a, "a.txt", "1\n"u8.ToArray(), "um");
        await PushLinearTests.CommitAsync(a, "b.txt", "2\n"u8.ToArray(), "dois");
        await PushLinearTests.CommitAsync(a, "a.txt", "3\n"u8.ToArray(), "tres");
        return (repo, a);
    }

    static Pusher NewPusher(string clone, out StateStore state)
    {
        var git = new GitClient(clone);
        var svn = new SvnClient();
        state = new StateStore(git.GitDir);
        return new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(state.Root, "wc")), state);
    }

    static async Task<string[]> TrunkMessagesAsync(SvnTestRepo repo)
    {
        var xml = XDocument.Parse(await repo.SvnAsync("log", "--xml", repo.RepoUrl + "/trunk"));
        return xml.Descendants("logentry").Select(e => e.Element("msg")?.Value ?? "").Reverse().ToArray();
    }

    const string Trunk = "/trunk";
    const string Remote = "refs/svn/origin/heads/main";

    [Fact]
    public async Task Push_interrompido_ao_segundo_commit_nao_duplica_na_reexecucao()
    {
        var (repo, a) = await TresCommitsLocaisAsync();
        await using var repoGuard = repo;
        var antes = await repo.HeadRevisionAsync();
        var pusher = NewPusher(a, out _);
        pusher.AfterEachRevision = n => n == 2 ? throw new IOException("queda simulada") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + Trunk, Remote, false));
        Assert.Equal(antes + 2, await repo.HeadRevisionAsync());

        var result = await NewPusher(a, out _).PushBranchAsync("refs/heads/main", repo.RepoUrl + Trunk, Remote, false);

        Assert.Equal(antes + 3, await repo.HeadRevisionAsync());
        Assert.Equal(1, result.RevisionsCreated);
        Assert.Equal(new[] { "base", "um", "dois", "tres" }, (await TrunkMessagesAsync(repo))[1..]);
        var bodies = (await HelperBinary.GitAsync(a, "log", "-3", "--format=%B%x00", "main")).StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.All(bodies, b => Assert.Contains("svn-id: ", b));
        Assert.Equal("", (await HelperBinary.GitAsync(a, "status", "--porcelain")).StdOut);
    }

    [Fact]
    public async Task Revisao_alheia_apos_interrupcao_continua_recusada()
    {
        var (repo, a) = await TresCommitsLocaisAsync();
        await using var repoGuard = repo;
        var pusher = NewPusher(a, out _);
        pusher.AfterEachRevision = n => n == 1 ? throw new IOException("queda simulada") : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + Trunk, Remote, false));
        await repo.CommitFileAsync("c.txt", "c\n"u8.ToArray(), "colega", "bob");
        var antes = await repo.HeadRevisionAsync();

        var ex = await Assert.ThrowsAsync<PushRejectedException>(() =>
            NewPusher(a, out _).PushBranchAsync("refs/heads/main", repo.RepoUrl + Trunk, Remote, false));

        Assert.Contains("git pull", ex.Message);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Log_contem_revisoes_criadas_e_erro()
    {
        var (repo, a) = await TresCommitsLocaisAsync();
        await using var repoGuard = repo;
        var antes = await repo.HeadRevisionAsync();
        var pusher = NewPusher(a, out var state);
        var log = new SyncLog(new GitClient(a).GitDir);
        pusher.Log = log;
        pusher.AfterEachRevision = n => n == 2 ? throw new IOException("queda simulada") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + Trunk, Remote, false));

        Assert.StartsWith(Path.Combine(state.Root, "log"), log.FilePath);
        var texto = await File.ReadAllTextAsync(log.FilePath);
        Assert.Contains($"r{antes + 1}", texto);
        Assert.Contains($"r{antes + 2}", texto);
        Assert.Contains("queda simulada", texto);
    }

    [Fact]
    public async Task Dry_run_lista_o_que_seria_enviado()
    {
        var (repo, a) = await TresCommitsLocaisAsync();
        await using var repoGuard = repo;
        var antes = await repo.HeadRevisionAsync();

        var push = await HelperBinary.GitAsync(a, "push", "--dry-run", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        foreach (var m in new[] { "um", "dois", "tres" })
            Assert.Contains($" {m} -> 1 revisao", push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
