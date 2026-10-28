using System.Xml.Linq;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class MergePushTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-merge-" + Guid.NewGuid().ToString("N"));
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

    static async Task<(int Code, string StdOut, string StdErr)> Git(string repo, params string[] args)
    {
        var r = await HelperBinary.GitAsync(repo, args);
        return r;
    }

    /// <summary>trunk com a.txt; branches/b1 copiada do trunk com 2 commits. Devolve (copia, ultima revisao da b1).</summary>
    static async Task<(long CopyRev, long LastB1)> BranchPublicadaAsync(SvnTestRepo repo)
    {
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        var copy = await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches/b1", "cria b1");
        await repo.CommitFileToAsync("branches/b1", "b1.txt", "um\n"u8.ToArray(), "b1 um");
        var last = await repo.CommitFileToAsync("branches/b1", "b1.txt", "dois\n"u8.ToArray(), "b1 dois");
        return (copy, last);
    }

    static async Task<string[]> TrunkMessagesAsync(SvnTestRepo repo)
    {
        var xml = XDocument.Parse(await repo.SvnAsync("log", "--xml", repo.RepoUrl + "/trunk"));
        return xml.Descendants("logentry").Select(e => e.Element("msg")?.Value ?? "").Reverse().ToArray();
    }

    [Fact]
    public async Task Merge_no_ff_vira_uma_revisao_com_mergeinfo()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var (copy, lastB1) = await BranchPublicadaAsync(repo);
        var a = await CloneAsync(repo);
        var antes = (await TrunkMessagesAsync(repo)).Length;
        Assert.Equal(0, (await Git(a, "merge", "--no-ff", "-m", "merge b1", "origin/b1")).Code);

        var push = await Git(a, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        var msgs = await TrunkMessagesAsync(repo);
        Assert.Equal(antes + 1, msgs.Length);
        Assert.Equal("merge b1", msgs[^1]);
        Assert.Equal("dois\n", await repo.CatAsync(repo.RepoUrl + "/trunk/b1.txt"));
        var mergeinfo = (await repo.SvnAsync("propget", "svn:mergeinfo", repo.RepoUrl + "/trunk")).Trim();
        Assert.Equal($"/branches/b1:{copy}-{lastB1}", mergeinfo);
        // quem usa SVN e tenta mergear a b1 de novo nao recebe alteracao de conteudo
        // (o svn so acrescenta ao mergeinfo a revisao do proprio merge, inocua - igual apos merge feito no SVN)
        var wc = Path.Combine(NewDir(), "wc");
        await repo.SvnAsync("checkout", "-q", repo.RepoUrl + "/trunk", wc);
        await repo.SvnAsync("merge", "-q", "--non-interactive", repo.RepoUrl + "/branches/b1", wc);
        var st = (await repo.SvnAsync("status", wc)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(st, l => Assert.True(l.StartsWith(" M"), l));
        // o merge continua merge no Git, com o segundo pai na b1
        Assert.Equal(await RevAsync(a, "origin/b1"), await RevAsync(a, "main^2"));
        Assert.Contains("svn-id: ", (await Git(a, "log", "-1", "--format=%B", "main")).StdOut);
    }

    [Fact]
    public async Task Merge_de_branch_nao_publicada_e_recusado()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        await Git(a, "switch", "-q", "-c", "b1");
        await PushLinearTests.CommitAsync(a, "b1.txt", "x\n"u8.ToArray(), "so local");
        await Git(a, "switch", "-q", "main");
        Assert.Equal(0, (await Git(a, "merge", "--no-ff", "-m", "merge b1", "b1")).Code);
        var antes = await repo.HeadRevisionAsync();

        var push = await Git(a, "push", "origin", "main");

        Assert.NotEqual(0, push.Code);
        Assert.Contains("git push -u origin b1", push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Merge_octopus_e_recusado()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        foreach (var b in new[] { "b1", "b2" })
        {
            await Git(a, "switch", "-q", "-c", b, "main");
            await PushLinearTests.CommitAsync(a, b + ".txt", "x\n"u8.ToArray(), "em " + b);
        }
        await Git(a, "switch", "-q", "main");
        Assert.Equal(0, (await Git(a, "merge", "--no-ff", "-m", "octopus", "b1", "b2")).Code);
        var antes = await repo.HeadRevisionAsync();

        var push = await Git(a, "push", "origin", "main");

        Assert.NotEqual(0, push.Code);
        Assert.Contains("octopus", push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Commit_vazio_e_pulado_com_aviso()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        Assert.Equal(0, (await Git(a, "commit", "-q", "--allow-empty", "-m", "vazio")).Code);
        var antes = await repo.HeadRevisionAsync();
        var git = new GitClient(a);
        var svn = new SvnClient();
        var state = new StateStore(git.GitDir);
        var pusher = new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(state.Root, "wc")), state);

        var result = await pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + "/trunk", "refs/svn/origin/heads/main", dryRun: false);

        Assert.Equal(1, result.EmptyCommitsSkipped);
        Assert.Equal(0, result.RevisionsCreated);
        Assert.Contains(result.Warnings, w => w.Contains("sem alteracao") || w.Contains("nao altera"));
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Merge_sem_alteracao_nao_cria_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await BranchPublicadaAsync(repo);
        var a = await CloneAsync(repo);
        Assert.Equal(0, (await Git(a, "merge", "--no-ff", "-s", "ours", "-m", "merge vazio", "origin/b1")).Code);
        var antes = await repo.HeadRevisionAsync();

        var push = await Git(a, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        Assert.Equal("", (await Git(a, "status", "--porcelain")).StdOut);
    }

    static async Task<string> RevAsync(string repo, string rev) =>
        (await HelperBinary.GitAsync(repo, "rev-parse", rev)).StdOut.Trim();

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
