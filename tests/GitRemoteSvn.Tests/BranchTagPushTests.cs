using System.Xml.Linq;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class BranchTagPushTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-btp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<string> CloneAsync(SvnTestRepo repo, string nome = "a")
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, nome);
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, dest);
        Assert.True(clone.Code == 0, clone.StdErr);
        await HelperBinary.GitAsync(dest, "config", "user.name", "Dev " + nome);
        await HelperBinary.GitAsync(dest, "config", "user.email", nome + "@exemplo.com");
        return dest;
    }

    static Task<(int Code, string StdOut, string StdErr)> Git(string repo, params string[] args) =>
        HelperBinary.GitAsync(repo, args);

    static async Task<bool> SvnExistsAsync(SvnTestRepo repo, string path) =>
        (await SvnTestRepo.RunCoreAsync("svn", repo.RootPath, "info", repo.RepoUrl + "/" + path)).Code == 0;

    static async Task<SvnTestRepo> RepoAsync()
    {
        var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        return repo;
    }

    [Fact]
    public async Task Push_de_branch_nova_cria_svn_copy_e_commits()
    {
        await using var repo = await RepoAsync();
        var a = await CloneAsync(repo);
        await Git(a, "switch", "-q", "-c", "b1");
        await PushLinearTests.CommitAsync(a, "c.txt", "c\n"u8.ToArray(), "na b1");

        var push = await Git(a, "push", "-u", "origin", "b1");

        Assert.True(push.Code == 0, push.StdErr);
        var log = XDocument.Parse(await repo.SvnAsync("log", "--xml", "-v", "--stop-on-copy", repo.RepoUrl + "/branches/b1"));
        var entries = log.Descendants("logentry").Reverse().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("/trunk", (string?)entries[0].Descendants("path").First().Attribute("copyfrom-path"));
        Assert.Equal("na b1", entries[1].Element("msg")!.Value);
        var wc = Path.Combine(NewDir(), "colega");
        await repo.SvnAsync("checkout", "-q", repo.RepoUrl + "/branches/b1", wc);
        Assert.Equal("1\n", await File.ReadAllTextAsync(Path.Combine(wc, "a.txt")));
        Assert.Equal("c\n", await File.ReadAllTextAsync(Path.Combine(wc, "c.txt")));
        Assert.Equal((await Git(a, "rev-parse", "b1")).StdOut, (await Git(a, "rev-parse", "origin/b1")).StdOut);
        Assert.Contains("svn-id: ", (await Git(a, "log", "-1", "--format=%B", "b1")).StdOut);
        Assert.Equal("origin", (await Git(a, "config", "branch.b1.remote")).StdOut.Trim());
    }

    [Fact]
    public async Task Branch_com_barra_no_nome_vira_hierarquia()
    {
        await using var repo = await RepoAsync();
        var a = await CloneAsync(repo);
        await Git(a, "switch", "-q", "-c", "feature/x");
        await PushLinearTests.CommitAsync(a, "c.txt", "c\n"u8.ToArray(), "na feature");

        var push = await Git(a, "push", "-u", "origin", "feature/x");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal("c\n", await repo.CatAsync(repo.RepoUrl + "/branches/feature/x/c.txt"));
        var b = await CloneAsync(repo, "b");
        Assert.Equal(new[] { "na feature", "base" },
            (await Git(b, "log", "--format=%s", "refs/remotes/origin/feature/x")).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("", (await Git(b, "rev-parse", "--verify", "-q", "refs/remotes/origin/feature")).StdOut.Trim());
    }

    [Fact]
    public async Task Push_de_tag_cria_copia_e_recusa_tag_existente()
    {
        await using var repo = await RepoAsync();
        var a = await CloneAsync(repo);
        await Git(a, "tag", "-a", "-m", "versao 1", "v1");

        var push = await Git(a, "push", "origin", "v1");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal("1\n", await repo.CatAsync(repo.RepoUrl + "/tags/v1/a.txt"));
        var fetch = await Git(a, "fetch", "--tags");
        Assert.True(fetch.Code == 0, fetch.StdErr);

        // B nao sabe da v1 e cria a sua propria em outro commit
        var b = await CloneAsync(repo, "b");
        await PushLinearTests.CommitAsync(b, "a.txt", "2\n"u8.ToArray(), "dois");
        Assert.Equal(0, (await Git(b, "push", "origin", "main")).Code);
        await Git(b, "tag", "-f", "v1");
        var antes = await repo.HeadRevisionAsync();

        var recusa = await Git(b, "push", "origin", "v1");
        var forcado = await Git(b, "push", "--force", "origin", "v1");

        Assert.NotEqual(0, recusa.Code);
        Assert.NotEqual(0, forcado.Code);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        Assert.Equal("1\n", await repo.CatAsync(repo.RepoUrl + "/tags/v1/a.txt"));
    }

    [Fact]
    public async Task Tag_de_commit_nao_publicado_e_recusada()
    {
        await using var repo = await RepoAsync();
        var a = await CloneAsync(repo);
        await PushLinearTests.CommitAsync(a, "a.txt", "local\n"u8.ToArray(), "so local");
        await Git(a, "tag", "v2");

        var push = await Git(a, "push", "origin", "v2");

        Assert.NotEqual(0, push.Code);
        Assert.Contains("publique", push.StdErr);
        Assert.False(await SvnExistsAsync(repo, "tags/v2"));
    }

    [Fact]
    public async Task Colega_svn_cria_branch_e_elas_se_enxergam()
    {
        await using var repo = await RepoAsync();
        var a = await CloneAsync(repo);
        var b = await CloneAsync(repo, "b");
        await Git(a, "switch", "-q", "-c", "de-a");
        await PushLinearTests.CommitAsync(a, "a.txt", "a\n"u8.ToArray(), "de A");
        Assert.Equal(0, (await Git(a, "push", "-u", "origin", "de-a")).Code);
        await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches/do-svn", "colega cria branch");

        var lsB = await Git(b, "ls-remote", "origin");
        var lsA = await Git(a, "ls-remote", "origin");

        Assert.True(lsB.Code == 0, lsB.StdErr);
        Assert.Contains("refs/heads/de-a", lsB.StdOut);
        Assert.Contains("refs/heads/do-svn", lsB.StdOut);
        Assert.Contains("refs/heads/do-svn", lsA.StdOut);
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
