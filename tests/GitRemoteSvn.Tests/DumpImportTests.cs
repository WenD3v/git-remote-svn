using System.Text;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

/// <summary>Importacao via <c>svnrdump</c>: um fluxo por ref em vez de um <c>svn cat</c> por arquivo.</summary>
public class DumpImportTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<(GitClient Git, RevisionImporter Importer, ProjectLayout Layout)> SetupAsync(SvnTestRepo repo, SvnClient? svn = null)
    {
        var gitPath = Path.Combine(NewDir("grs-dump-"), "repo");
        await SvnTestRepo.RunAsync("git", Path.GetDirectoryName(gitPath)!, "init", "-b", "main", gitPath);
        var git = new GitClient(gitPath);
        svn ??= new SvnClient();
        var layout = await ProjectLayout.DiscoverAsync(svn, repo.RepoUrl);
        return (git, new RevisionImporter(svn, git, new StateStore(git.GitDir), AuthorMap.Load(null)), layout);
    }

    static async Task<byte[]> ShowAsync(GitClient git, string spec)
    {
        var (code, bytes, err) = await Util.Proc.RunBytesAsync("git", git.RepoPath, new[] { "show", spec });
        Assert.True(code == 0, err);
        return bytes;
    }

    async Task<byte[]> ExportAsync(SvnTestRepo repo, string urlPath)
    {
        var target = Path.Combine(NewDir("grs-exp-"), "f");
        await repo.SvnAsync("export", "-q", repo.RepoUrl + "/" + urlPath, target);
        return await File.ReadAllBytesAsync(target);
    }

    static byte[] Texto(int linhas, string marca) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, linhas).Select(i => $"linha {i} {marca}\n")));

    [Fact]
    public async Task Dump_reconstroi_conteudo_de_cada_revisao_com_deltas()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var v1 = Texto(200, "a");
        var v2 = v1.Concat(Texto(5, "b")).ToArray();
        v2[10] = (byte)'X';
        var bin = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        await repo.CommitFileAsync("a.txt", v1, "um");
        await repo.CommitFileAsync("a.txt", v2, "dois");
        await repo.CommitFileAsync("bin.dat", bin, "tres");
        var svn = new SvnClient();
        var file = Path.Combine(NewDir("grs-dumpf-"), "d.dump");

        await svn.DumpAsync(repo.RepoUrl + "/trunk", 1, await repo.HeadRevisionAsync(), incremental: false, file);

        var files = new Dictionary<string, byte[]>();
        var revs = new List<long>();
        await using var stream = File.OpenRead(file);
        foreach (var rev in SvnDumpReader.Read(stream))
        {
            revs.Add(rev.Revision);
            foreach (var n in rev.Nodes.Where(n => n.Kind != "dir" && n.Text is not null))
            {
                var source = files.GetValueOrDefault(n.Path) ?? Array.Empty<byte>();
                files[n.Path] = n.TextDelta ? SvnDiff.Apply(source, n.Text!) : n.Text!;
            }
            if (rev.Revision == 2) Assert.Equal(v1, files["trunk/a.txt"]);
        }
        Assert.Equal(new long[] { 1, 2, 3, 4 }, revs);
        Assert.Equal(v2, files["trunk/a.txt"]);
        Assert.Equal(bin, files["trunk/bin.dat"]);
    }

    [Fact]
    public async Task Clone_nao_usa_svn_cat_nem_export_por_arquivo()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "um");
        await repo.CommitFileAsync("dir/b.txt", "2"u8.ToArray(), "dois");
        await repo.CommitFileAsync("a.txt", "3"u8.ToArray(), "tres");
        var fake = Path.Combine(NewDir("grs-fakesvn-"), "svn.cmd");
        await File.WriteAllTextAsync(fake,
            "@echo off\r\n" +
            "if \"%1\"==\"cat\" (echo svn cat proibido 1>&2 & exit /b 1)\r\n" +
            "if \"%1\"==\"export\" (echo svn export proibido 1>&2 & exit /b 1)\r\n" +
            "svn %*\r\n");
        var (git, importer, layout) = await SetupAsync(repo, new SvnClient(fake));

        Assert.Equal(3, await importer.ImportTrunkAsync(layout));

        Assert.Equal("3"u8.ToArray(), await ShowAsync(git, "main:a.txt"));
        Assert.Equal("2"u8.ToArray(), await ShowAsync(git, "main:dir/b.txt"));
        Assert.Equal("1"u8.ToArray(), await ShowAsync(git, "main~2:a.txt"));
    }

    [Fact]
    public async Task Eol_native_fica_igual_ao_svn_export_no_clone_e_no_fetch()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("n.txt", "a\nb\n"u8.ToArray(), "texto");
        await repo.SvnAsync("propset", "svn:eol-style", "native", Path.Combine(repo.WcPath, "n.txt"));
        await repo.CommitAsync("native");
        var (git, importer, layout) = await SetupAsync(repo);

        await importer.ImportTrunkAsync(layout);
        Assert.Equal(await ExportAsync(repo, "trunk/n.txt"), await ShowAsync(git, "main:n.txt"));

        await repo.CommitFileAsync("n.txt", "a\nb\nc\n"u8.ToArray(), "mais uma linha");
        await importer.ImportTrunkAsync(layout);
        Assert.Equal(await ExportAsync(repo, "trunk/n.txt"), await ShowAsync(git, "main:n.txt"));

        await repo.SvnAsync("propdel", "svn:eol-style", Path.Combine(repo.WcPath, "n.txt"));
        await repo.CommitAsync("sem eol-style");
        await importer.ImportTrunkAsync(layout);
        Assert.Equal(await ExportAsync(repo, "trunk/n.txt"), await ShowAsync(git, "main:n.txt"));
    }

    [Fact]
    public async Task Copia_interna_de_revisao_antiga_e_mv_de_pasta_no_fetch()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        var r1 = await repo.CommitFileAsync("a.txt", Texto(50, "um"), "um");
        await repo.CommitFileAsync("dir/x.txt", "x"u8.ToArray(), "pasta");
        var (git, importer, layout) = await SetupAsync(repo);
        await importer.ImportTrunkAsync(layout);

        await repo.CommitFileAsync("a.txt", Texto(50, "dois"), "dois");
        await repo.SvnAsync("copy", "-m", "restaura", $"{repo.RepoUrl}/trunk/a.txt@{r1}", repo.RepoUrl + "/trunk/b.txt");
        await repo.SvnAsync("move", "-m", "renomeia", repo.RepoUrl + "/trunk/dir", repo.RepoUrl + "/trunk/dir2");
        await importer.ImportTrunkAsync(layout);

        Assert.Equal(Texto(50, "um"), await ShowAsync(git, "main:b.txt"));
        Assert.Equal(Texto(50, "dois"), await ShowAsync(git, "main:a.txt"));
        Assert.Equal("x"u8.ToArray(), await ShowAsync(git, "main:dir2/x.txt"));
        var files = (await git.RunAsync("ls-tree", "-r", "--name-only", "main")).Trim().Split('\n');
        Assert.Equal(new[] { "a.txt", "b.txt", "dir2/x.txt" }, files);
    }

    [Fact]
    public async Task Svn_ignore_vem_do_dump_sem_propget_no_clone_e_no_fetch()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("sub/a.txt", "1"u8.ToArray(), "um");
        await repo.SvnAsync("propset", "svn:ignore", "*.log", repo.WcPath);
        await repo.CommitAsync("ignore na raiz");
        var fake = Path.Combine(NewDir("grs-fakesvn-"), "svn.cmd");
        await File.WriteAllTextAsync(fake,
            "@echo off\r\n" +
            "if \"%1\"==\"propget\" (echo svn propget proibido 1>&2 & exit /b 1)\r\n" +
            "svn %*\r\n");
        var (git, importer, layout) = await SetupAsync(repo, new SvnClient(fake));
        var exclude = Path.Combine(git.GitDir, "info", "exclude");

        await importer.ImportTrunkAsync(layout);
        Assert.Contains("*.log", (await File.ReadAllTextAsync(exclude)).Split('\n'));

        await repo.SvnAsync("propset", "svn:ignore", "bin", Path.Combine(repo.WcPath, "sub"));
        await repo.CommitAsync("ignore na subpasta");
        await importer.ImportTrunkAsync(layout);
        Assert.Contains("/sub/bin", (await File.ReadAllTextAsync(exclude)).Split('\n'));
        Assert.Equal(0, await importer.ImportTrunkAsync(layout));
    }

    [Fact]
    public async Task Sem_svnrdump_importa_pelo_caminho_antigo()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "um");
        await repo.CommitFileAsync("a.txt", "2"u8.ToArray(), "dois");
        var (git, importer, layout) = await SetupAsync(repo, new SvnClient { SvnrdumpExe = "svnrdump-que-nao-existe" });

        Assert.Equal(2, await importer.ImportTrunkAsync(layout));

        Assert.Equal("2"u8.ToArray(), await ShowAsync(git, "main:a.txt"));
    }

    public void Dispose()
    {
        foreach (var d in _dirs) SvnTestRepo.DeleteTree(d);
    }
}
