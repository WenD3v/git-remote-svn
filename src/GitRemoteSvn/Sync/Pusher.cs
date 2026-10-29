using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

public record PushResult(int RevisionsCreated, int EmptyCommitsSkipped, IReadOnlyList<string> Warnings)
{
    /// <summary>Ultimo commit espelho (com svn-id) da branch remota apos o push.</summary>
    public string? RemoteHead { get; init; }
}

/// <summary>Push recusado sem tocar no servidor (ex.: SVN avancou e o usuario precisa de git pull).</summary>
public class PushRejectedException(string message) : Exception(message);

/// <summary>
/// Publica commits Git no SVN: cada commit da linha de primeira descendencia vira uma revisao com a
/// mensagem original; em seguida grava um commit espelho (mesma arvore, autor e mensagem + trailer
/// svn-id) e move a ref remota e a branch local para os espelhos (rebase interno, como o git svn dcommit).
/// </summary>
public class Pusher(SvnClient svn, GitClient git, SvnWorkingCopy wc, StateStore state)
{
    /// <summary>Chamado logo apos cada revisao criada (antes do espelho); usado para simular quedas.</summary>
    public Func<int, Task>? AfterEachRevision { get; set; }

    public SyncLog? Log { get; set; }

    public async Task<PushResult> PushBranchAsync(string localRef, string branchUrl, string remoteRef, bool dryRun)
    {
        var warnings = new List<string>();
        var localSha = await git.RevParseAsync(localRef + "^{commit}");
        // sem ref remota: so a branch vazia no SVN (ex.: trunk de projeto novo) recebe o primeiro push
        var remoteSha = await git.RefExistsAsync(remoteRef) ? await git.RevParseAsync(remoteRef) : null;
        if (localSha == remoteSha) return new PushResult(0, 0, warnings) { RemoteHead = remoteSha };

        if (remoteSha is null)
        {
            if ((await svn.ListAsync(branchUrl)).Count > 0)
                throw new PushRejectedException($"{branchUrl} ja tem arquivos no SVN; rode git fetch e git pull --rebase e repita o push");
        }
        else
        {
            var (ff, _, _) = await git.RunAllowAsync("merge-base", "--is-ancestor", remoteSha, localSha);
            if (ff != 0)
                throw new PushRejectedException("o SVN tem revisoes que voce ainda nao tem; rode git pull e repita o push");
        }

        var commits = (await git.RunAsync("rev-list", "--first-parent", "--reverse",
                remoteSha is null ? localSha : $"{remoteSha}..{localSha}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // a linha publicada precisa partir do topo do SVN; um merge do git pull sem rebase deixaria commits
        // locais sobre uma base antiga e o primeiro deles reverteria, no SVN, o trabalho do colega
        if (remoteSha is not null && await FirstParentAsync(commits[0]) != remoteSha)
            throw new PushRejectedException(
                "o historico local tem um merge feito pelo git pull sem rebase; rode git pull --rebase e repita o push");
        // valida todos os merges antes de qualquer escrita no SVN: nada de push pela metade por recusa previsivel
        var merges = new Dictionary<string, MergeSource>();
        foreach (var c in commits)
        {
            var source = await MergeSourceAsync(c);
            if (source is not null) merges[c] = source;
        }
        if (dryRun)
        {
            foreach (var c in commits)
                warnings.Add($"[dry-run] {c[..7]} {await SubjectAsync(c)} -> 1 revisao em {branchUrl}");
            return new PushResult(commits.Length, 0, warnings) { RemoteHead = remoteSha };
        }

        var clock = Stopwatch.StartNew();
        var info = await svn.InfoAsync(branchUrl);
        var uuid = info.Uuid;
        long lastRev;
        if (remoteSha is null)
            lastRev = info.LastChangedRev;
        else
        {
            // o svn-id da ref e o que o Git de fato tem; o estado pode estar adiantado por revisoes sem alteracao
            var tipRev = await SvnRevOfAsync(remoteSha);
            var known = state.GetLastRev(remoteRef);
            lastRev = Math.Max(tipRev ?? 0, known ?? 0);
            if (tipRev is null && known is null) throw new PushRejectedException($"{remoteRef} nao tem svn-id; rode git fetch");
        }
        // o SVN nao pode ter nada que o Git ainda nao importou (senao o commit poderia ser aceito e a revisao do
        // colega ficaria fora do historico Git). Excecao: revisoes deste mesmo push que ja entraram antes de uma
        // queda - registradas no estado logo apos cada svn commit e conferidas, em ordem, pela mensagem.
        var pushed = state.GetPushedRevs(remoteRef);
        var pending = new Queue<SvnRevision>();
        if (info.LastChangedRev > lastRev)
            foreach (var r in await svn.LogAsync(branchUrl, lastRev + 1, info.LastChangedRev, stopOnCopy: false)) pending.Enqueue(r);
        const string Stale = "o SVN recebeu revisoes novas desde o ultimo fetch; rode git pull e repita o push";
        var path = await wc.EnsureAsync(branchUrl, lastRev);
        Log?.Write($"push {localRef}: {commits.Length} commit(s) sobre r{lastRev}; wc pronta em {clock.ElapsedMilliseconds} ms");
        await wc.RevertAsync(path);
        // arquivos que ja estao no SVN nesta revisao: base para saber o que baixar, apagar ou adicionar
        var repoFiles = new HashSet<string>(await svn.ListFilesAsync(At(branchUrl, lastRev)), StringComparer.Ordinal);
        Log?.Write($"wc limpa e arvore listada ({repoFiles.Count} arquivos) em {clock.ElapsedMilliseconds} ms");

        var mirror = remoteSha;
        int created = 0, empty = 0, adopted = 0;
        try
        {
            foreach (var c in commits)
            {
                var changes = await DiffAsync(await FirstParentAsync(c) ?? EmptyTree, c, warnings);
                var message = (await git.RunAsync("log", "-1", "--format=%B", c)).TrimEnd();
                merges.TryGetValue(c, out var merge);
                var parents = new[] { mirror, merge?.Parent }.OfType<string>().ToArray();
                if (changes.Count == 0)
                {
                    // nenhuma revisao vazia: o commit fica mapeado para a revisao corrente
                    empty++;
                    warnings.Add($"{c[..7]} nao altera nada no SVN; nenhuma revisao criada para ele");
                    mirror = await CreateMirrorAsync(c, parents, message + "\n\n" + SvnId.Format(branchUrl, lastRev, uuid));
                    continue;
                }
                if (pending.Count > 0)
                {
                    var r = pending.Dequeue();
                    if (!pushed.Contains(r.Revision) || r.Message.TrimEnd() != message) throw new PushRejectedException(Stale);
                    mirror = await CreateMirrorAsync(c, parents, message + "\n\n" + SvnId.Format(branchUrl, r.Revision, uuid));
                    lastRev = r.Revision;
                    adopted++;
                    Log?.Write($"r{r.Revision} ja estava no SVN (push anterior interrompido) <- {c[..7]} {FirstLine(message)}");
                    // a revisao adotada ja tem o conteudo deste commit no SVN: o snapshot passa a refletir isso
                    foreach (var change in changes)
                        if (change.Content is null) repoFiles.Remove(change.Path);
                        else repoFiles.Add(change.Path);
                    lastRev = r.Revision;
                    adopted++;
                    continue;
                }
                await wc.ApplyTreeAsync(path, branchUrl, lastRev, changes, repoFiles);
                if (merge is not null)
                    await wc.SetMergeInfoAsync(path, AddMergeInfo(await svn.PropGetAsync("svn:mergeinfo", path), merge));
                long rev;
                try
                {
                    rev = await wc.CommitAsync(path, message);
                }
                catch (SvnException ex) when (IsOutOfDate(ex.Message))
                {
                    await wc.RevertAsync(path);
                    throw new PushRejectedException(
                        $"o SVN recebeu revisoes novas durante o push ({created} revisao(oes) ja criada(s)); rode git pull e repita o push");
                }
                if (rev == 0)
                {
                    await wc.RevertAsync(path);
                    empty++;
                    warnings.Add($"{c[..7]} nao altera nada no SVN; nenhuma revisao criada para ele");
                    mirror = await CreateMirrorAsync(c, parents, message + "\n\n" + SvnId.Format(branchUrl, lastRev, uuid));
                    continue;
                }
                state.AddPushedRev(remoteRef, rev);
                Log?.Write($"r{rev} criada <- {c[..7]} {FirstLine(message)}");
                // alguem commitou entre a verificacao e o nosso commit: nao espelha; o proximo fetch traz as duas
                var between = rev - 1 > lastRev ? await svn.LogAsync(branchUrl, lastRev + 1, rev - 1, stopOnCopy: false) : [];
                if (between.Count > 0)
                    throw new PushRejectedException(
                        $"r{rev} criada, mas o SVN recebeu r{between[0].Revision} de outra pessoa ao mesmo tempo; rode git pull antes de continuar");
                mirror = await CreateMirrorAsync(c, parents, message + "\n\n" + SvnId.Format(branchUrl, rev, uuid));
                lastRev = rev;
                created++;
                if (AfterEachRevision is not null) await AfterEachRevision(created);
                // avanca so a revisao das pastas tocadas (sem baixar conteudo) para o proximo commit
                await wc.RefreshAsync(path, rev, changes);
            }
            if (pending.Count > 0) throw new PushRejectedException(Stale);
        }
        catch (Exception ex)
        {
            // ref remota e estado so avancam no fim: a reexecucao reconhece as revisoes ja criadas
            Log?.Write($"erro apos {created} revisao(oes) criada(s): {ex.Message}");
            throw;
        }

        await git.RunAsync("update-ref", remoteRef, mirror!);
        state.SetLastRev(remoteRef, lastRev);
        state.ClearPushedRevs(remoteRef);
        Log?.Write($"push concluido em {clock.ElapsedMilliseconds} ms: {created} revisao(oes) criada(s), {adopted} adotada(s), {empty} vazia(s)");
        if (adopted > 0)
            warnings.Add($"{adopted} revisao(oes) de um push interrompido reconhecida(s); nada foi reenviado");

        if (localRef.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var (code, _, _) = await git.RunAllowAsync("update-ref", localRef, mirror!, localSha);
            if (code != 0)
                warnings.Add($"{localRef} mudou durante o push e nao foi reescrita; rode git pull");
        }
        if (empty > 0)
            warnings.Add($"{empty} commit(s) sem alteracao nao viraram revisao no SVN");
        return new PushResult(created, empty, warnings) { RemoteHead = mirror };
    }

    /// <summary>
    /// Branch que ainda nao existe no SVN: <c>svn copy</c> da revisao do ultimo commit ja publicado na historia
    /// de <paramref name="localRef"/> e, em seguida, o push linear dos commits restantes.
    /// </summary>
    public async Task<PushResult> PushNewBranchAsync(string localRef, string branchUrl, string remoteRef, bool dryRun)
    {
        var localSha = await git.RevParseAsync(localRef + "^{commit}");
        var unpublished = (await git.RunAsync("rev-list", "--first-parent", localSha, "--not", $"--glob={RemotePrefix(remoteRef)}*"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baseSha = localSha;
        if (unpublished.Length > 0)
        {
            var (code, parent, _) = await git.RunAllowAsync("rev-parse", "--verify", "-q", unpublished[^1] + "^1");
            if (code != 0)
                throw new PushRejectedException("a branch nao parte de nenhum commit que ja esteja no SVN");
            baseSha = parent.Trim();
        }
        var body = await git.RunAsync("log", "-1", "--format=%B", baseSha);
        if (!SvnId.TryParse(body, out var srcUrl, out var srcRev))
            throw new PushRejectedException($"o commit-base {baseSha[..7]} nao tem svn-id; rode git pull --rebase e repita");

        var name = remoteRef.Substring(remoteRef.IndexOf("/heads/", StringComparison.Ordinal) + 7);
        if (dryRun)
        {
            var warnings = new List<string> { $"[dry-run] svn copy {srcUrl}@{srcRev} {branchUrl}" };
            foreach (var c in unpublished.Reverse())
                warnings.Add($"[dry-run] {c[..7]} {await SubjectAsync(c)} -> 1 revisao em {branchUrl}");
            return new PushResult(unpublished.Length, 0, warnings) { RemoteHead = baseSha };
        }

        var copyRev = await svn.CopyAsync($"{srcUrl}@{srcRev}", branchUrl, $"Cria a branch {name}");
        await git.UpdateRefAsync(remoteRef, baseSha);
        state.SetLastRev(remoteRef, copyRev);
        return await PushBranchAsync(localRef, branchUrl, remoteRef, dryRun: false);
    }

    /// <summary>Tag nova: <c>svn copy</c> da revisao do commit (que precisa estar no SVN). Tags sao imutaveis.</summary>
    public async Task<PushResult> PushTagAsync(string tagName, string objectSha, string tagUrl, string tagRef, bool dryRun)
    {
        if (await git.RefExistsAsync(tagRef))
            throw new PushRejectedException($"a tag {tagName} ja existe no SVN; tags sao imutaveis (crie outra tag)");
        var commit = await git.RevParseAsync(objectSha + "^{commit}");
        var body = await git.RunAsync("log", "-1", "--format=%B", commit);
        if (!SvnId.TryParse(body, out var srcUrl, out var srcRev))
            throw new PushRejectedException(
                $"o commit {commit[..7]} da tag {tagName} nao esta no SVN; publique a branch antes (git push) e recrie a tag no commit publicado");
        if (dryRun)
            return new PushResult(1, 0, new[] { $"[dry-run] svn copy {srcUrl}@{srcRev} {tagUrl}" });

        var copyRev = await svn.CopyAsync($"{srcUrl}@{srcRev}", tagUrl, $"Cria a tag {tagName}");
        await git.UpdateRefAsync(tagRef, objectSha);
        state.SetLastRev(tagRef, copyRev);
        return new PushResult(1, 0, Array.Empty<string>()) { RemoteHead = objectSha };
    }

    /// <summary><c>refs/svn/&lt;remote&gt;/</c> a partir de uma ref privada.</summary>
    static string RemotePrefix(string privateRef)
    {
        var parts = privateRef.Split('/');
        return string.Join('/', parts.Take(3)) + "/";
    }

    /// <summary>URL da revisao (a URL ja pode vir com peg de outro ponto do fluxo).</summary>
    static string At(string url, long rev) =>
        url.LastIndexOf('@') > url.LastIndexOf('/') ? url : $"{url}@{rev.ToString(CultureInfo.InvariantCulture)}";

    // E155011 arquivo desatualizado, E160028/E160024 conflito no servidor, E170004 item desatualizado
    static bool IsOutOfDate(string message) =>
        message.Contains("E155011") || message.Contains("E160028") || message.Contains("E160024") ||
        message.Contains("E170004") || message.Contains("out of date", StringComparison.OrdinalIgnoreCase);

    static string FirstLine(string message) => message.Split('\n')[0].Trim();

    async Task<string> SubjectAsync(string sha) => (await git.RunAsync("log", "-1", "--format=%s", sha)).Trim();

    const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    async Task<string?> FirstParentAsync(string sha)
    {
        var (code, parent, _) = await git.RunAllowAsync("rev-parse", "--verify", "-q", sha + "^1");
        return code == 0 ? parent.Trim() : null;
    }

    async Task<long?> SvnRevOfAsync(string sha)
    {
        var body = await git.RunAsync("log", "-1", "--format=%B", sha);
        return SvnId.TryParse(body, out _, out var rev) ? rev : null;
    }

    /// <summary>Alteracoes de <paramref name="from"/> para <paramref name="to"/> como ImportFile (null = apagar).</summary>
    internal async Task<List<ImportFile>> DiffAsync(string from, string to, List<string> warnings)
    {
        var raw = await git.RunAsync("diff-tree", "-r", "-z", "--no-renames", "--no-commit-id", from, to);
        var parts = raw.Split('\0');
        var result = new List<ImportFile>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var meta = parts[i].TrimStart(':').Split(' ');
            if (meta.Length < 5) break;
            var (newMode, newSha, status, file) = (meta[1], meta[3], meta[4], parts[i + 1]);
            if (status == "D")
                result.Add(new ImportFile(file, null));
            else if (newMode == "160000")
                warnings.Add($"submodulo {file} ignorado (o SVN nao tem equivalente)");
            else
                result.Add(new ImportFile(file, await git.CatBlobAsync(newSha)));
        }
        return result;
    }

    /// <summary>Origem SVN do segundo pai de um merge.</summary>
    record MergeSource(string Parent, string Path, long FromRev, long ToRev);

    /// <summary>
    /// Null para commit comum. Para merge, exige exatamente 2 pais e o segundo ja publicado no SVN
    /// (com svn-id); caso contrario recusa o push inteiro.
    /// </summary>
    async Task<MergeSource?> MergeSourceAsync(string commit)
    {
        var parents = (await git.RunAsync("rev-list", "--parents", "-n1", commit))
            .Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        if (parents.Length < 2) return null;
        if (parents.Length > 2)
            throw new PushRejectedException(
                $"{commit[..7]} e um merge octopus ({parents.Length} pais); o SVN registra um merge por vez - faca merges separados");

        var second = parents[1];
        var body = await git.RunAsync("log", "-1", "--format=%B", second);
        if (!SvnId.TryParse(body, out var url, out var rev))
        {
            var name = (await git.RunAllowAsync("name-rev", "--name-only", "--refs=refs/heads/*", second)).StdOut.Trim();
            name = name.Length == 0 || name == "undefined" ? second[..7] : name.Split('~', '^')[0];
            throw new PushRejectedException(
                $"o merge {commit[..7]} traz commits que nao estao no SVN; publique a branch antes: git push -u origin {name}");
        }
        var repoRoot = (await svn.InfoAsync(url)).RepoRootUrl;
        var path = Uri.UnescapeDataString(url.Substring(repoRoot.Length));
        // primeira revisao da linha da branch (a copia que a criou)
        var first = await svn.LogAsync($"{url}@{rev}", 1, rev, stopOnCopy: true, limit: 1);
        return new MergeSource(second, path, first.Count > 0 ? first[0].Revision : rev, rev);
    }

    /// <summary>Acrescenta o intervalo do merge ao svn:mergeinfo existente sem sobrepor faixas ja registradas.</summary>
    static string AddMergeInfo(string? existing, MergeSource merge)
    {
        var lines = (existing ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var idx = lines.FindIndex(l => l.StartsWith(merge.Path + ":", StringComparison.Ordinal));
        if (idx < 0)
        {
            lines.Add($"{merge.Path}:{merge.FromRev}-{merge.ToRev}");
            return string.Join('\n', lines);
        }
        var ranges = lines[idx].Substring(merge.Path.Length + 1);
        var maxEnd = ranges.Split(',').Select(r => long.Parse(r.Split('-')[^1].TrimEnd('*'))).Max();
        var from = Math.Max(merge.FromRev, maxEnd + 1);
        if (from <= merge.ToRev)
            lines[idx] += from == merge.ToRev ? $",{from}" : $",{from}-{merge.ToRev}";
        return string.Join('\n', lines);
    }

    /// <summary>Commit com a mesma arvore, autor e committer de <paramref name="original"/>, com os pais indicados.</summary>
    async Task<string> CreateMirrorAsync(string original, IReadOnlyList<string> parents, string message)
    {
        var who = (await git.RunAsync("log", "-1", "--date=raw", "--format=%an%x00%ae%x00%ad%x00%cn%x00%ce%x00%cd", original))
            .TrimEnd('\n', '\r').Split('\0');
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = who[0], ["GIT_AUTHOR_EMAIL"] = who[1], ["GIT_AUTHOR_DATE"] = who[2],
            ["GIT_COMMITTER_NAME"] = who[3], ["GIT_COMMITTER_EMAIL"] = who[4], ["GIT_COMMITTER_DATE"] = who[5],
        };
        var msgFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(msgFile, message + "\n", new UTF8Encoding(false));
            var args = new List<string> { "commit-tree", $"{original}^{{tree}}" };
            foreach (var p in parents) args.AddRange(new[] { "-p", p });
            args.AddRange(new[] { "-F", msgFile });
            return (await git.RunWithEnvAsync(env, args.ToArray())).Trim();
        }
        finally { File.Delete(msgFile); }
    }
}
