using System.Runtime.InteropServices;
using System.Text;
using GitRemoteSvn.Git;
using GitRemoteSvn.Helper;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;
using GitRemoteSvn.Update;

// Git invoca: git-remote-svn <remote> <url>  (o prefixo "svn::" ja vem removido; aceito por garantia)
if (args.Length == 0)
{
    Console.Error.WriteLine("git-remote-svn: uso: git clone svn::<url-do-svn>");
    return 1;
}

var exePath = Environment.ProcessPath ?? "git-remote-svn";
// sobra da troca de executavel de uma atualizacao anterior (Windows nao permite apagar o exe em uso)
UpdateService.CleanupLeftovers(exePath);

if (args is ["--version"] or ["version"])
{
    Console.WriteLine($"git-remote-svn {VersionUtil.Current} ({RuntimeInformation.RuntimeIdentifier})");
    return 0;
}

if (args[0] == "update") return await RunUpdateAsync(args);

var url = args[^1];
if (url.StartsWith("svn::", StringComparison.Ordinal)) url = url.Substring(5);
url = url.TrimEnd('/');
var remote = args.Length >= 2 && !args[0].Contains("://") ? args[0] : "origin";

var utf8 = new UTF8Encoding(false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };

try
{
    var git = new GitClient(Environment.CurrentDirectory);
    // um helper por repositorio: fetch automatico do editor + push do usuario disputariam estado e refs
    using var sessionLock = AcquireLock(Path.Combine(git.GitDir, "svn"));
    if (sessionLock is null)
    {
        Console.Error.WriteLine("git-remote-svn: outro git-remote-svn esta rodando neste repositorio (ex.: fetch automatico do editor); tente de novo em instantes");
        return 1;
    }
    var authorsFile = Path.Combine(git.GitDir, "svn", "autores.txt");
    var helper = new RemoteHelper(new SvnClient(), AuthorMap.Load(File.Exists(authorsFile) ? authorsFile : null), git, remote);
    await helper.RunAsync(url, input, output);
    // depois do protocolo, nunca no meio dele: uma linha no stderr, sem mexer no resultado do fetch/push
    await MaybeNotifyUpdateAsync(git);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"git-remote-svn: {ex.Message}");
    return 1;
}

/// <summary><c>git-remote-svn update [--check] [--force] [--quiet]</c>.</summary>
static async Task<int> RunUpdateAsync(string[] args)
{
    var options = UpdateOptions.FromEnvironment();
    if (args.Contains("--quiet")) options = options with { Quiet = true };
    var check = args.Contains("--check");
    var force = args.Contains("--force");

    var exe = Environment.ProcessPath ?? "git-remote-svn";
    var cache = new UpdateCache(CachePath());
    // o --check respeita o cache do dia: consultar o GitHub a cada chamada estouraria o limite por IP
    if (check && !force && cache.Read() is { } entry && cache.IsFresh(entry, UpdateNotice.Ttl))
    {
        Console.WriteLine(entry.LatestVersion is null
            ? $"git-remote-svn: ja esta na ultima versao (v{VersionUtil.Current})"
            : $"git-remote-svn: nova versao v{entry.LatestVersion} disponivel (git-remote-svn update)");
        return 0;
    }

    using var http = new HttpClient();
    var svc = new UpdateService(options, http, exe);
    var release = await svc.CheckAsync();
    if (release is null)
    {
        Console.Error.WriteLine("git-remote-svn: nao foi possivel consultar atualizacoes (rede, limite do GitHub ou release sem tag)");
        return check ? 0 : 1;
    }

    var newer = VersionUtil.IsNewer(release.Version, VersionUtil.Current);
    cache.Write(new UpdateCacheEntry(DateTimeOffset.UtcNow, newer ? release.Version : null, null));
    if (!newer && !force)
    {
        Console.WriteLine($"git-remote-svn: ja esta na ultima versao (v{VersionUtil.Current})");
        return 0;
    }

    if (check)
    {
        Console.WriteLine($"git-remote-svn: nova versao v{release.Version} disponivel (git-remote-svn update)");
        return 0;
    }

    var result = await svc.UpdateAsync(release);
    if (!result.Updated)
    {
        Console.Error.WriteLine($"git-remote-svn: {result.Message}");
        return 1;
    }
    Console.WriteLine($"git-remote-svn: atualizado para v{release.Version} (novas sessoes usam a versao nova)");
    return 0;
}

static string CachePath() =>
    Environment.GetEnvironmentVariable("GIT_REMOTE_SVN_UPDATE_CACHE") is { Length: > 0 } p ? p : UpdateCache.DefaultPath;

/// <summary>Checagem diaria no fim da sessao: aviso de uma linha ou auto-instalacao, sempre silenciosa em falha.</summary>
static async Task MaybeNotifyUpdateAsync(GitClient git)
{
    try
    {
        var exe = Environment.ProcessPath;
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return;
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var mensagem = await UpdateNotice.CheckAndApplyAsync(UpdateOptions.FromEnvironment(), http, exe,
            new UpdateCache(CachePath()), async () =>
                UpdateOptions.AutoUpdateRequested() ||
                (await git.RunAllowAsync("config", "--global", "--get", "remote-svn.autoUpdate")).StdOut
                    .Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            timeout.Token);
        if (mensagem is not null) await Console.Error.WriteLineAsync(mensagem);
    }
    catch (Exception)
    {
        // uma checagem de atualizacao nunca derruba nem suja a sessao
    }
}

static FileStream? AcquireLock(string dir)
{
    Directory.CreateDirectory(dir);
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            return new FileStream(Path.Combine(dir, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (attempt < 5)
        {
            Thread.Sleep(1000);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
