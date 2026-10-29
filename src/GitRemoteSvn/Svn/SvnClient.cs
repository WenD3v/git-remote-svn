using System.Globalization;
using System.Xml.Linq;
using GitRemoteSvn.Util;

namespace GitRemoteSvn.Svn;

public class SvnClient(string svnExe = "svn")
{
    public string SvnExe { get; } = svnExe;

    /// <summary><c>svnrdump</c> ao lado do <c>svn</c> configurado, senao o do PATH.</summary>
    public string SvnrdumpExe { get; init; } = SiblingSvnrdump(svnExe);

    public const int MaxAttempts = 4;

    static string SiblingSvnrdump(string svnExe)
    {
        var dir = Path.GetDirectoryName(svnExe);
        if (!string.IsNullOrEmpty(dir))
            foreach (var name in new[] { "svnrdump.exe", "svnrdump" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return "svnrdump";
    }

    bool? _hasSvnrdump;

    public async Task<bool> HasSvnrdumpAsync()
    {
        if (_hasSvnrdump is null)
        {
            try { _hasSvnrdump = (await Proc.RunAsync(SvnrdumpExe, null, new[] { "--version", "--quiet" })).Code == 0; }
            catch (System.ComponentModel.Win32Exception) { _hasSvnrdump = false; }
        }
        return _hasSvnrdump.Value;
    }

    /// <summary>
    /// <c>svnrdump dump</c> de <paramref name="url"/> para <paramref name="file"/>: todas as revisoes num unico fluxo.
    /// Sem <paramref name="incremental"/>, a primeira revisao traz a arvore inteira.
    /// </summary>
    public async Task DumpAsync(string url, long startRev, long endRev, bool incremental, string file)
    {
        var args = new List<string> { "dump", "-q", "--non-interactive", "-r", $"{startRev}:{endRev}", "--file", file };
        if (incremental) args.Add("--incremental");
        args.Add(url);
        var (code, _, stderr) = await WithRetryAsync(() => Proc.RunAsync(SvnrdumpExe, null, args));
        if (code != 0) throw new SvnException($"svnrdump {string.Join(' ', args)}: {stderr.Trim()}");
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<string> RunAsync(IEnumerable<string> args)
    {
        var list = args.ToList();
        var (code, stdout, stderr) = SafeToRetry.Contains(list[0])
            ? await WithRetryAsync(() => Proc.RunAsync(SvnExe, null, list))
            : await Proc.RunAsync(SvnExe, null, list);
        if (code != 0) throw new SvnException($"svn {string.Join(' ', list)}: {stderr.Trim()}");
        return stdout;
    }

    // timeout, conexao recusada/fechada/reiniciada (codigos do svn, do Windows e do Linux)
    static readonly string[] TransientCodes =
        { "E175012", "E175002", "E120108", "E730053", "E730054", "E730060", "E730061", "E000104", "E000110", "E000111" };

    static bool IsTransient(string stderr) => TransientCodes.Any(stderr.Contains);

    // so leitura ou operacoes idempotentes: repetir commit/copy apos queda poderia duplicar uma revisao ja aceita
    static readonly HashSet<string> SafeToRetry = new(StringComparer.Ordinal)
        { "info", "list", "log", "cat", "propget", "export", "checkout", "update", "cleanup", "revert" };

    /// <summary>Repete a chamada quando a falha e de rede (instavel em clones longos); erros reais saem na hora.</summary>
    async Task<(int Code, T StdOut, string StdErr)> WithRetryAsync<T>(Func<Task<(int Code, T StdOut, string StdErr)>> run)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await run();
            if (result.Code == 0 || attempt >= MaxAttempts || !IsTransient(result.StdErr)) return result;
            await Task.Delay(RetryDelay * attempt);
        }
    }

    async Task<XDocument> XmlAsync(IEnumerable<string> args)
    {
        var xml = await RunAsync(args);
        return XDocument.Parse(xml);
    }

    public async Task<SvnInfo> InfoAsync(string url)
    {
        var doc = await XmlAsync(new[] { "info", "--xml", url });
        var entry = doc.Root!.Element("entry")!;
        var repo = entry.Element("repository")!;
        return new SvnInfo(
            entry.Element("url")!.Value.Trim(),
            repo.Element("root")!.Value.Trim(),
            repo.Element("uuid")!.Value.Trim(),
            (long)entry.Attribute("revision")!,
            (long?)entry.Element("commit")?.Attribute("revision") ?? (long)entry.Attribute("revision")!);
    }

    /// <summary>Ultima revisao que alterou algo sob a URL.</summary>
    public async Task<long> LastChangedRevAsync(string url) => (await InfoAsync(url)).LastChangedRev;

    /// <summary>
    /// Arquivos versionados sob <paramref name="url"/> (caminho relativo com '/'), numa unica chamada:
    /// evita um <c>svn cat</c>/<c>svn info</c> por arquivo ao montar o working copy de um push.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListFilesAsync(string url)
    {
        var doc = await XmlAsync(new[] { "list", "-R", "--xml", url });
        var root = Uri.UnescapeDataString(StripPeg(url).TrimEnd('/'));
        var result = new List<string>();
        foreach (var list in doc.Root!.Elements("list"))
        {
            var at = (string?)list.Attribute("path");
            var prefix = at is null ? "" : RelativeTo(root, Uri.UnescapeDataString(at));
            foreach (var entry in list.Elements("entry"))
            {
                if ((string?)entry.Attribute("kind") == "dir") continue;
                var name = entry.Element("name")!.Value;
                result.Add(prefix.Length == 0 ? name : prefix + "/" + name);
            }
        }
        return result;
    }

    static string StripPeg(string url)
    {
        var at = url.LastIndexOf('@');
        return at > url.LastIndexOf('/') && url[(at + 1)..].All(char.IsDigit) ? url[..at] : url;
    }

    static string RelativeTo(string root, string path) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal)
            ? path[root.Length..].Trim('/')
            : "";

    public async Task<IReadOnlyList<SvnEntry>> ListAsync(string url)
    {
        var doc = await XmlAsync(new[] { "list", "--xml", url });
        return doc.Root!.Elements("list").SelectMany(l => l.Elements("entry"))
            .Select(e => new SvnEntry(
                e.Element("name")!.Value,
                (string)e.Attribute("kind")!,
                (long?)(e.Element("commit")?.Attribute("revision")) ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<SvnRevision>> LogAsync(string url, long startRev, long endRev, bool stopOnCopy,
        int? limit = null)
    {
        var args = new List<string> { "log", "--xml", "-v", "-r", $"{startRev}:{endRev}" };
        if (stopOnCopy) args.Add("--stop-on-copy");
        if (limit is not null) args.AddRange(new[] { "--limit", limit.Value.ToString() });
        args.Add(url);
        var doc = await XmlAsync(args);
        return doc.Root!.Elements("logentry")
            .Select(e => new SvnRevision(
                (long)e.Attribute("revision")!,
                e.Element("author")?.Value ?? "",
                DateTimeOffset.Parse(e.Element("date")!.Value, CultureInfo.InvariantCulture),
                e.Element("msg")?.Value ?? "",
                e.Element("paths")?.Elements("path").Select(p => new SvnPathChange(
                    ((string?)p.Attribute("action") ?? "M")[0],
                    p.Value,
                    (string?)p.Attribute("copyfrom-path"),
                    (long?)p.Attribute("copyfrom-rev"),
                    (string?)p.Attribute("kind") ?? "file")).ToList()
                    ?? (IReadOnlyList<SvnPathChange>)Array.Empty<SvnPathChange>()))
            .OrderBy(r => r.Revision)
            .ToList();
    }

    /// <summary><c>svn copy --parents</c> no servidor; devolve a revisao criada.</summary>
    public async Task<long> CopyAsync(string srcUrlAtRev, string dstUrl, string message)
    {
        var msgFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(msgFile, message, new System.Text.UTF8Encoding(false));
            await RunAsync(new[] { "copy", "--parents", "--non-interactive", "-F", msgFile, "--encoding", "UTF-8", srcUrlAtRev, dstUrl });
        }
        finally { File.Delete(msgFile); }
        return await LastChangedRevAsync(dstUrl);
    }

    /// <summary>URL com cada segmento do caminho escapado (preserva as barras).</summary>
    public static string JoinUrl(string baseUrl, string relativePath) =>
        baseUrl.TrimEnd('/') + "/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    public async Task<string?> PropGetAsync(string name, string url)
    {
        var (code, stdout, stderr) = await WithRetryAsync(() => Proc.RunAsync(SvnExe, null, new[] { "propget", name, url }));
        if (code != 0 && IsTransient(stderr)) throw new SvnException($"svn propget {name} {url}: {stderr.Trim()}");
        return code == 0 ? stdout : null;
    }

    /// <summary>Propriedade em <paramref name="url"/> e em todas as pastas abaixo: (caminho relativo com '/', valor).</summary>
    public async Task<IReadOnlyList<(string Path, string Value)>> PropGetRecursiveAsync(string name, string url)
    {
        var (code, stdout, stderr) = await WithRetryAsync(() =>
            Proc.RunAsync(SvnExe, null, new[] { "propget", "-R", "--xml", name, url }));
        if (code != 0)
        {
            if (IsTransient(stderr)) throw new SvnException($"svn propget -R {name} {url}: {stderr.Trim()}");
            return Array.Empty<(string, string)>();
        }
        var peg = url.LastIndexOf('@');
        var bare = peg > url.LastIndexOf('/') && url[(peg + 1)..].All(char.IsDigit) ? url[..peg] : url;
        var root = Uri.UnescapeDataString(bare.TrimEnd('/'));
        return XDocument.Parse(stdout).Root!.Elements("target")
            .Select(t => (Path: Uri.UnescapeDataString(((string)t.Attribute("path")!).TrimEnd('/')),
                Value: t.Elements("property").FirstOrDefault(p => (string?)p.Attribute("name") == name)?.Value ?? ""))
            .Where(t => t.Path.StartsWith(root, StringComparison.Ordinal))
            .Select(t => (t.Path.Substring(root.Length).TrimStart('/'), t.Value))
            .ToList();
    }

    public async Task<byte[]> CatAsync(string urlAtRev)
    {
        var (code, bytes, stderr) = await WithRetryAsync(() => Proc.RunBytesAsync(SvnExe, null, new[] { "cat", urlAtRev }));
        if (code != 0) throw new SvnException($"svn cat {urlAtRev}: {stderr.Trim()}");
        return bytes;
    }

    /// <summary>
    /// Arquivos sob <paramref name="urlAtRev"/> (caminho relativo com '/', conteudo) via um unico <c>svn export</c> -
    /// muito mais rapido que um <c>svn cat</c> por arquivo ao materializar pastas copiadas grandes.
    /// </summary>
    public async Task<IReadOnlyList<(string Path, byte[] Content)>> ExportFilesAsync(string urlAtRev)
    {
        var dir = Path.Combine(Path.GetTempPath(), "grs-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            // mesma traducao de svn:eol-style/svn:keywords que o svn cat usa no resto do historico
            await RunAsync(new[] { "export", "-q", "--non-interactive", "--ignore-externals", urlAtRev, dir });
            var root = LongPath(dir);
            var result = new List<(string, byte[])>();
            if (File.Exists(root)) // a URL era um arquivo
                return result;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                result.Add((Path.GetRelativePath(root, f).Replace('\\', '/'), await File.ReadAllBytesAsync(f)));
            return result;
        }
        finally
        {
            try
            {
                var info = new DirectoryInfo(LongPath(dir));
                if (info.Exists)
                {
                    foreach (var f in info.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
                    info.Delete(true);
                }
            }
            catch (IOException) { /* temporario */ }
        }
    }

    static string LongPath(string p) =>
        OperatingSystem.IsWindows() && !p.StartsWith(@"\\?\") ? @"\\?\" + p : p;
}
