using System.Xml.Linq;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class ConcurrencyTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-conc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<string> CloneAsync(SvnTestRepo repo)
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, "a");
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, dest);
        Assert.True(clone.Code == 0, clone.StdErr);
        await HelperBinary.GitAsync(dest, "config", "user.name", "Dev A");
        await HelperBinary.GitAsync(dest, "config", "user.email", "a@exemplo.com");
        return dest;
    }

    static async Task<string[]> SvnMessagesAsync(SvnTestRepo repo)
    {
        var xml = XDocument.Parse(await repo.SvnAsync("log", "--xml", repo.RepoUrl + "/trunk"));
        return xml.Descendants("logentry").Select(e => e.Element("msg")?.Value ?? "").Reverse().ToArray();
    }

    /// <summary>trunk com a.txt (3 linhas) e b.txt; clone A com 1 commit local; cliente B commita no SVN.</summary>
    async Task<(SvnTestRepo Repo, string A, string LocalSha)> CenarioAsync(string arquivoDeB, string conteudoDeB, string conteudoDeA = "l1\nA\nl3\n")
    {
        var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "l1\nl2\nl3\n"u8.ToArray(), "base");
        await repo.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "base b");
        var a = await CloneAsync(repo);
        await PushLinearTests.CommitAsync(a, "a.txt", System.Text.Encoding.UTF8.GetBytes(conteudoDeA), "trabalho de A");
        var local = (await HelperBinary.GitAsync(a, "rev-parse", "main")).StdOut.Trim();
        await repo.CommitFileAsync(arquivoDeB, System.Text.Encoding.UTF8.GetBytes(conteudoDeB), "trabalho de B", "bob");
        return (repo, a, local);
    }

    [Fact]
    public async Task Push_com_servidor_avancado_e_recusado_sem_alterar_nada()
    {
        var (repo, a, local) = await CenarioAsync("b.txt", "b de B\n");
        await using var _ = repo;
        var antes = await repo.HeadRevisionAsync();

        var push = await HelperBinary.GitAsync(a, "push", "origin", "main");

        Assert.NotEqual(0, push.Code);
        Assert.Contains("pull", push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        Assert.Equal("trabalho de B", (await SvnMessagesAsync(repo))[^1]);
        Assert.Equal(local, (await HelperBinary.GitAsync(a, "rev-parse", "main")).StdOut.Trim());
    }

    [Fact]
    public async Task Depois_do_pull_rebase_o_push_entra_sem_sobrescrever()
    {
        var (repo, a, _) = await CenarioAsync("b.txt", "b de B\n");
        await using var _r = repo;

        var pull = await HelperBinary.GitAsync(a, "pull");
        Assert.True(pull.Code == 0, pull.StdErr);
        var push = await HelperBinary.GitAsync(a, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        var msgs = await SvnMessagesAsync(repo);
        Assert.Equal(new[] { "trabalho de B", "trabalho de A" }, msgs[^2..]);
        Assert.Equal("l1\nA\nl3\n", await repo.CatAsync(repo.RepoUrl + "/trunk/a.txt"));
        Assert.Equal("b de B\n", await repo.CatAsync(repo.RepoUrl + "/trunk/b.txt"));
    }

    [Fact]
    public async Task Conflito_de_conteudo_para_no_rebase_e_preserva_trabalho()
    {
        var (repo, a, _) = await CenarioAsync("a.txt", "l1\nB\nl3\n");
        await using var _r = repo;
        var antes = await repo.HeadRevisionAsync();

        var pull = await HelperBinary.GitAsync(a, "pull");

        Assert.NotEqual(0, pull.Code);
        var texto = await File.ReadAllTextAsync(Path.Combine(a, "a.txt"));
        Assert.Contains("<<<<<<<", texto);
        Assert.Contains("A", texto);
        Assert.Contains("B", texto);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        await HelperBinary.GitAsync(a, "rebase", "--abort");
    }

    [Fact]
    public async Task Pusher_com_ref_remota_desatualizada_recusa_sem_criar_revisao()
    {
        // B mexe em outro arquivo: o svn commit de A seria aceito e a revisao de B ficaria fora do Git
        var (repo, a, local) = await CenarioAsync("b.txt", "b de B\n");
        await using var _r = repo;
        var antes = await repo.HeadRevisionAsync();
        var git = new GitClient(a);
        var svn = new SvnClient();
        var state = new StateStore(git.GitDir);
        var pusher = new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(state.Root, "wc")), state);

        var ex = await Assert.ThrowsAsync<PushRejectedException>(() =>
            pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + "/trunk", "refs/svn/origin/heads/main", dryRun: false));

        Assert.Contains("git pull", ex.Message);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        Assert.Equal(local, (await git.RevParseAsync("refs/heads/main")));
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
