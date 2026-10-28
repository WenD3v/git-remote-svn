using GitRemoteSvn.Git;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class RevisionImporterTests : IDisposable
{
    readonly List<string> _dirs = new();

    async Task<(GitClient Git, RevisionImporter Importer, ProjectLayout Layout)> SetupAsync(SvnTestRepo repo)
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-imp-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var gitPath = Path.Combine(dir, "repo");
        await SvnTestRepo.RunAsync("git", dir, "init", "-b", "main", gitPath);
        var git = new GitClient(gitPath);
        var state = new StateStore(git.GitDir);
        var layout = await ProjectLayout.DiscoverAsync(new SvnClient(), repo.RepoUrl);
        return (git, new RevisionImporter(new SvnClient(), git, state, AuthorMap.Load(null)), layout);
    }

    [Fact]
    public async Task Clone_importa_todas_as_revisoes_com_metadados()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await repo.CommitFileAsync("a.txt", "2"u8.ToArray(), "segundo");
        await repo.CommitFileAsync("b.txt", "3"u8.ToArray(), "terceiro");
        var (git, importer, layout) = await SetupAsync(repo);

        var n = await importer.ImportTrunkAsync(layout);

        Assert.Equal(3, n);
        Assert.Equal("3", (await git.RunAsync("rev-list", "--count", "main")).Trim());
        var log = await git.RunAsync("log", "--format=%an|%ae|%s", "-1");
        Assert.Equal("alice|alice@svn.local|terceiro", log.Trim());
        var body = await git.RunAsync("log", "--format=%B", "-1");
        Assert.Contains($"svn-id: {layout.TrunkUrl}@4 ", body);
        Assert.True(await git.RefExistsAsync("refs/remotes/origin/main") is false);
        Assert.Equal("false", await git.ConfigGetAsync("core.autocrlf"));
        Assert.Equal("true", await git.ConfigGetAsync("core.longpaths"));
        Assert.Equal("true", await git.ConfigGetAsync("branch.main.rebase"));
    }

    [Fact]
    public async Task Clone_preserva_binario_crlf_e_nomes_dificeis()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var bin = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var crlf = "linha1\r\nlinha2\r\n"u8.ToArray();
        await repo.CommitFileAsync("bin.dat", bin, "binario");
        await repo.CommitFileAsync("crlf.txt", crlf, "crlf");
        await repo.CommitFileAsync("pasta com espaço/ação.txt", "acento"u8.ToArray(), "unicode");
        var aninhado = string.Join('/', Enumerable.Repeat("d123456789", 25)) + "/f.txt";
        Assert.True(aninhado.Length >= 280);
        await repo.CommitFileAsync(aninhado, "fundo"u8.ToArray(), "aninhado");
        var (git, importer, layout) = await SetupAsync(repo);

        await importer.ImportTrunkAsync(layout);

        Assert.Equal(bin, await ReadShowBytesAsync(git, "HEAD:bin.dat"));
        Assert.Equal(crlf, await ReadShowBytesAsync(git, "HEAD:crlf.txt"));
        Assert.Equal("acento"u8.ToArray(), await ReadShowBytesAsync(git, "HEAD:pasta com espaço/ação.txt"));
        Assert.Equal("fundo"u8.ToArray(), await ReadShowBytesAsync(git, "HEAD:" + aninhado));
        var exported = await repo.CatAsync(repo.RepoUrl + "/trunk/bin.dat");
        Assert.Equal(bin.Length, exported.Length);
    }

    static async Task<byte[]> ReadShowBytesAsync(GitClient git, string spec)
    {
        var (code, bytes, err) = await Util.Proc.RunBytesAsync("git", git.RepoPath, new[] { "show", spec });
        Assert.True(code == 0, err);
        return bytes;
    }

    [Fact]
    public async Task Clone_ignora_caminhos_de_outros_projetos_na_mesma_revisao()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        var rootWc = Path.Combine(repo.RootPath, "rootwc");
        await repo.SvnAsync("checkout", "-q", repo.RepoUrl, rootWc);
        await File.WriteAllTextAsync(Path.Combine(rootWc, "trunk", "a.txt"), "2");
        Directory.CreateDirectory(Path.Combine(rootWc, "branches", "outro"));
        await File.WriteAllTextAsync(Path.Combine(rootWc, "branches", "outro", "x.txt"), "fora");
        await SvnTestRepo.RunAsync("svn", rootWc, "add", Path.Combine(rootWc, "branches", "outro"));
        await SvnTestRepo.RunAsync("svn", rootWc, "commit", "-m", "misto", rootWc);
        var (git, importer, layout) = await SetupAsync(repo);

        await importer.ImportTrunkAsync(layout);

        var files = await git.RunAsync("ls-tree", "-r", "--name-only", "main");
        Assert.Equal("a.txt", files.Trim());
        var log = await git.RunAsync("log", "--format=%s", "-1");
        Assert.Equal("misto", log.Trim());
    }

    [Fact]
    public async Task Clone_aplica_svn_ignore_sem_criar_gitignore()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await SvnTestRepo.RunAsync("svn", repo.RootPath, "propset", "svn:ignore", "*.log\nbuild/", repo.WcPath);
        await repo.CommitAsync("ignore");
        var (git, importer, layout) = await SetupAsync(repo);

        await importer.ImportTrunkAsync(layout);

        var exclude = await File.ReadAllTextAsync(Path.Combine(git.GitDir, "info", "exclude"));
        Assert.Contains("*.log", exclude);
        var files = await git.RunAsync("ls-tree", "-r", "--name-only", "main");
        Assert.DoesNotContain(".gitignore", files);
    }

    [Fact]
    public async Task Clone_nao_cria_commit_para_pasta_vazia_e_e_idempotente()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "primeiro");
        await repo.SvnAsync("mkdir", "-m", "pasta vazia", repo.RepoUrl + "/trunk/pasta");
        var (git, importer, layout) = await SetupAsync(repo);

        var n = await importer.ImportTrunkAsync(layout);

        Assert.Equal(1, n);
        Assert.Equal("1", (await git.RunAsync("rev-list", "--count", "main")).Trim());
        Assert.Equal(0, await importer.ImportTrunkAsync(layout));
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
