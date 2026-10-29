using System.Diagnostics;
using GitRemoteSvn.Util;

namespace GitRemoteSvn.Git;

public class GitClient(string repoPath, string gitExe = "git")
{
    string? _gitDir;

    public string RepoPath { get; } = repoPath;
    public string GitExe { get; } = gitExe;

    /// <summary>
    /// Diretorio comum do repositorio (o mesmo para todos os worktrees): estado do helper, refs e
    /// <c>info/exclude</c> precisam ser um so, senao cada worktree reimportaria o historico.
    /// </summary>
    public string GitDir
    {
        get
        {
            if (_gitDir is null)
                _gitDir = Path.GetFullPath(RunAsync("rev-parse", "--path-format=absolute", "--git-common-dir")
                    .GetAwaiter().GetResult().Trim());
            return _gitDir;
        }
    }

    public async Task<string> RunAsync(params string[] args)
    {
        var (code, stdout, stderr) = await Proc.RunAsync(GitExe, RepoPath, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Trim()}");
        return stdout;
    }

    public Task<(int Code, string StdOut, string StdErr)> RunAllowAsync(params string[] args) =>
        Proc.RunAsync(GitExe, RepoPath, args);

    readonly Dictionary<string, string?> _config = new(StringComparer.Ordinal);

    /// <summary>
    /// Grava a configuracao local, sem derrubar a sessao: um <c>.git/config</c> somente leitura (ou travado por
    /// outro processo) nao pode abortar um fetch/push inteiro por causa de um ajuste de conveniencia.
    /// </summary>
    public async Task ConfigSetAsync(string key, string value)
    {
        if (_config.TryGetValue(key, out var cached) && cached == value) return;
        var (getCode, stdout, _) = await RunAllowAsync("config", "--local", "--get", key);
        if (getCode == 0 && stdout.Trim() == value) { _config[key] = value; return; }
        var (code, _, stderr) = await RunAllowAsync("config", "--local", key, value);
        if (code != 0)
        {
            Console.Error.WriteLine($"git-remote-svn: aviso: nao foi possivel configurar {key}: {stderr.Trim()}");
            return;
        }
        _config[key] = value;
    }

    public async Task<string?> ConfigGetAsync(string key)
    {
        var (code, stdout, _) = await RunAllowAsync("config", "--get", key);
        return code == 0 ? stdout.Trim() : null;
    }

    public async Task<string> RevParseAsync(string refName) => (await RunAsync("rev-parse", refName)).Trim();

    public async Task<bool> RefExistsAsync(string refName)
    {
        var (code, _, _) = await RunAllowAsync("show-ref", "--verify", "--quiet", refName);
        return code == 0;
    }

    public Task UpdateRefAsync(string refName, string sha) => RunAsync("update-ref", refName, sha);

    public async Task<byte[]> CatBlobAsync(string sha)
    {
        var (code, bytes, stderr) = await Proc.RunBytesAsync(GitExe, RepoPath, new[] { "cat-file", "blob", sha });
        if (code != 0) throw new InvalidOperationException($"git cat-file blob {sha}: {stderr.Trim()}");
        return bytes;
    }

    /// <summary>Roda git com variaveis de ambiente extras (ex.: autor/committer do commit-tree).</summary>
    public async Task<string> RunWithEnvAsync(IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var (code, stdout, stderr) = await Proc.RunAsync(GitExe, RepoPath, args, env: env);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Trim()}");
        return stdout;
    }

    public Task<FastImportWriter> BeginFastImportAsync() =>
        Task.FromResult(new FastImportWriter(this));
}

internal static class FastImportProcess
{
    public static Process Start(string gitExe, string repoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitExe,
            WorkingDirectory = repoPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("fast-import");
        psi.ArgumentList.Add("--quiet");
        return Process.Start(psi)!;
    }
}
