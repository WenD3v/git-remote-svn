using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

/// <summary>
/// Converte as revisoes de um dump do <c>svnrdump</c> de uma branch em alteracoes de commit. Mantem a arvore da
/// branch em memoria (caminho -> conteudo na forma do repositorio + <c>svn:eol-style</c>) e devolve, por revisao,
/// so os arquivos cujo conteudo no Git mudou. O conteudo no Git e o mesmo que o <c>svn export</c> entrega.
/// </summary>
sealed class DumpApplier(SvnClient svn, GitClient git, string basePath, string targetRef)
{
    /// <summary>Repo null: arquivo vindo da arvore Git de partida; conteudo e eol sao lidos sob demanda.</summary>
    sealed record Entry(string GitSha, byte[]? Repo, string? Eol, Origin? From);

    /// <summary>De onde veio uma entrada da arvore Git: url@rev e caminho relativo, para descobrir o svn:eol-style.</summary>
    sealed record Origin(string Url, long Rev, string Path);

    static readonly ImmutableDictionary<string, Entry> Empty =
        ImmutableDictionary<string, Entry>.Empty.WithComparers(StringComparer.Ordinal);

    ImmutableDictionary<string, Entry> _tree = Empty;
    readonly SortedList<long, ImmutableDictionary<string, Entry>> _history = new();
    readonly Dictionary<string, ImmutableDictionary<string, Entry>> _gitTrees = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _eols = new(StringComparer.Ordinal);

    /// <summary><c>svn:ignore</c> das pastas tocadas pelo dump (caminho relativo, "" = raiz; null = removido).</summary>
    public Dictionary<string, string?> Ignores { get; } = new(StringComparer.Ordinal);

    /// <summary>Dumps com copia vinda de fora da branch nao sao tratados aqui (o svnrdump as manda como adicao).</summary>
    public static bool Supported(IEnumerable<DumpRevision> revisions, string basePath) =>
        revisions.SelectMany(r => r.Nodes)
            .Where(n => Under(Abs(n.Path), basePath) is not null && n.CopyFromPath is not null)
            .All(n => Under(Abs(n.CopyFromPath!), basePath) is not null);

    /// <summary>Arvore de partida: o commit <paramref name="commitish"/>, que corresponde a <paramref name="url"/>@<paramref name="rev"/>.</summary>
    public async Task LoadBaselineAsync(string commitish, string url, long rev)
    {
        _tree = await GitTreeAsync(commitish, url, rev);
        _history[rev] = _tree;
    }

    public async Task<List<ImportFile>> ApplyAsync(DumpRevision rev)
    {
        var before = _tree;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in rev.Nodes) await ApplyNodeAsync(node, rev.Revision, touched);
        _history[rev.Revision] = _tree;

        var result = new List<ImportFile>();
        foreach (var path in touched.OrderBy(p => p, StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var old);
            if (!_tree.TryGetValue(path, out var now))
            {
                if (had) result.Add(new ImportFile(path, null));
            }
            else if (!had || old!.GitSha != now.GitSha)
            {
                result.Add(new ImportFile(path, await GitContentAsync(now)));
            }
        }
        return result;
    }

    async Task ApplyNodeAsync(DumpNode n, long rev, HashSet<string> touched)
    {
        var rel = Under(Abs(n.Path), basePath);
        if (rel is null) return;

        // add sobre caminho que ainda existe: a exclusao ficou fora da faixa (ex.: branch apagada e recriada)
        if (n.Action is 'D' or 'R' or 'A') Remove(rel, touched);
        if (n.Action == 'D') return;

        if (rel.Length == 0 || n.Kind == "dir")
        {
            if (n.Props is not null && (!n.PropDelta || n.Props.ContainsKey("svn:ignore")))
                Ignores[rel] = n.Props.GetValueOrDefault("svn:ignore");
            if (n.Action == 'M' || n.CopyFromPath is null) return; // propriedades da pasta ou pasta nova vazia
            var (source, srcRel) = await SourceAsync(n);
            var builder = _tree.ToBuilder();
            foreach (var (path, entry) in source)
            {
                if (srcRel.Length > 0 && !path.StartsWith(srcRel + "/", StringComparison.Ordinal)) continue;
                var dst = Join(rel, srcRel.Length == 0 ? path : path[(srcRel.Length + 1)..]);
                builder[dst] = entry;
                touched.Add(dst);
            }
            _tree = builder.ToImmutable();
            return;
        }

        Entry? baseEntry = null;
        if (n.Action == 'M')
        {
            baseEntry = _tree.GetValueOrDefault(rel)
                ?? throw new InvalidDataException($"dump altera arquivo desconhecido: {n.Path}@{rev}");
        }
        else if (n.CopyFromPath is not null)
        {
            var (source, srcRel) = await SourceAsync(n);
            baseEntry = source.GetValueOrDefault(srcRel)
                ?? throw new InvalidDataException($"origem da copia nao encontrada: {n.CopyFromPath}@{n.CopyFromRev}");
        }

        Entry result;
        if (n.Text is null && n.Props is null)
        {
            result = baseEntry ?? Make(Array.Empty<byte>(), null);
        }
        else
        {
            var b = baseEntry is null ? null : await ResolveAsync(baseEntry);
            var eol = b?.Eol;
            if (n.Props is not null)
            {
                if (!n.PropDelta) eol = n.Props.GetValueOrDefault("svn:eol-style")?.Trim();
                else if (n.Props.TryGetValue("svn:eol-style", out var v)) eol = v?.Trim();
            }
            var source = b?.Repo ?? Array.Empty<byte>();
            var repo = n.Text is null ? source : n.TextDelta ? SvnDiff.Apply(source, n.Text) : n.Text;
            if (n.Text is not null && n.TextMd5 is not null &&
                !string.Equals(Convert.ToHexString(MD5.HashData(repo)), n.TextMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"conteudo divergente ao aplicar o dump: {n.Path}@{rev}");
            result = Make(repo, eol);
        }
        _tree = _tree.SetItem(rel, result);
        touched.Add(rel);
    }

    void Remove(string rel, HashSet<string> touched)
    {
        var gone = _tree.Keys.Where(k => rel.Length == 0 || k == rel || k.StartsWith(rel + "/", StringComparison.Ordinal)).ToList();
        _tree = _tree.RemoveRange(gone);
        touched.UnionWith(gone);
    }

    /// <summary>Arvore da branch na revisao de origem da copia e o caminho relativo dentro dela.</summary>
    async Task<(ImmutableDictionary<string, Entry> Tree, string Rel)> SourceAsync(DumpNode n)
    {
        var srcRel = Under(Abs(n.CopyFromPath!), basePath)
            ?? throw new InvalidDataException($"copia de fora da branch no dump: {n.CopyFromPath}");
        var rev = n.CopyFromRev ?? throw new InvalidDataException($"copia sem revisao no dump: {n.Path}");
        var index = LastKeyAtOrBefore(rev);
        if (index >= 0) return (_history.Values[index], srcRel);

        // anterior ao que este dump cobre: o commit Git mais recente com svn-id ate essa revisao
        var log = await git.RunAsync("log", "--format=%H%x1f%B%x1e", targetRef);
        string? best = null, bestUrl = null;
        long bestRev = -1;
        foreach (var item in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('\x1f', 2);
            if (parts.Length == 2 && SvnId.TryParse(parts[1], out var url, out var r) && r <= rev && r > bestRev)
                (best, bestUrl, bestRev) = (parts[0].Trim(), url, r);
        }
        if (best is null) throw new InvalidDataException($"origem da copia anterior ao historico importado: {n.CopyFromPath}@{rev}");
        return (await GitTreeAsync(best, bestUrl!, bestRev), srcRel);
    }

    int LastKeyAtOrBefore(long rev)
    {
        int lo = 0, hi = _history.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_history.Keys[mid] <= rev) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    async Task<ImmutableDictionary<string, Entry>> GitTreeAsync(string commitish, string url, long rev)
    {
        if (_gitTrees.TryGetValue(commitish, out var cached)) return cached;
        var builder = Empty.ToBuilder();
        foreach (var line in (await git.RunAsync("ls-tree", "-r", "-z", commitish)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var meta = line[..tab].Split(' ');
            if (meta[1] != "blob") continue;
            var path = line[(tab + 1)..];
            builder[path] = new Entry(meta[2], null, null, new Origin(url, rev, path));
        }
        return _gitTrees[commitish] = builder.ToImmutable();
    }

    /// <summary>Entrada com conteudo na forma do repositorio e svn:eol-style conhecidos.</summary>
    async Task<Entry> ResolveAsync(Entry e)
    {
        if (e.Repo is not null) return e;
        var blob = await git.CatBlobAsync(e.GitSha);
        // sem CR e com quebra de linha, a traducao de fim de linha nao muda nada (no repositorio, arquivo com
        // svn:eol-style e sempre LF): so nos outros casos vale a ida ao servidor para saber a propriedade
        string? eol = null;
        if (Array.IndexOf(blob, (byte)'\r') >= 0 || Array.IndexOf(blob, (byte)'\n') < 0)
            eol = await EolAsync(e.From!);
        return new Entry(e.GitSha, ToRepository(blob, eol), eol, null);
    }

    async Task<string?> EolAsync(Origin o)
    {
        var url = $"{SvnClient.JoinUrl(o.Url, o.Path)}@{o.Rev}";
        if (!_eols.TryGetValue(url, out var eol))
            _eols[url] = eol = (await svn.PropGetAsync("svn:eol-style", url))?.Trim() is { Length: > 0 } v ? v : null;
        return eol;
    }

    async Task<byte[]> GitContentAsync(Entry e) =>
        e.Repo is null ? await git.CatBlobAsync(e.GitSha) : Translate(e.Repo, e.Eol);

    static Entry Make(byte[] repo, string? eol) => new(BlobSha(Translate(repo, eol)), repo, eol, null);

    /// <summary>O que o svn entrega na copia de trabalho para <c>svn:eol-style</c> (native = fim de linha da plataforma).</summary>
    static byte[] Translate(byte[] repo, string? eol) => eol switch
    {
        null => repo,
        "native" => ConvertEol(repo, OperatingSystem.IsWindows() ? "\r\n"u8 : "\n"u8),
        "CRLF" => ConvertEol(repo, "\r\n"u8),
        "LF" => ConvertEol(repo, "\n"u8),
        "CR" => ConvertEol(repo, "\r"u8),
        _ => repo,
    };

    /// <summary>Volta para a forma do repositorio: com svn:eol-style, o svn guarda tudo em LF.</summary>
    static byte[] ToRepository(byte[] content, string? eol) =>
        eol is "native" or "CRLF" or "LF" or "CR" ? ConvertEol(content, "\n"u8) : content;

    static byte[] ConvertEol(byte[] data, ReadOnlySpan<byte> target)
    {
        if (target.Length == 1 && target[0] == '\n' && Array.IndexOf(data, (byte)'\r') < 0) return data;
        using var ms = new MemoryStream(data.Length + data.Length / 16);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '\r' || b == '\n')
            {
                if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n') i++;
                ms.Write(target);
            }
            else ms.WriteByte(b);
        }
        return ms.ToArray();
    }

    static string BlobSha(byte[] content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static string Abs(string path) => "/" + path.TrimStart('/');

    /// <summary>Caminho relativo a <paramref name="basePath"/> ("" para a propria raiz) ou null se estiver fora.</summary>
    static string? Under(string path, string basePath) =>
        path == basePath ? "" : path.StartsWith(basePath + "/", StringComparison.Ordinal) ? path[(basePath.Length + 1)..] : null;

    static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
}
