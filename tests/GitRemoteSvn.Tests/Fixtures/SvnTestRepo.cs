using System.Diagnostics;
using System.Text;

namespace GitRemoteSvn.Tests.Fixtures;

/// <summary>Repositorio SVN local e descartavel para os testes (file://).</summary>
public sealed class SvnTestRepo : IAsyncDisposable
{
    public string RootPath { get; }
    public string RepoPath { get; }
    public string RepoUrl { get; }
    public string WcPath { get; }

    SvnTestRepo(string root)
    {
        RootPath = root;
        RepoPath = Path.Combine(root, "repo");
        RepoUrl = "file:///" + RepoPath.Replace('\\', '/');
        WcPath = Path.Combine(root, "wc");
    }

    public static async Task<SvnTestRepo> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "grs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new SvnTestRepo(root);
        Run("svnadmin", root, "create", repo.RepoPath);
        await RunAsync("svn", root, "mkdir", "-m", "layout",
            repo.RepoUrl + "/trunk", repo.RepoUrl + "/branches", repo.RepoUrl + "/tags");
        await RunAsync("svn", root, "checkout", "-q", repo.RepoUrl + "/trunk", repo.WcPath);
        return repo;
    }

    public Task<string> SvnAsync(params string[] args) => RunAsync("svn", RootPath, args);

    public Task<long> HeadRevisionAsync() =>
        RunAsync("svn", RootPath, "info", "--show-item", "revision", RepoUrl)
            .ContinueWith(t => long.Parse(t.Result.Trim()), TaskContinuationOptions.OnlyOnRanToCompletion);

    public async Task<long> CommitFileAsync(string relativePath, byte[] content, string message, string author = "alice")
    {
        var full = Path.Combine(WcPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(LongPath(Path.GetDirectoryName(full)!));
        await File.WriteAllBytesAsync(LongPath(full), content);
        var (addCode, _, addErr) = await RunCoreAsync("svn", RootPath, "add", "--parents", full);
        if (addCode != 0 && !addErr.Contains("already under version control"))
            throw new InvalidOperationException($"svn add {full}: {addErr}");
        await RunAsync("svn", RootPath, "commit", "-m", message, "--username", author, WcPath);
        return await HeadRevisionAsync();
    }

    public async Task<long> CommitAsync(string message)
    {
        await RunAsync("svn", RootPath, "update", "-q", WcPath);
        await RunAsync("svn", RootPath, "commit", "-m", message, WcPath);
        return await HeadRevisionAsync();
    }

    public async Task<long> CopyAsync(string sourceUrl, string destUrl, string message)
    {
        await RunAsync("svn", RootPath, "copy", "-m", message, sourceUrl, destUrl);
        return await HeadRevisionAsync();
    }

    /// <summary>Commita um arquivo num caminho qualquer do repositorio (ex.: "branches/b1"), via checkout proprio.</summary>
    public async Task<long> CommitFileToAsync(string repoPath, string relativePath, byte[] content, string message, string author = "bob")
    {
        var wc = Path.Combine(RootPath, "wc-" + Guid.NewGuid().ToString("N"));
        await RunAsync("svn", RootPath, "checkout", "-q", RepoUrl + "/" + repoPath, wc);
        var full = Path.Combine(wc, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
        var (addCode, _, addErr) = await RunCoreAsync("svn", RootPath, "add", "--parents", full);
        if (addCode != 0 && !addErr.Contains("already under version control"))
            throw new InvalidOperationException($"svn add {full}: {addErr}");
        await RunAsync("svn", RootPath, "commit", "-m", message, "--username", author, wc);
        return await HeadRevisionAsync();
    }

    public async Task<string> CatAsync(string url) => await RunAsync("svn", RootPath, "cat", url);

    public static async Task<string> RunAsync(string exe, string cwd, params string[] args)
    {
        var (code, stdout, stderr) = await RunCoreAsync(exe, cwd, args);
        if (code != 0) throw new InvalidOperationException($"{exe} {string.Join(' ', args)} falhou ({code}): {stderr}");
        return stdout;
    }

    public static void Run(string exe, string cwd, params string[] args) =>
        RunAsync(exe, cwd, args).GetAwaiter().GetResult();

    public static async Task<(int Code, string StdOut, string StdErr)> RunCoreAsync(string exe, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    public async ValueTask DisposeAsync()
    {
        DeleteTree(RootPath);
        await Task.CompletedTask;
    }

    /// <summary>Apaga pasta temporaria, inclusive objetos somente-leitura do Git.</summary>
    public static void DeleteTree(string path)
    {
        try
        {
            var root = new DirectoryInfo(LongPath(path));
            if (!root.Exists) return;
            foreach (var f in root.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
            root.Delete(true);
        }
        catch { /* temp */ }
    }

    public static string LongPath(string p) =>
        OperatingSystem.IsWindows() && !p.StartsWith(@"\\?\") ? @"\\?\" + p : p;
}
