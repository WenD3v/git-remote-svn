using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

public class RevisionImporter(SvnClient svn, GitClient git, StateStore state, AuthorMap authors)
{
    public async Task<int> ImportTrunkAsync(ProjectLayout layout, string targetRef = "refs/heads/main") =>
        await ImportAsync(layout, layout.TrunkUrl, targetRef, stopOnCopyOnFirst: true);

    /// <summary>
    /// Importa branches e tags do layout padrao em <c>&lt;refPrefix&gt;/heads/*</c> e <c>&lt;refPrefix&gt;/tags/*</c>.
    /// Branch/tag nova criada por <c>svn copy</c> parte do commit de origem ja importado; so o delta vira commit.
    /// Branch/tag apagada no SVN perde a ref; tag que recebeu commits no SVN nao e movida. Avisos vao para <paramref name="warnings"/>.
    /// </summary>
    public async Task ImportBranchesAndTagsAsync(ProjectLayout layout, string refPrefix = "refs/svn/origin",
        TextWriter? warnings = null)
    {
        if (!layout.Standard) return;
        warnings ??= Console.Error;
        var repoRoot = (await svn.InfoAsync(layout.RootUrl)).RepoRootUrl;

        foreach (var (kind, baseUrl) in new[] { ("heads", layout.BranchesUrl), ("tags", layout.TagsUrl) })
        {
            var prefix = $"{refPrefix}/{kind}/";
            var origins = new Dictionary<string, (long FirstRev, string? CopyPath, long? CopyRev)>(StringComparer.Ordinal);
            var names = await ListRefNamesAsync(repoRoot, baseUrl, prefix, "", origins, 0);
            foreach (var gone in state.RefsWithPrefix(prefix))
            {
                var name = gone.Substring(prefix.Length);
                if (kind == "heads" && name == "main") continue;
                if (names.Contains(name)) continue;
                await git.RunAllowAsync("update-ref", "-d", gone);
                state.RemoveLastRev(gone);
                await warnings.WriteLineAsync(
                    $"git-remote-svn: {(kind == "heads" ? "branch" : "tag")} {name} foi apagada no SVN; ref removida.");
            }

            var novas = new List<(string Name, string Url, string Ref, long FirstRev, string? CopyPath, long? CopyRev)>();
            foreach (var name in names)
            {
                var url = SvnClient.JoinUrl(baseUrl, name);
                var refName = prefix + name;
                var last = state.GetLastRev(refName);
                if (last is null)
                {
                    var (first, copyPath, copyRev) = origins.TryGetValue(name, out var o) ? o : await FindCopyOriginAsync(repoRoot, url);
                    novas.Add((name, url, refName, first, copyPath, copyRev));
                }
                else if (kind == "heads")
                {
                    await ImportAsync(layout, url, refName);
                }
                else
                {
                    await WarnIfTagChangedAsync(repoRoot, url, name, last.Value, warnings);
                }
            }

            foreach (var n in novas.OrderBy(n => n.FirstRev))
            {
                if (n.CopyPath is not null && n.CopyRev is not null)
                {
                    var parent = await ResolveCommitAsync(repoRoot, n.CopyPath, n.CopyRev.Value, refPrefix, 0);
                    if (parent is not null) await git.UpdateRefAsync(n.Ref, parent);
                }
                await ImportAsync(layout, n.Url, n.Ref, stopOnCopyOnFirst: true);
            }
        }
    }

    /// <summary>
    /// Nomes de branches/tags sob <paramref name="baseUrl"/>. Pasta criada por <c>svn copy</c> (ou com arquivos) e
    /// branch; pasta criada sem copia e so com subpastas e hierarquia (<c>feature/x</c>) e e percorrida.
    /// </summary>
    async Task<HashSet<string>> ListRefNamesAsync(string repoRoot, string baseUrl, string prefix, string parent,
        Dictionary<string, (long FirstRev, string? CopyPath, long? CopyRev)> origins, int depth)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<SvnEntry> entries;
        try { entries = await svn.ListAsync(baseUrl); }
        catch (SvnException) { return result; }

        foreach (var dir in entries.Where(e => e.Kind == "dir"))
        {
            var name = parent + dir.Name;
            if (state.GetLastRev(prefix + name) is not null) { result.Add(name); continue; }
            var url = SvnClient.JoinUrl(baseUrl, dir.Name);
            var origin = await FindCopyOriginAsync(repoRoot, url);
            if (origin.CopyPath is null && depth < 5)
            {
                var children = await svn.ListAsync(url);
                if (children.Count > 0 && children.All(c => c.Kind == "dir"))
                {
                    result.UnionWith(await ListRefNamesAsync(repoRoot, url, prefix, name + "/", origins, depth + 1));
                    continue;
                }
            }
            origins[name] = origin;
            result.Add(name);
        }
        return result;
    }

    /// <summary>Primeira revisao da linha de historico de <paramref name="url"/> e, se for copia, sua origem.</summary>
    async Task<(long FirstRev, string? CopyPath, long? CopyRev)> FindCopyOriginAsync(string repoRoot, string url)
    {
        var head = (await svn.InfoAsync(url)).Revision;
        var revs = await svn.LogAsync(url, 1, head, stopOnCopy: true, limit: 1);
        if (revs.Count == 0) return (head, null, null);
        var basePath = RelativePath(repoRoot, url);
        var create = revs[0].Paths.FirstOrDefault(p => p.Path == basePath && p.CopyFromPath is not null);
        return (revs[0].Revision, create?.CopyFromPath, create?.CopyFromRev);
    }

    /// <summary>Commit Git que representa <paramref name="path"/>@<paramref name="rev"/>, seguindo copias se preciso.</summary>
    async Task<string?> ResolveCommitAsync(string repoRoot, string path, long rev, string refPrefix, int depth)
    {
        var log = await git.RunAllowAsync("log", $"--glob={refPrefix}/*", "--format=%H%x1f%B%x1e");
        string? best = null;
        long bestRev = -1;
        foreach (var entry in log.StdOut.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('\x1f', 2);
            if (parts.Length < 2 || !SvnId.TryParse(parts[1], out var url, out var r)) continue;
            if (!url.StartsWith(repoRoot, StringComparison.Ordinal) || RelativePath(repoRoot, url) != path) continue;
            if (r <= rev && r > bestRev) { bestRev = r; best = parts[0].Trim(); }
        }
        if (best is not null || depth >= 10) return best;

        // a origem pode nao ter commits proprios (ex.: branch de branch sem commit): sobe pela copia dela
        try
        {
            var srcUrl = repoRoot + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            var revs = await svn.LogAsync($"{srcUrl}@{rev}", 1, rev, stopOnCopy: true);
            var create = revs.FirstOrDefault()?.Paths.FirstOrDefault(p => p.Path == path && p.CopyFromPath is not null);
            return create is null ? null
                : await ResolveCommitAsync(repoRoot, create.CopyFromPath!, create.CopyFromRev!.Value, refPrefix, depth + 1);
        }
        catch (SvnException)
        {
            return null;
        }
    }

    async Task WarnIfTagChangedAsync(string repoRoot, string url, string name, long lastRev, TextWriter warnings)
    {
        var head = (await svn.InfoAsync(url)).Revision;
        if (head <= lastRev) return;
        var basePath = RelativePath(repoRoot, url);
        var revs = await svn.LogAsync(url, lastRev + 1, head, stopOnCopy: false);
        var changed = revs.Where(r => r.Paths.Any(p => p.Path.StartsWith(basePath + "/", StringComparison.Ordinal))).ToList();
        if (changed.Count > 0)
            await warnings.WriteLineAsync(
                $"git-remote-svn: aviso: tag {name} recebeu commits no SVN (r{string.Join(", r", changed.Select(r => r.Revision))}); a tag local nao foi movida.");
    }

    static string RelativePath(string repoRoot, string url) => Uri.UnescapeDataString(url.Substring(repoRoot.Length));

    /// <summary>Importa as revisoes de <paramref name="branchUrl"/> para a ref indicada.</summary>
    public async Task<int> ImportAsync(ProjectLayout layout, string branchUrl, string targetRef,
        bool stopOnCopyOnFirst = false, long? sinceRev = null)
    {
        var info = await svn.InfoAsync(branchUrl);
        var repoRoot = info.RepoRootUrl;
        var basePath = RelativePath(repoRoot, branchUrl);
        var shortBranch = targetRef.StartsWith("refs/heads/") ? targetRef.Substring(11) : targetRef;

        await git.ConfigSetAsync("core.autocrlf", "false");
        await git.ConfigSetAsync("core.longpaths", "true");
        await EnsureNoEolConversionAsync();
        if (targetRef.StartsWith("refs/heads/"))
            await git.ConfigSetAsync($"branch.{shortBranch}.rebase", "true");
        state.SaveLayout(layout);

        var refExists = await git.RefExistsAsync(targetRef);
        var lastRev = state.GetLastRev(targetRef) ?? sinceRev;
        // a ref manda: se o estado passou dela (queda antes do fast-import terminar) ou se perdeu, retoma do que o Git tem
        if (refExists && await TipRevAsync(targetRef, branchUrl) is long tip && (lastRev is null || tip < lastRev)) lastRev = tip;
        var initial = lastRev is null;
        var start = (lastRev ?? 0) + 1;
        if (start > info.Revision) return 0;

        var revs = (await svn.LogAsync(branchUrl, start, info.Revision, initial && stopOnCopyOnFirst)).ToList();
        var count = 0;
        var pending = revs;
        if (pending.Count > 0 && await svn.HasSvnrdumpAsync())
        {
            if (initial && refExists)
            {
                // branch nova com a ref ja na origem: a revisao da copia sai pelo log (so o que mudou junto com
                // a copia vira commit); o dump traria a arvore inteira de novo
                count += await ImportViaLogAsync(repoRoot, basePath, branchUrl, info.Uuid, targetRef, pending.Take(1).ToList(),
                    initial: true, refExists: true);
                pending = pending.Skip(1).ToList();
            }
            if (pending.Count > 0 && await ImportViaDumpAsync(basePath, branchUrl, info.Uuid, targetRef, pending) is int n)
            {
                count += n;
                pending = new List<SvnRevision>();
            }
        }
        if (pending.Count > 0)
        {
            count += await ImportViaLogAsync(repoRoot, basePath, branchUrl, info.Uuid, targetRef, pending,
                initial: initial && pending.Count == revs.Count, refExists: await git.RefExistsAsync(targetRef));
            // sem dump, o svn:ignore vem do servidor; propget -R percorre pasta a pasta (lento), so com revisao nova
            await ApplyIgnoreAsync(await svn.PropGetRecursiveAsync("svn:ignore", branchUrl));
        }

        // so depois do fast-import concluir: estado nunca a frente das refs
        if (revs.Count > 0) state.SetLastRev(targetRef, revs[^1].Revision);
        return count;
    }

    /// <summary>
    /// Todas as revisoes de uma vez via <c>svnrdump</c> (um fluxo so, em vez de um <c>svn cat</c> por arquivo).
    /// null quando o dump tem algo que so o caminho pelo log trata (copia vinda de fora da branch).
    /// </summary>
    async Task<int?> ImportViaDumpAsync(string basePath, string branchUrl, string uuid, string targetRef,
        IReadOnlyList<SvnRevision> revs)
    {
        var refExists = await git.RefExistsAsync(targetRef);
        var file = Path.Combine(Path.GetTempPath(), "grs-dump-" + Guid.NewGuid().ToString("N") + ".dump");
        try
        {
            await svn.DumpAsync(branchUrl, revs[0].Revision, revs[^1].Revision, incremental: refExists, file);
            await using (var check = File.OpenRead(file))
                if (!DumpApplier.Supported(SvnDumpReader.Read(check), basePath)) return null;

            var applier = new DumpApplier(svn, git, basePath, targetRef);
            if (refExists) await applier.LoadBaselineAsync(targetRef, branchUrl, revs[0].Revision - 1);
            var count = 0;
            await using (var writer = await git.BeginFastImportAsync())
            await using (var stream = File.OpenRead(file))
            {
                foreach (var rev in SvnDumpReader.Read(stream))
                {
                    var changes = await applier.ApplyAsync(rev);
                    if (changes.Count == 0) continue;
                    var (name, email) = authors.Resolve(rev.Props.GetValueOrDefault("svn:author") ?? "");
                    var date = rev.Props.GetValueOrDefault("svn:date") is string d
                        ? DateTimeOffset.Parse(d, System.Globalization.CultureInfo.InvariantCulture)
                        : DateTimeOffset.UnixEpoch;
                    var message = (rev.Props.GetValueOrDefault("svn:log") ?? "") + "\n\n" + SvnId.Format(branchUrl, rev.Revision, uuid);
                    await writer.CommitAsync(targetRef, count == 0 && refExists ? targetRef : null, (name, email, date), message, changes);
                    count++;
                }
            }
            await ApplyIgnoreAsync(applier.Ignores.Where(i => i.Value is not null).Select(i => (i.Key, i.Value!)));
            return count;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Caminho por revisao: <c>svn cat</c> por arquivo alterado (sem <c>svnrdump</c> ou para casos que o dump nao cobre).</summary>
    async Task<int> ImportViaLogAsync(string repoRoot, string basePath, string branchUrl, string uuid, string targetRef,
        IReadOnlyList<SvnRevision> revs, bool initial, bool refExists)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (refExists)
        {
            var tree = await git.RunAsync("ls-tree", "-r", "-z", "--name-only", targetRef);
            foreach (var f in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries)) files.Add(f);
        }

        var count = 0;
        var first = true;
        var firstRev = true;
        var hasParent = refExists;
        await using (var writer = await git.BeginFastImportAsync())
        {
            foreach (var rev in revs)
            {
                // a copia que cria a branch ja e o commit pai (ref posicionada na origem); copia posterior da
                // raiz e branch apagada e recriada: a arvore e substituida pela da nova origem
                var resetRoot = !(initial && firstRev && hasParent);
                firstRev = false;
                var changes = await BuildChangesAsync(repoRoot, basePath, rev, files, resetRoot);
                if (changes.Count > 0)
                {
                    var (name, email) = authors.Resolve(rev.Author);
                    var message = rev.Message + "\n\n" + SvnId.Format(branchUrl, rev.Revision, uuid);
                    await writer.CommitAsync(targetRef, first && refExists ? targetRef : null,
                        (name, email, rev.Date), message, changes);
                    count++;
                    first = false;
                    hasParent = true;
                }
            }
        }
        return count;
    }

    /// <summary>Revisao do svn-id do topo da ref, se o topo pertence a <paramref name="branchUrl"/>.</summary>
    async Task<long?> TipRevAsync(string refName, string branchUrl)
    {
        var body = await git.RunAsync("log", "-1", "--format=%B", refName);
        return SvnId.TryParse(body, out var url, out var rev) && url == branchUrl ? rev : null;
    }

    async Task<List<ImportFile>> BuildChangesAsync(string repoRoot, string basePath, SvnRevision rev,
        HashSet<string> currentFiles, bool resetRoot)
    {
        var result = new List<ImportFile>();
        // ordem por caminho: a copia de uma pasta vem antes das alteracoes dentro dela na mesma revisao
        foreach (var change in rev.Paths.OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            var isRoot = change.Path == basePath;
            if (!isRoot && !change.Path.StartsWith(basePath + "/", StringComparison.Ordinal))
                continue;
            var rel = isRoot ? "" : change.Path.Substring(basePath.Length + 1);
            var dirPrefix = isRoot ? "" : rel + "/";

            if (change.Action == 'D' || (change.Action == 'R' && change.Kind == "dir"))
            {
                if (isRoot && change.Action == 'D') continue;
                if (!isRoot)
                {
                    foreach (var f in currentFiles.Where(f => f == rel || f.StartsWith(dirPrefix, StringComparison.Ordinal)).ToList())
                    {
                        result.Add(new ImportFile(f, null));
                        currentFiles.Remove(f);
                    }
                    if (change.Action == 'D') continue;
                }
            }

            if (change.Kind == "dir")
            {
                // pasta copiada traz conteudo que o log nao lista
                if (change.CopyFromPath is null) continue;
                if (isRoot)
                {
                    if (!resetRoot) continue;
                    foreach (var f in currentFiles.ToList()) result.Add(new ImportFile(f, null));
                    currentFiles.Clear();
                }
                var dirUrl = repoRoot + string.Join('/', change.Path.Split('/').Select(Uri.EscapeDataString));
                foreach (var (f, content) in await svn.ExportFilesAsync($"{dirUrl}@{rev.Revision}"))
                {
                    var path = dirPrefix + f;
                    result.Add(new ImportFile(path, content));
                    currentFiles.Add(path);
                }
            }
            else if (!isRoot)
            {
                var fileUrl = repoRoot + string.Join('/', change.Path.Split('/').Select(Uri.EscapeDataString));
                var content = await svn.CatAsync($"{fileUrl}@{rev.Revision}");
                result.Add(new ImportFile(rel, content));
                currentFiles.Add(rel);
            }
        }
        return result;
    }

    /// <summary>
    /// O git clone le a configuracao antes do helper gravar core.autocrlf=false; com autocrlf=true no sistema o
    /// checkout converteria para CRLF e o proximo commit levaria mudanca de fim de linha. Atributo local
    /// "* -text" (lido no checkout, precedencia maxima) mantem os arquivos byte a byte.
    /// </summary>
    async Task EnsureNoEolConversionAsync()
    {
        var infoDir = Path.Combine(git.GitDir, "info");
        Directory.CreateDirectory(infoDir);
        var attributes = Path.Combine(infoDir, "attributes");
        var existing = File.Exists(attributes) ? await File.ReadAllTextAsync(attributes) : "";
        if (existing.Split('\n').Any(l => l.Trim() == "* -text")) return;
        await File.AppendAllTextAsync(attributes, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + "* -text\n");
    }

    async Task ApplyIgnoreAsync(IEnumerable<(string Dir, string Value)> ignores)
    {
        // svn:ignore vale para os filhos diretos da pasta: na raiz mantem o padrao livre; em subpasta, ancorado nela
        var patterns = new List<string>();
        foreach (var (dir, value) in ignores)
            foreach (var p in value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                patterns.Add(dir.Length == 0 ? p : $"/{dir}/{p}");
        if (patterns.Count == 0) return;
        var infoDir = Path.Combine(git.GitDir, "info");
        Directory.CreateDirectory(infoDir);
        var exclude = Path.Combine(infoDir, "exclude");
        var existing = File.Exists(exclude) ? await File.ReadAllTextAsync(exclude) : "";
        var known = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var lines = patterns.Distinct().Where(l => !known.Contains(l));
        var add = string.Join('\n', lines);
        if (add.Length > 0)
            await File.AppendAllTextAsync(exclude, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + add + "\n");
    }
}
