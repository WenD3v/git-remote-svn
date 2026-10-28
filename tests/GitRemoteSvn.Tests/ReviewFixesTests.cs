using System.Xml.Linq;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GitRemoteSvn.Tests;

/// <summary>Achados da revisao final: cada teste reproduz um caminho que perdia trabalho ou travava o uso real.</summary>
public class ReviewFixesTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<string> CloneAsync(SvnTestRepo repo, string name = "a")
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, name);
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, dest);
        Assert.True(clone.Code == 0, clone.StdErr);
        await HelperBinary.GitAsync(dest, "config", "user.name", "Dev A");
        await HelperBinary.GitAsync(dest, "config", "user.email", "a@exemplo.com");
        return dest;
    }

    static Pusher NewPusher(string clone)
    {
        var git = new GitClient(clone);
        var svn = new SvnClient();
        var state = new StateStore(git.GitDir);
        return new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(state.Root, "wc")), state);
    }

    static async Task<string[]> TrunkMessagesAsync(SvnTestRepo repo)
    {
        var xml = XDocument.Parse(await repo.SvnAsync("log", "--xml", repo.RepoUrl + "/trunk"));
        return xml.Descendants("logentry").Select(e => e.Element("msg")?.Value ?? "").Reverse().ToArray();
    }

    const string Remote = "refs/svn/origin/heads/main";

    [Fact]
    public async Task Estado_adiantado_em_relacao_a_ref_nao_pula_revisoes_no_fetch()
    {
        // queda no meio do fetch: o estado chegou a gravar a revisao, mas o fast-import nao moveu a ref
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1\n"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        var rev = await repo.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "colega", "bob");
        new StateStore(new GitClient(a).GitDir).SetLastRev(Remote, rev);

        var fetch = await HelperBinary.GitAsync(a, "fetch");

        Assert.True(fetch.Code == 0, fetch.StdErr);
        Assert.Equal("colega", (await HelperBinary.GitAsync(a, "log", "-1", "--format=%s", "origin/main")).StdOut.Trim());
    }

    [Fact]
    public async Task Revisao_alheia_com_a_mesma_mensagem_nao_e_adotada()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "0\n"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        await PushLinearTests.CommitAsync(a, "a.txt", "1\n"u8.ToArray(), "um");
        await PushLinearTests.CommitAsync(a, "b.txt", "2\n"u8.ToArray(), "ajuste");
        var pusher = NewPusher(a);
        pusher.AfterEachRevision = n => n == 1 ? throw new IOException("queda simulada") : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + "/trunk", Remote, false));
        await repo.CommitFileAsync("c.txt", "c\n"u8.ToArray(), "ajuste", "bob");
        var antes = await repo.HeadRevisionAsync();

        var ex = await Assert.ThrowsAsync<PushRejectedException>(() =>
            NewPusher(a).PushBranchAsync("refs/heads/main", repo.RepoUrl + "/trunk", Remote, false));

        Assert.Contains("git pull", ex.Message);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Push_de_arquivo_com_arroba_no_nome()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        await PushLinearTests.CommitAsync(a, "img/logo@2x.png", new byte[] { 0x89, 0x50, 0, 1 }, "logo retina");

        var push = await HelperBinary.GitAsync(a, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal("logo retina", (await TrunkMessagesAsync(repo))[^1]);
        var ls = await repo.SvnAsync("ls", repo.RepoUrl + "/trunk/img");
        Assert.Contains("logo@2x.png", ls);
    }

    [Fact]
    public async Task Push_depois_de_pull_com_merge_e_recusado_pedindo_rebase()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "a\n"u8.ToArray(), "base");
        await repo.CommitFileAsync("b.txt", "b\n"u8.ToArray(), "base b");
        var a = await CloneAsync(repo);
        Assert.Equal("true", (await HelperBinary.GitAsync(a, "config", "pull.rebase")).StdOut.Trim());
        await PushLinearTests.CommitAsync(a, "a.txt", "a de A\n"u8.ToArray(), "trabalho de A");
        await repo.CommitFileAsync("b.txt", "b de B\n"u8.ToArray(), "trabalho de B", "bob");
        var pull = await HelperBinary.GitAsync(a, "pull", "--no-rebase", "--no-edit");
        Assert.True(pull.Code == 0, pull.StdErr);
        var antes = await repo.HeadRevisionAsync();

        var push = await HelperBinary.GitAsync(a, "push", "origin", "main");

        Assert.NotEqual(0, push.Code);
        Assert.Contains("git pull --rebase", push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Branch_apagada_e_recriada_no_svn_chega_com_a_arvore_nova()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches/b1", "cria b1");
        await repo.CommitFileToAsync("branches/b1", "so-na-antiga.txt", "x"u8.ToArray(), "na b1 antiga");
        var a = await CloneAsync(repo);
        await repo.SvnAsync("rm", "-m", "apaga b1", repo.RepoUrl + "/branches/b1");
        await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches/b1", "recria b1");

        var fetch = await HelperBinary.GitAsync(a, "fetch");

        Assert.True(fetch.Code == 0, fetch.StdErr);
        var files = (await HelperBinary.GitAsync(a, "ls-tree", "-r", "--name-only", "origin/b1")).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "a.txt" }, files);
    }

    [Fact]
    public async Task GitDir_de_um_worktree_aponta_para_o_repositorio_comum()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        var wt = Path.Combine(Path.GetDirectoryName(a)!, "wt");
        var add = await HelperBinary.GitAsync(a, "worktree", "add", "-q", "-b", "outra", wt);
        Assert.True(add.Code == 0, add.StdErr);

        Assert.Equal(Path.GetFullPath(new GitClient(a).GitDir), Path.GetFullPath(new GitClient(wt).GitDir));
    }

    [Fact]
    public async Task Segundo_helper_no_mesmo_repositorio_falha_com_mensagem_clara()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var a = await CloneAsync(repo);
        var lockFile = Path.Combine(new GitClient(a).GitDir, "svn", "lock");

        (int Code, string StdOut, string StdErr) fetch;
        using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            fetch = await HelperBinary.GitAsync(a, "fetch");

        Assert.NotEqual(0, fetch.Code);
        Assert.Contains("outro git-remote-svn", fetch.StdErr);
    }

    [Fact]
    public async Task Primeiro_push_para_trunk_vazio()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var a = await CloneAsync(repo);
        await HelperBinary.GitAsync(a, "symbolic-ref", "HEAD", "refs/heads/main");
        await PushLinearTests.CommitAsync(a, "a.txt", "inicio\n"u8.ToArray(), "inicio do projeto");

        var push = await HelperBinary.GitAsync(a, "push", "-u", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal("inicio\n", await repo.CatAsync(repo.RepoUrl + "/trunk/a.txt"));
        Assert.Equal("inicio do projeto", (await TrunkMessagesAsync(repo))[^1]);
        Assert.Contains("svn-id: ", (await HelperBinary.GitAsync(a, "log", "-1", "--format=%B", "main")).StdOut);
    }

    [Fact]
    public async Task Numero_da_revisao_sai_de_mensagem_traduzida_do_svn()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        var fake = Path.Combine(dir, "svn.cmd");
        File.WriteAllText(fake, "@echo off\r\necho Enviando        a.txt\r\necho Revisao 123 commitada.\r\n");

        var rev = await new SvnWorkingCopy(new SvnClient(fake), dir).CommitAsync(dir, "msg");

        Assert.Equal(123, rev);
    }

    [Fact]
    public async Task Working_copy_travada_por_queda_e_destravada_no_proximo_uso()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var head = await repo.HeadRevisionAsync();
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(path, ".svn", "wc.db")};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO WC_LOCK (wc_id, local_dir_relpath, locked_levels) VALUES (1, '', -1)";
            cmd.ExecuteNonQuery();
        }

        var again = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        Assert.Equal(path, again);
    }

    [Fact]
    public async Task Svn_ignore_de_subpasta_vale_no_git()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("sub/x.txt", "x"u8.ToArray(), "base");
        await repo.SvnAsync("propset", "svn:ignore", "bin", Path.Combine(repo.WcPath, "sub"));
        await repo.CommitAsync("ignora bin");
        var a = await CloneAsync(repo);

        var ignorado = await HelperBinary.GitAsync(a, "check-ignore", "-q", "sub/bin/a.dll");
        var fora = await HelperBinary.GitAsync(a, "check-ignore", "-q", "outra/bin/a.dll");

        Assert.Equal(0, ignorado.Code);
        Assert.Equal(1, fora.Code);
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
