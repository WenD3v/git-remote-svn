using System.Text;
using GitRemoteSvn.Git;
using GitRemoteSvn.Helper;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;

// Git invoca: git-remote-svn <remote> <url>  (o prefixo "svn::" ja vem removido; aceito por garantia)
if (args.Length == 0)
{
    Console.Error.WriteLine("git-remote-svn: uso: git clone svn::<url-do-svn>");
    return 1;
}

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
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"git-remote-svn: {ex.Message}");
    return 1;
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
