using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class BranchTagTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-bt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>trunk com a.txt; branches/b1 copiada do trunk com 1 commit proprio; tags/t1 copiada do trunk.</summary>
    static async Task<SvnTestRepo> RepoComBranchETagAsync()
    {
        var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches/b1", "cria b1");
        await repo.CommitFileToAsync("branches/b1", "b.txt", "b"u8.ToArray(), "na b1");
        await repo.CopyAsync(repo.RepoUrl + "/trunk", repo.RepoUrl + "/tags/t1", "cria t1");
        return repo;
    }

    async Task<string> CloneAsync(SvnTestRepo repo, string? url = null)
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, "dest");
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + (url ?? repo.RepoUrl), dest);
        Assert.True(clone.Code == 0, clone.StdErr);
        return dest;
    }

    static async Task<string> RevAsync(string repo, string rev) =>
        (await HelperBinary.GitAsync(repo, "rev-parse", "--verify", "-q", rev)).StdOut.Trim();

    static async Task<string[]> SubjectsAsync(string repo, string rev) =>
        (await HelperBinary.GitAsync(repo, "log", "--format=%s", rev)).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Clone_traz_branches_e_tags()
    {
        await using var repo = await RepoComBranchETagAsync();

        var dest = await CloneAsync(repo);

        Assert.Equal(new[] { "na b1", "primeiro" }, await SubjectsAsync(dest, "refs/remotes/origin/b1"));
        var tags = (await HelperBinary.GitAsync(dest, "tag", "--list")).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "t1" }, tags);
        Assert.Equal(await RevAsync(dest, "main"), await RevAsync(dest, "t1^{commit}"));
    }

    [Fact]
    public async Task Branch_criada_por_copia_nao_duplica_historico()
    {
        await using var repo = await RepoComBranchETagAsync();

        var dest = await CloneAsync(repo);

        var mergeBase = (await HelperBinary.GitAsync(dest, "merge-base", "main", "refs/remotes/origin/b1")).StdOut.Trim();
        Assert.Equal(await RevAsync(dest, "main"), mergeBase);
        Assert.Equal("2", (await HelperBinary.GitAsync(dest, "rev-list", "--count", "refs/remotes/origin/b1")).StdOut.Trim());
    }

    [Fact]
    public async Task Branch_apagada_no_svn_some_no_fetch()
    {
        await using var repo = await RepoComBranchETagAsync();
        var dest = await CloneAsync(repo);
        Assert.NotEqual("", await RevAsync(dest, "refs/remotes/origin/b1"));
        await repo.SvnAsync("rm", "-m", "apaga b1", repo.RepoUrl + "/branches/b1");

        var fetch = await HelperBinary.GitAsync(dest, "fetch");

        Assert.True(fetch.Code == 0, fetch.StdErr);
        Assert.Equal("", await RevAsync(dest, "refs/remotes/origin/b1"));
        Assert.Contains("b1", fetch.StdErr);
    }

    [Fact]
    public async Task Tag_modificada_no_svn_nao_move_tag_local_e_avisa()
    {
        await using var repo = await RepoComBranchETagAsync();
        var dest = await CloneAsync(repo);
        var antes = await RevAsync(dest, "t1^{commit}");
        await repo.CommitFileToAsync("tags/t1", "c.txt", "c"u8.ToArray(), "commit na tag");

        var fetch = await HelperBinary.GitAsync(dest, "fetch", "--tags");

        Assert.True(fetch.Code == 0, fetch.StdErr);
        Assert.Equal(antes, await RevAsync(dest, "t1^{commit}"));
        Assert.Contains("t1", fetch.StdErr);
    }

    [Fact]
    public async Task Fetch_incremental_traz_so_revisoes_novas()
    {
        await using var repo = await RepoComBranchETagAsync();
        var dest = await CloneAsync(repo);
        var localAntes = await RevAsync(dest, "main");
        await repo.CommitFileAsync("a.txt", "2"u8.ToArray(), "segundo");

        var fetch = await HelperBinary.GitAsync(dest, "fetch");

        Assert.True(fetch.Code == 0, fetch.StdErr);
        Assert.Equal("1", (await HelperBinary.GitAsync(dest, "rev-list", "--count", "main..refs/remotes/origin/main")).StdOut.Trim());
        Assert.Equal(localAntes, await RevAsync(dest, "main"));
        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
        Assert.Equal(new[] { "segundo", "primeiro" }, await SubjectsAsync(dest, "refs/remotes/origin/main"));
    }

    [Fact]
    public async Task Trunk_criado_por_copia_de_fora_do_projeto_traz_arquivos()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var origem = Path.Combine(repo.RootPath, "origem");
        Directory.CreateDirectory(Path.Combine(origem, "sub"));
        await File.WriteAllTextAsync(Path.Combine(origem, "x.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(origem, "sub", "y.txt"), "y");
        await repo.SvnAsync("import", "-m", "legado", origem, repo.RepoUrl + "/legado/proj");
        await repo.SvnAsync("mkdir", "-m", "novo layout", repo.RepoUrl + "/novo", repo.RepoUrl + "/novo/branches", repo.RepoUrl + "/novo/tags");
        await repo.CopyAsync(repo.RepoUrl + "/legado/proj", repo.RepoUrl + "/novo/trunk", "reorganiza");

        var dest = await CloneAsync(repo, repo.RepoUrl + "/novo");

        Assert.Equal(new[] { "reorganiza" }, await SubjectsAsync(dest, "main"));
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(dest, "x.txt")));
        Assert.Equal("y", await File.ReadAllTextAsync(Path.Combine(dest, "sub", "y.txt")));
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
