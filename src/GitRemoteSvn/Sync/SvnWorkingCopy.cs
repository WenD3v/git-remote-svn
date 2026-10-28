using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;

namespace GitRemoteSvn.Sync;

/// <summary>Working copies SVN temporarias (uma por branch, em <c>.git/svn/wc</c>) usadas para publicar commits.</summary>
public class SvnWorkingCopy(SvnClient svn, string rootDir)
{
    const int Chunk = 100;

    /// <summary>Faz checkout (ou update) de <paramref name="branchUrl"/> na revisao pedida e devolve o caminho.</summary>
    public async Task<string> EnsureAsync(string branchUrl, long rev)
    {
        var path = Path.Combine(rootDir, DirName(branchUrl));
        if (Directory.Exists(Path.Combine(path, ".svn")))
        {
            // queda no meio de um svn commit/update deixa a WC travada; cleanup a destrava antes do uso
            await svn.RunAsync(new[] { "cleanup", path });
            await svn.RunAsync(new[] { "update", "-q", "--non-interactive", "-r", rev.ToString(), path });
        }
        else
        {
            Directory.CreateDirectory(rootDir);
            await svn.RunAsync(new[] { "checkout", "-q", "--non-interactive", $"{branchUrl}@{rev}", path });
        }
        return path;
    }

    /// <summary>Aplica as alteracoes (conteudo novo ou null = apagar) no working copy, mantendo o versionamento.</summary>
    public async Task ApplyTreeAsync(string wcPath, IReadOnlyList<ImportFile> changes)
    {
        var deletes = new List<string>();
        var adds = new List<string>();
        foreach (var change in changes)
        {
            var full = Path.Combine(wcPath, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (change.Content is null)
            {
                if (File.Exists(full)) deletes.Add(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, change.Content);
            adds.Add(full);
        }

        // "@" no fim: sem ele o svn le "logo@2x.png" como caminho + revisao peg
        foreach (var batch in deletes.Chunk(Chunk))
            await svn.RunAsync(new[] { "delete", "-q", "--force" }.Concat(batch.Select(Peg)));
        foreach (var batch in adds.Chunk(Chunk))
            await svn.RunAsync(new[] { "add", "-q", "--parents", "--force" }.Concat(batch.Select(Peg)));

        // Git nao guarda pasta vazia: pasta que ficou vazia sai do SVN tambem
        var root = Path.GetFullPath(wcPath).TrimEnd(Path.DirectorySeparatorChar);
        var dirs = deletes.Select(d => Path.GetDirectoryName(d)!).Distinct()
            .SelectMany(Ancestors).Distinct().OrderByDescending(d => d.Length);
        foreach (var dir in dirs)
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                await svn.RunAsync(new[] { "delete", "-q", "--force", Peg(dir) });
        }

        IEnumerable<string> Ancestors(string dir)
        {
            for (var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                 d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                 d = Path.GetDirectoryName(d)!)
                yield return d;
        }
    }

    /// <summary>Commita o working copy; devolve a revisao criada ou 0 quando nao havia alteracao.</summary>
    public async Task<long> CommitAsync(string wcPath, string message)
    {
        // mensagem por arquivo UTF-8: preserva acentos e quebras de linha independente da codepage
        var msgFile = Path.GetTempFileName();
        string output;
        try
        {
            await File.WriteAllTextAsync(msgFile, message, new UTF8Encoding(false));
            output = await svn.RunAsync(new[] { "commit", "--non-interactive", "-F", msgFile, "--encoding", "UTF-8", wcPath });
        }
        finally { File.Delete(msgFile); }
        // "Committed revision N." vem traduzido conforme o idioma do sistema: vale o ultimo numero da ultima
        // linha que tem numero (as linhas anteriores sao "Sending <arquivo>" / "Transmitting file data")
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => Regex.IsMatch(l, @"\d"));
        if (last is null) return 0;
        return long.Parse(Regex.Matches(last, @"\d+")[^1].Value);
    }

    static string Peg(string path) => path + "@";

    public Task SetMergeInfoAsync(string wcPath, string value) =>
        svn.RunAsync(new[] { "propset", "-q", "svn:mergeinfo", value, wcPath });

    /// <summary>Descarta alteracoes locais e arquivos nao versionados.</summary>
    public async Task RevertAsync(string wcPath)
    {
        await svn.RunAsync(new[] { "revert", "-q", "-R", wcPath });
        await svn.RunAsync(new[] { "cleanup", "--remove-unversioned", "--remove-ignored", wcPath });
    }

    static string DirName(string branchUrl)
    {
        var tail = branchUrl.TrimEnd('/').Split('/')[^1];
        var safe = Regex.Replace(Uri.UnescapeDataString(tail), @"[^\w.\-]", "_");
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(branchUrl)))[..8].ToLowerInvariant();
        return $"{safe}-{hash}";
    }
}
