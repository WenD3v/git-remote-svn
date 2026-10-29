using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

/// <summary>
/// Working copies SVN temporarias (uma por branch, em <c>.git/svn/wc</c>) usadas para publicar commits.
/// O checkout e raso (<c>--depth empty</c>): so a raiz entra e cada push baixa apenas as pastas e os arquivos
/// que vai alterar - um checkout completo da arvore custava mais que todo o resto do push junto.
/// </summary>
public class SvnWorkingCopy(SvnClient svn, string rootDir)
{
    const int DirChunk = 50;
    const int FileChunk = 100;

    /// <summary>
    /// Garante a working copy de <paramref name="branchUrl"/> e devolve o caminho. Com a WC ja criada nada e
    /// baixado nem atualizado: conteudo entra sob demanda em <see cref="ApplyTreeAsync"/>.
    /// </summary>
    public async Task<string> EnsureAsync(string branchUrl, long rev)
    {
        var path = Path.Combine(rootDir, DirName(branchUrl));
        if (Directory.Exists(Path.Combine(path, ".svn")))
        {
            // queda no meio de um commit/add/update deixa a WC travada: o marcador fica ao lado da pasta porque
            // "cleanup --remove-unversioned" apaga qualquer arquivo solto dentro dela
            if (File.Exists(BusyPath(path)))
            {
                await svn.RunAsync(new[] { "cleanup", path });
                File.Delete(BusyPath(path));
            }
        }
        else
        {
            Directory.CreateDirectory(rootDir);
            await svn.RunAsync(new[] { "checkout", "-q", "--non-interactive", "--depth", "empty", $"{branchUrl}@{rev}", path });
        }
        return path;
    }

    /// <summary>
    /// Aplica as alteracoes (conteudo novo ou null = apagar) no working copy, mantendo o versionamento.
    /// Baixa da WC so o necessario: as pastas afetadas (profundidade vazia) e os arquivos que ja existem no SVN
    /// e faltam no disco - inclusive os que serao apagados, que precisam estar versionados.
    /// </summary>
    /// <param name="repoFiles">
    /// Arquivos versionados na revisao base. Mantido entre os commits de um mesmo push (e atualizado aqui) para
    /// nao repetir <c>svn list -R</c>; null faz a consulta na hora.
    /// </param>
    public async Task ApplyTreeAsync(string wcPath, string branchUrl, long rev, IReadOnlyList<ImportFile> changes,
        HashSet<string>? repoFiles = null)
    {
        var requested = repoFiles is not null;
        repoFiles ??= new HashSet<string>(
            await svn.ListFilesAsync(At(branchUrl, rev)), StringComparer.Ordinal);
        var deleted = changes.Where(c => c.Content is null).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        // pastas que perdem todos os arquivos saem numa unica revisao (um svn delete por pasta, nao por arquivo)
        var wholeDirs = WholeDeletedDirs(repoFiles, deleted);
        var inWholeDir = (string p) => wholeDirs.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal));

        await FetchAsync(wcPath, branchUrl, rev, changes, repoFiles, wholeDirs, inWholeDir);

        var adds = new List<string>();
        foreach (var change in changes)
        {
            if (change.Content is null) continue;
            var full = Full(wcPath, change.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, change.Content);
            adds.Add(full);
        }

        // "@" no fim: sem ele o svn le "logo@2x.png" como caminho + revisao peg
        var deletes = changes
            .Where(c => c.Content is null && !inWholeDir(c.Path) && repoFiles.Contains(c.Path))
            .Select(c => Full(wcPath, c.Path)).Where(File.Exists).ToList();
        deletes.AddRange(wholeDirs.Select(d => Full(wcPath, d)).Where(Directory.Exists));
        foreach (var batch in deletes.Chunk(FileChunk))
            await MutateAsync(wcPath, new[] { "delete", "-q", "--force" }.Concat(batch.Select(Peg)));
        foreach (var batch in adds.Chunk(FileChunk))
            await MutateAsync(wcPath, new[] { "add", "-q", "--parents", "--force" }.Concat(batch.Select(Peg)));

        // Git nao guarda pasta vazia: pasta que ficou vazia sai do SVN tambem
        var root = Path.GetFullPath(wcPath).TrimEnd(Path.DirectorySeparatorChar);
        var remaining = repoFiles.Where(p => !deleted.Contains(p)).ToHashSet(StringComparer.Ordinal);
        var dirs = deletes.Select(d => Path.GetDirectoryName(d)!).Distinct()
            .SelectMany(d => Ancestors(root, d)).Distinct().OrderByDescending(d => d.Length);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) continue;
            // pasta vazia na WC rasa pode ter arquivos no SVN que so nao foram baixados
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (remaining.Any(p => p.StartsWith(rel + "/", StringComparison.Ordinal))) continue;
            await MutateAsync(wcPath, new[] { "delete", "-q", "--force", Peg(dir) });
        }

        if (requested)
            foreach (var change in changes)
                if (change.Content is null) repoFiles.Remove(change.Path);
                else repoFiles.Add(change.Path);
    }

    /// <summary>
    /// Avanca a revisao das pastas tocadas, sem baixar conteudo: o commit seguinte nao fica "out of date"
    /// por a pasta ter mudado de revisao no servidor.
    /// </summary>
    public async Task RefreshAsync(string wcPath, long rev, IReadOnlyList<ImportFile> changes)
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal) { wcPath };
        foreach (var change in changes)
            foreach (var dir in AncestorDirs(change.Path))
                dirs.Add(Full(wcPath, dir));
        await svn.RunAsync(new[] { "update", "-q", "--non-interactive", "-r", Num(rev), "--depth", "empty", "--parents" }
            .Concat(dirs.Select(Peg)));
    }

    /// <summary>Baixa as pastas afetadas (vazias) e os arquivos que faltam para aplicar <paramref name="changes"/>.</summary>
    async Task FetchAsync(string wcPath, string branchUrl, long rev, IReadOnlyList<ImportFile> changes,
        HashSet<string> repoFiles, IReadOnlyList<string> wholeDirs, Func<string, bool> inWholeDir)
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>();
        var onDisk = new List<string>();
        foreach (var change in changes)
        {
            if (!inWholeDir(change.Path) && repoFiles.Contains(change.Path))
            {
                if (File.Exists(Full(wcPath, change.Path))) onDisk.Add(change.Path);
                else files.Add(change.Path);
            }
            // pasta que ainda nao existe no disco e ja existe no SVN entra versionada, com profundidade vazia;
            // pasta nova (sem nada no SVN) fica para o "svn add --parents"
            foreach (var dir in AncestorDirs(change.Path))
                if (ExistsInRepo(dir, repoFiles) && !Directory.Exists(Full(wcPath, dir)))
                    dirs.Add(dir);
        }
        foreach (var dir in wholeDirs)
            if (!Directory.Exists(Full(wcPath, dir)))
                dirs.Add(dir);

        await UpdateAsync(rev, "empty", dirs.OrderBy(d => d.Length).Select(d => Full(wcPath, d)), DirChunk);
        await UpdateAsync(rev, null, files.Select(f => Full(wcPath, f)), FileChunk);
        await RefreshBasesAsync(wcPath, rev, onDisk, changes, repoFiles);
    }

    /// <summary>
    /// Alinha a revisao base dos arquivos que ja estao no disco com <paramref name="rev"/>. A WC rasa guarda o arquivo
    /// baixado num push anterior: se o SVN mudou o mesmo arquivo depois disso, commitar por cima sairia "out of date"
    /// mesmo com o Git ja atualizado (o fetch nao toca na WC).
    /// </summary>
    async Task RefreshBasesAsync(string wcPath, long rev, List<string> onDisk, IReadOnlyList<ImportFile> changes,
        HashSet<string> repoFiles)
    {
        if (onDisk.Count == 0) return;
        try
        {
            await UpdateAsync(rev, null, onDisk.Select(f => Full(wcPath, f)), FileChunk);
            return;
        }
        catch (SvnException) { }

        // um dos caminhos nao existe mais nessa revisao: isola os demais um a um
        foreach (var rel in onDisk)
        {
            var full = Full(wcPath, rel);
            try
            {
                await svn.RunAsync(new[] { "update", "-q", "--non-interactive", "-r", Num(rev), Peg(full) });
            }
            catch (SvnException)
            {
                // apagado no SVN depois do ultimo download: nao ha base para atualizar. Conteudo novo entra como
                // replace (delete + add); caminho que o commit so apagaria ja nao existe no servidor.
                repoFiles.Remove(rel);
                if (changes.Any(c => c.Path == rel && c.Content is not null))
                    await MutateAsync(wcPath, new[] { "delete", "-q", "--force", Peg(full) });
            }
        }
    }

    async Task UpdateAsync(long rev, string? depth, IEnumerable<string> targets, int chunk)
    {
        var args = new List<string> { "update", "-q", "--non-interactive", "-r", Num(rev), "--parents" };
        if (depth is not null) args.AddRange(new[] { "--set-depth", depth });
        foreach (var batch in targets.Chunk(chunk))
            await svn.RunAsync(args.Concat(batch.Select(Peg)));
    }

    /// <summary>Commita o working copy; devolve a revisao criada ou 0 quando nao havia alteracao.</summary>
    public async Task<long> CommitAsync(string wcPath, string message)
    {
        // marcador de operacao em andamento: se o processo morrer, o proximo push roda cleanup
        var busy = BusyPath(wcPath);
        File.WriteAllText(busy, "");
        // mensagem por arquivo UTF-8: preserva acentos e quebras de linha independente da codepage
        var msgFile = Path.GetTempFileName();
        string output;
        try
        {
            await File.WriteAllTextAsync(msgFile, message, new UTF8Encoding(false));
            output = await svn.RunAsync(new[] { "commit", "--non-interactive", "-F", msgFile, "--encoding", "UTF-8", wcPath });
        }
        finally { File.Delete(msgFile); }
        File.Delete(busy);
        // "Committed revision N." vem traduzido conforme o idioma do sistema: vale o ultimo numero da ultima
        // linha que tem numero (as linhas anteriores sao "Sending <arquivo>" / "Transmitting file data")
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => Regex.IsMatch(l, @"\d"));
        if (last is null) return 0;
        return long.Parse(Regex.Matches(last, @"\d+")[^1].Value);
    }

    public Task SetMergeInfoAsync(string wcPath, string value) =>
        svn.RunAsync(new[] { "propset", "-q", "svn:mergeinfo", value, wcPath });

    /// <summary>Descarta alteracoes locais e arquivos nao versionados.</summary>
    public async Task RevertAsync(string wcPath)
    {
        await svn.RunAsync(new[] { "revert", "-q", "-R", wcPath });
        await svn.RunAsync(new[] { "cleanup", "--remove-unversioned", "--remove-ignored", wcPath });
    }

    /// <summary>Operacao que altera a WC: deixa o marcador caso o processo morra no meio.</summary>
    async Task MutateAsync(string wcPath, IEnumerable<string> args)
    {
        var busy = BusyPath(wcPath);
        File.WriteAllText(busy, "");
        var ok = false;
        try { await svn.RunAsync(args); ok = true; }
        finally { if (ok) File.Delete(busy); }
    }

    static string BusyPath(string wcPath) => wcPath + ".busy";

    static string At(string url, long rev) => url.LastIndexOf('@') > url.LastIndexOf('/') ? url : $"{url}@{Num(rev)}";

    static string Num(long rev) => rev.ToString(CultureInfo.InvariantCulture);

    static string Full(string wcPath, string relative) =>
        Path.Combine(wcPath, relative.Replace('/', Path.DirectorySeparatorChar));

    static bool ExistsInRepo(string relativeDir, IReadOnlySet<string> repoFiles)
    {
        var prefix = relativeDir + "/";
        return repoFiles.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Pastas (relativas com '/') que perdem todos os arquivos versionados nas alteracoes.</summary>
    static List<string> WholeDeletedDirs(IReadOnlySet<string> repoFiles, HashSet<string> deleted)
    {
        var result = new List<string>();
        if (deleted.Count == 0) return result;
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in deleted)
            foreach (var dir in AncestorDirs(file))
                candidates.Add(dir);
        foreach (var dir in candidates.OrderBy(d => d.Length))
        {
            if (result.Any(w => dir.StartsWith(w + "/", StringComparison.Ordinal))) continue;
            var prefix = dir + "/";
            var under = repoFiles.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (under.Count == 0 || !under.All(deleted.Contains)) continue;
            result.Add(dir);
        }
        return result;
    }

    /// <summary>Pastas relativas acima de <paramref name="relativeFilePath"/>, da imediata ate a raiz.</summary>
    static IEnumerable<string> AncestorDirs(string relativeFilePath)
    {
        var slash = relativeFilePath.LastIndexOf('/');
        for (var end = slash; end > 0; end = relativeFilePath.LastIndexOf('/', end - 1))
            yield return relativeFilePath[..end];
    }

    static IEnumerable<string> Ancestors(string root, string dir)
    {
        for (var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
             d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             d = Path.GetDirectoryName(d)!)
            yield return d;
    }

    static string Peg(string path) => path + "@";

    static string DirName(string branchUrl)
    {
        var tail = branchUrl.TrimEnd('/').Split('/')[^1];
        var safe = Regex.Replace(Uri.UnescapeDataString(tail), @"[^\w.\-]", "_");
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(branchUrl)))[..8].ToLowerInvariant();
        return $"{safe}-{hash}";
    }
}
