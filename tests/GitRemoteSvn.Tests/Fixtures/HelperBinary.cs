namespace GitRemoteSvn.Tests.Fixtures;

/// <summary>Publica o git-remote-svn uma vez por execucao dos testes e expoe o diretorio para o PATH.</summary>
public static class HelperBinary
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static string? _dir;

    public static async Task<string> GetDirAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_dir is not null) return _dir;
            var dir = Path.Combine(Path.GetTempPath(), "grs-bin-" + Guid.NewGuid().ToString("N"));
            var project = Path.Combine(FindRepoRoot(), "src", "GitRemoteSvn", "GitRemoteSvn.csproj");
            await SvnTestRepo.RunAsync("dotnet", Path.GetTempPath(), "publish", project, "-c", "Release", "-o", dir, "--nologo");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SvnTestRepo.DeleteTree(dir);
            _dir = dir;
            return dir;
        }
        finally { Gate.Release(); }
    }

    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "git-remote-svn.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("raiz do repositorio nao encontrada");
    }

    /// <summary>Roda git com o helper publicado na frente do PATH.</summary>
    /// <param name="env">Variaveis extras; a checagem de atualizacao fica desligada por padrao.</param>
    public static async Task<(int Code, string StdOut, string StdErr)> GitAsync(string cwd, params string[] args) =>
        await GitWithEnvAsync(cwd, null, args);

    public static async Task<(int Code, string StdOut, string StdErr)> GitWithEnvAsync(string cwd,
        IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var dir = await GetDirAsync();
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        // os testes nunca consultam o GitHub nem escrevem no cache real do desenvolvedor
        psi.Environment["GIT_REMOTE_SVN_NO_UPDATE"] = "1";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }
}
