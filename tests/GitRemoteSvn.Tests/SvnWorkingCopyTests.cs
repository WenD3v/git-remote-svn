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
    public async Task Ensure_faz_checkout_raso_e_reaproveita_a_wc()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var r2 = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        var r3 = await repo.CommitFileAsync("a.txt", "2"u8.ToArray(), "segundo");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());

        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", r2);
        // o checkout nao baixa a arvore: o conteudo entra so no que o push vai alterar
        Assert.False(File.Exists(Path.Combine(path, "a.txt")));

        var path2 = await wc.EnsureAsync(repo.RepoUrl + "/trunk", r3);
        Assert.Equal(path, path2);
    }

    [Fact]
    public async Task ApplyTree_baixa_so_os_arquivos_alterados()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var head = await repo.CommitFileAsync("intocado.txt", "x"u8.ToArray(), "x");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head,
            new[] { new ImportFile("a.txt", "2"u8.ToArray()) });

        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(path, "a.txt")));
        Assert.False(File.Exists(Path.Combine(path, "intocado.txt")));
    }

    [Fact]
    public async Task ApplyTree_adiciona_modifica_renomeia_e_apaga()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var head = await repo.CommitFileAsync("old/b.txt", "b"u8.ToArray(), "b");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head, new[]
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
    public async Task Apagar_pasta_inteira_sai_numa_unica_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("old/1.txt", "1"u8.ToArray(), "1");
        var head = await repo.CommitFileAsync("old/2.txt", "2"u8.ToArray(), "2");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head, new[]
        {
            new ImportFile("old/1.txt", null),
            new ImportFile("old/2.txt", null),
            new ImportFile("novo.txt", "n"u8.ToArray()),
        });
        var rev = await wc.CommitAsync(path, "apaga pasta antiga");

        Assert.Equal(head + 1, rev);
        var xml = System.Xml.Linq.XDocument.Parse(await repo.SvnAsync("list", "-R", "--xml", repo.RepoUrl + "/trunk"));
        var list = xml.Descendants("entry").Select(e => e.Element("name")!.Value).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { "novo.txt" }, list);
    }

    [Fact]
    public async Task Apagar_pasta_inteira_preserva_arquivos_novos_do_mesmo_commit()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("src/styles/a.css", "a"u8.ToArray(), "a");
        await repo.CommitFileAsync("src/styles/b.css", "b"u8.ToArray(), "b");
        var head = await repo.CommitFileAsync("src/outro.txt", "o"u8.ToArray(), "o");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        // reestruturacao: a pasta perde todos os arquivos versionados e ganha arquivos novos no mesmo commit
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head, new[]
        {
            new ImportFile("src/styles/a.css", null),
            new ImportFile("src/styles/b.css", null),
            new ImportFile("src/styles/new.css", "n"u8.ToArray()),
            new ImportFile("src/styles/themes/x.css", "x"u8.ToArray()),
        });
        var rev = await wc.CommitAsync(path, "reestrutura estilos");

        Assert.Equal(head + 1, rev);
        var xml = System.Xml.Linq.XDocument.Parse(await repo.SvnAsync("list", "-R", "--xml", repo.RepoUrl + "/trunk"));
        var list = xml.Descendants("entry")
            .Select(e => e.Element("name")!.Value + ((string?)e.Attribute("kind") == "dir" ? "/" : ""))
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { "src/", "src/outro.txt", "src/styles/", "src/styles/new.css", "src/styles/themes/", "src/styles/themes/x.css" }, list);
        Assert.Equal("n", await repo.CatAsync(repo.RepoUrl + "/trunk/src/styles/new.css"));
    }

    [Fact]
    public async Task Renomear_todos_os_arquivos_da_pasta_no_mesmo_commit()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("dir/x.txt", "x"u8.ToArray(), "x");
        var head = await repo.CommitFileAsync("fora.txt", "f"u8.ToArray(), "f");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);

        // renomeacao sem deteccao de rename (o push usa --no-renames): sai x, entra y na mesma pasta
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head, new[]
        {
            new ImportFile("dir/x.txt", null),
            new ImportFile("dir/y.txt", "y"u8.ToArray()),
        });
        var rev = await wc.CommitAsync(path, "renomeia x para y");

        Assert.Equal(head + 1, rev);
        var xml = System.Xml.Linq.XDocument.Parse(await repo.SvnAsync("list", "-R", "--xml", repo.RepoUrl + "/trunk"));
        var list = xml.Descendants("entry")
            .Select(e => e.Element("name")!.Value + ((string?)e.Attribute("kind") == "dir" ? "/" : ""))
            .OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { "dir/", "dir/y.txt", "fora.txt" }, list);
        Assert.Equal("y", await repo.CatAsync(repo.RepoUrl + "/trunk/dir/y.txt"));
    }

    [Fact]
    public async Task ApplyTree_atualiza_base_de_arquivo_que_mudou_no_svn()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var head = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head,
            new[] { new ImportFile("a.txt", "2"u8.ToArray()) });
        await wc.CommitAsync(path, "muda a");

        // outra pessoa muda o mesmo arquivo: a WC rasa continua com a base antiga do ultimo push
        var externo = await repo.CommitFileToAsync("trunk", "a.txt", "3"u8.ToArray(), "outro muda a");
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", externo,
            new[] { new ImportFile("a.txt", "4"u8.ToArray()) });
        var rev = await wc.CommitAsync(path, "muda a de novo");

        Assert.Equal(externo + 1, rev);
        Assert.Equal("4", await repo.CatAsync(repo.RepoUrl + "/trunk/a.txt"));
    }

    [Fact]
    public async Task Commit_retorna_numero_da_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var head = await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "a");
        var wc = new SvnWorkingCopy(new SvnClient(), NewDir());
        var path = await wc.EnsureAsync(repo.RepoUrl + "/trunk", head);
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head,
            new[] { new ImportFile("a.txt", "2"u8.ToArray()) });

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
        await wc.ApplyTreeAsync(path, repo.RepoUrl + "/trunk", head,
            new[] { new ImportFile("a.txt", "2"u8.ToArray()), new ImportFile("n.txt", "n"u8.ToArray()) });
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
