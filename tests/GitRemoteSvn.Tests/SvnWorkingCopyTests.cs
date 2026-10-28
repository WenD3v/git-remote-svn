using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class SvnWorkingCopyTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-wc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task Ensure_faz_checkout_e_update()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var r2 = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        var r3 = await repo.CommitFileAsync("a.txt", "2"u8.ToArray(), "segundo");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());

        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", r2);
        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(path, "a.txt")));

        var path2 = await wc.EnsureAsync(repo.RepoUrl + "/trunk", r3);
        Assert.Equal(path, path2);
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(path, "a.txt")));
    }

    [Fact]
    public async Task ApplyTree_adiciona_modifica_renomeia_e_apaga()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var head = await repo.CommitFileAsync("old/b.txt", "b"u8.ToArray(), "b");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        await wc.ApplyTreeAsync(path, new[]
        {
            new ImportFile("a.txt", "mudou"u8.ToArray()),
            new ImportFile("old/b.txt", null),
            new ImportFile("b2.txt", "b"u8.ToArray()),
            new ImportFile("sub/novo/c.txt", "c"u8.ToArray()),
            new ImportFile("ação.txt", "acento"u8.ToArray()),
        });
        var rev = await wc.CommitAsync(path, "aplica arvore");

        Assert.Equal(head + 1, rev);
        // --xml: a saida em texto do svn vem na codepage do console
        var xml = System.Xml.Linq.XDocument.Parse(await repo.SvnAsync("list", "-R", "--xml", repo.RepoUrl + "/trunk"));
        var list = xml.Descendants("entry")
            .Select(e => e.Element("name")!.Value + ((string?)e.Attribute("kind") == "dir" ? "/" : ""))
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { "a.txt", "ação.txt", "b2.txt", "sub/", "sub/novo/", "sub/novo/c.txt" }, list);
        Assert.Equal("mudou", await repo.CatAsync(repo.RepoUrl + "/trunk/a.txt"));
    }

    [Fact]
    public async Task Commit_retorna_numero_da_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var head = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);
        await wc.ApplyTreeAsync(path, new[] { new ImportFile("a.txt", "2"u8.ToArray()) });

        var rev = await wc.CommitAsync(path, "muda a");

        Assert.Equal(await repo.HeadRevisionAsync(), rev);
        Assert.Equal(head + 1, rev);
    }

    [Fact]
    public async Task Commit_sem_alteracao_nao_cria_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var head = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        var rev = await wc.CommitAsync(path, "nada");

        Assert.Equal(0, rev);
        Assert.Equal(head, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Revert_descarta_alteracoes_e_arquivos_soltos()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var head = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);
        await wc.ApplyTreeAsync(path, new[] { new ImportFile("a.txt", "2"u8.ToArray()), new ImportFile("n.txt", "n"u8.ToArray()) });
        await File.WriteAllTextAsync(Path.Combine(path, "solto.txt"), "x");

        await wc.RevertAsync(path);

        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(path, "a.txt")));
        Assert.False(File.Exists(Path.Combine(path, "n.txt")));
        Assert.False(File.Exists(Path.Combine(path, "solto.txt")));
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
