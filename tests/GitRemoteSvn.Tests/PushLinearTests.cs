using System.Text;
using System.Xml.Linq;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Tests.Fixtures;
using Xunit;

namespace GitRemoteSvn.Tests;

public class PushLinearTests : IDisposable
{
    readonly List<string> _dirs = new();

    string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    async Task<string> CloneAsync(SvnTestRepo repo)
    {
        var dir = NewDir();
        var dest = Path.Combine(dir, "dest");
        var clone = await HelperBinary.GitAsync(dir, "clone", "svn::" + repo.RepoUrl, dest);
        Assert.True(clone.Code == 0, clone.StdErr);
        await HelperBinary.GitAsync(dest, "config", "user.name", "Dev Git");
        await HelperBinary.GitAsync(dest, "config", "user.email", "dev@exemplo.com");
        return dest;
    }

    internal static async Task CommitAsync(string clone, string rel, byte[] content, string message)
    {
        var full = SvnTestRepo.LongPath(Path.Combine(clone, rel.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
        Assert.Equal(0, (await HelperBinary.GitAsync(clone, "add", "-A")).Code);
        var c = await HelperBinary.GitAsync(clone, "commit", "-q", "-m", message);
        Assert.True(c.Code == 0, c.StdErr);
    }

    static async Task<string[]> SvnMessagesAsync(SvnTestRepo repo, string path = "trunk")
    {
        var xml = XDocument.Parse(await repo.SvnAsync("log", "--xml", repo.RepoUrl + "/" + path));
        return xml.Descendants("logentry").Select(e => e.Element("msg")?.Value ?? "").Reverse().ToArray();
    }

    [Fact]
    public async Task Push_de_3_commits_cria_3_revisoes_com_mensagens()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var dest = await CloneAsync(repo);
        await CommitAsync(dest, "a.txt", "2"u8.ToArray(), "um");
        await CommitAsync(dest, "b.txt", "b"u8.ToArray(), "dois\n\ncorpo da mensagem");
        await CommitAsync(dest, "a.txt", "3"u8.ToArray(), "três");
        var antes = await repo.HeadRevisionAsync();

        var push = await HelperBinary.GitAsync(dest, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal(antes + 3, await repo.HeadRevisionAsync());
        Assert.Equal(new[] { "layout", "base", "um", "dois\n\ncorpo da mensagem", "três" }, await SvnMessagesAsync(repo));
    }

    [Fact]
    public async Task Conteudo_no_svn_fica_igual_ao_git()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        await repo.CommitFileAsync("velho/x.txt", "x"u8.ToArray(), "pasta que vai sumir");
        var dest = await CloneAsync(repo);
        var longo = string.Join('/', Enumerable.Range(0, 12).Select(i => $"pasta-bem-comprida-{i:00}")) + "/fundo.txt";
        await CommitAsync(dest, "bin.dat", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), "binario");
        await CommitAsync(dest, "crlf.txt", "l1\r\nl2\r\n"u8.ToArray(), "crlf");
        await CommitAsync(dest, "pasta com espaço/ação.txt", Encoding.UTF8.GetBytes("acentuado"), "acento");
        await CommitAsync(dest, longo, "fundo"u8.ToArray(), "caminho longo");
        Assert.Equal(0, (await HelperBinary.GitAsync(dest, "mv", "a.txt", "renomeado.txt")).Code);
        Assert.Equal(0, (await HelperBinary.GitAsync(dest, "rm", "-q", "velho/x.txt")).Code);
        Assert.Equal(0, (await HelperBinary.GitAsync(dest, "commit", "-q", "-m", "renomeia e apaga")).Code);

        var push = await HelperBinary.GitAsync(dest, "push", "origin", "main");
        Assert.True(push.Code == 0, push.StdErr);

        var export = Path.Combine(NewDir(), "exp");
        await repo.SvnAsync("export", "-q", repo.RepoUrl + "/trunk", export);
        var gitFiles = (await HelperBinary.GitAsync(dest, "-c", "core.quotepath=false", "ls-files", "-z")).StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var svnFiles = Directory.EnumerateFiles(SvnTestRepo.LongPath(export), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(SvnTestRepo.LongPath(export), f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(gitFiles, svnFiles);
        foreach (var f in gitFiles)
        {
            var git = await File.ReadAllBytesAsync(SvnTestRepo.LongPath(Path.Combine(dest, f.Replace('/', Path.DirectorySeparatorChar))));
            var svn = await File.ReadAllBytesAsync(SvnTestRepo.LongPath(Path.Combine(export, f.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(git.SequenceEqual(svn), f);
        }
        Assert.False(Directory.Exists(Path.Combine(export, "velho")));
    }

    [Fact]
    public async Task Push_atualiza_branch_local_para_commits_com_svn_id()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var dest = await CloneAsync(repo);
        await CommitAsync(dest, "a.txt", "2"u8.ToArray(), "um");
        await CommitAsync(dest, "a.txt", "3"u8.ToArray(), "dois");

        var push = await HelperBinary.GitAsync(dest, "push", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        var bodies = (await HelperBinary.GitAsync(dest, "log", "-3", "--format=%B%x00", "main")).StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, bodies.Length);
        Assert.All(bodies, b => Assert.Contains("svn-id: ", b));
        Assert.Equal("", (await HelperBinary.GitAsync(dest, "status", "--porcelain")).StdOut);
        var main = (await HelperBinary.GitAsync(dest, "rev-parse", "main")).StdOut;
        Assert.Equal(main, (await HelperBinary.GitAsync(dest, "rev-parse", "refs/remotes/origin/main")).StdOut);
        Assert.Equal("Dev Git <dev@exemplo.com>", (await HelperBinary.GitAsync(dest, "log", "-1", "--format=%an <%ae>", "main")).StdOut.Trim());

        // um segundo push nao tem o que enviar
        var again = await HelperBinary.GitAsync(dest, "push", "origin", "main");
        Assert.True(again.Code == 0, again.StdErr);
        Assert.Equal(new[] { "layout", "base", "um", "dois" }, await SvnMessagesAsync(repo));
    }

    [Fact]
    public async Task Push_vazio_nao_faz_nada()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var dest = await CloneAsync(repo);
        var git = new GitClient(dest);
        var svn = new SvnClient();
        var state = new StateStore(git.GitDir);
        var pusher = new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(git.GitDir, "svn", "wc")), state);
        var antes = await repo.HeadRevisionAsync();

        var result = await pusher.PushBranchAsync("refs/heads/main", repo.RepoUrl + "/trunk", "refs/svn/origin/heads/main", dryRun: false);

        Assert.Equal(0, result.RevisionsCreated);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
    }

    [Fact]
    public async Task Dry_run_nao_escreve_no_servidor()
    {
        await using var repo = await SvnTestRepo.CreateAsync();
        await repo.CommitFileAsync("a.txt", "1"u8.ToArray(), "base");
        var dest = await CloneAsync(repo);
        await CommitAsync(dest, "a.txt", "2"u8.ToArray(), "um");
        var local = (await HelperBinary.GitAsync(dest, "rev-parse", "main")).StdOut;
        var antes = await repo.HeadRevisionAsync();

        var push = await HelperBinary.GitAsync(dest, "push", "--dry-run", "origin", "main");

        Assert.True(push.Code == 0, push.StdErr);
        Assert.Equal(antes, await repo.HeadRevisionAsync());
        Assert.Equal(local, (await HelperBinary.GitAsync(dest, "rev-parse", "main")).StdOut);
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            SvnTestRepo.DeleteTree(d);
    }
}
