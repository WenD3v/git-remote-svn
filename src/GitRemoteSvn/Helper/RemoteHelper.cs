using GitRemoteSvn.Git;
using GitRemoteSvn.Svn;
using GitRemoteSvn.Sync;

namespace GitRemoteSvn.Helper;

/// <summary>
/// Protocolo de remote helper do Git (gitremote-helpers). O historico SVN e importado em refs
/// privadas <c>refs/svn/&lt;remote&gt;/heads/*</c>; o Git atualiza <c>refs/remotes/&lt;remote&gt;/*</c>
/// a partir do que o <c>list</c> anuncia.
/// </summary>
public class RemoteHelper(SvnClient svn, AuthorMap authors, GitClient git, string remote = "origin")
{
    bool _imported;
    bool _dryRun;
    ProjectLayout? _layout;
    SyncLog? _log;
    readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _tracking = new(StringComparer.Ordinal);

    public string PrivateRef(string branch) => $"refs/svn/{remote}/heads/{branch}";

    public async Task RunAsync(string url, TextReader input, TextWriter output)
    {
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) break;
            var cmd = line.Split(' ', 2)[0];
            switch (cmd)
            {
                case "capabilities":
                    await WriteAsync(output, "fetch", "push", "option", "");
                    break;
                case "option":
                    var opt = line.Split(' ');
                    if (opt.Length >= 3 && opt[1] == "dry-run")
                    {
                        _dryRun = opt[2] == "true";
                        await WriteAsync(output, "ok");
                    }
                    else await WriteAsync(output, "unsupported");
                    break;
                case "list":
                    await EnsureImportedAsync(url);
                    var lines = _refs.Select(r => $"{r.Value} {r.Key}").ToList();
                    if (_refs.ContainsKey("refs/heads/main")) lines.Add("@refs/heads/main HEAD");
                    lines.Add("");
                    await WriteAsync(output, lines.ToArray());
                    break;
                case "fetch":
                    // os objetos ja estao no repositorio desde o list; consome o lote ate a linha vazia
                    while (await input.ReadLineAsync() is { Length: > 0 }) { }
                    await WriteAsync(output, "");
                    break;
                case "push":
                    var pushes = new List<string> { line };
                    while (await input.ReadLineAsync() is { Length: > 0 } p) pushes.Add(p);
                    var status = new List<string>();
                    foreach (var p in pushes) status.Add(await PushOneAsync(url, p.Substring(5)));
                    status.Add("");
                    await WriteAsync(output, status.ToArray());
                    break;
                default:
                    throw new InvalidOperationException($"comando do protocolo desconhecido: {line}");
            }
        }
        await FixTrackingRefsAsync();
    }

    /// <summary>Um item de push <c>[+]src:dst</c>; devolve a linha de status do protocolo.</summary>
    async Task<string> PushOneAsync(string url, string spec)
    {
        spec = spec.TrimStart('+');
        var colon = spec.LastIndexOf(':');
        var (src, dst) = (spec[..colon], spec[(colon + 1)..]);
        if (src.Length == 0) return $"error {dst} apagar branch/tag no SVN pelo git nao e suportado";
        var isTag = dst.StartsWith("refs/tags/", StringComparison.Ordinal);
        if (!isTag && !dst.StartsWith("refs/heads/", StringComparison.Ordinal)) return $"error {dst} so branches e tags sao publicadas no SVN";

        await EnsureImportedAsync(url);
        if (!_layout!.Standard && dst != "refs/heads/main")
            return $"error {dst} o projeto nao tem trunk/branches/tags; so a main pode ser publicada";
        var state = new StateStore(git.GitDir);
        _log ??= new SyncLog(git.GitDir);
        var pusher = new Pusher(svn, git, new SvnWorkingCopy(svn, Path.Combine(state.Root, "wc")), state) { Log = _log };
        _log.Write($"push {src} -> {dst}{(_dryRun ? " (dry-run)" : "")}");
        try
        {
            if (isTag)
            {
                var tag = dst.Substring("refs/tags/".Length);
                var sha = await git.RevParseAsync(src);
                var tagResult = await pusher.PushTagAsync(tag, sha, SvnClient.JoinUrl(_layout.TagsUrl, tag),
                    $"refs/svn/{remote}/tags/{tag}", _dryRun);
                foreach (var w in tagResult.Warnings) await Console.Error.WriteLineAsync($"git-remote-svn: {w}");
                return $"ok {dst}";
            }

            var branch = dst.Substring("refs/heads/".Length);
            var remoteRef = PrivateRef(branch);
            var branchUrl = branch == "main" ? _layout.TrunkUrl : SvnClient.JoinUrl(_layout.BranchesUrl, branch);
            // main sem ref = trunk ainda vazio: recebe o primeiro push direto, sem svn copy
            var isNew = branch != "main" && !await git.RefExistsAsync(remoteRef);
            var result = isNew
                ? await pusher.PushNewBranchAsync(src, branchUrl, remoteRef, _dryRun)
                : await pusher.PushBranchAsync(src, branchUrl, remoteRef, _dryRun);
            foreach (var w in result.Warnings) await Console.Error.WriteLineAsync($"git-remote-svn: {w}");
            if (!_dryRun)
            {
                if (isNew)
                {
                    await Console.Error.WriteLineAsync($"git-remote-svn: branch {branch} criada em {branchUrl}");
                    await git.ConfigSetAsync($"branch.{branch}.rebase", "true");
                }
                await Console.Error.WriteLineAsync($"git-remote-svn: {result.RevisionsCreated} revisao(oes) criada(s) em {branchUrl}");
                if (result.RemoteHead is not null) _tracking[$"refs/remotes/{remote}/{branch}"] = result.RemoteHead;
            }
            return $"ok {dst}";
        }
        catch (Exception ex) when (ex is PushRejectedException or SvnException)
        {
            _log.Write($"recusado: {ex.Message}");
            await Console.Error.WriteLineAsync($"git-remote-svn: detalhes em {_log.FilePath}");
            return $"error {dst} {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}";
        }
    }

    /// <summary>
    /// Apos um push o Git aponta refs/remotes para o commit original; o SVN tem o commit espelho (com svn-id).
    /// Roda quando o Git encerra a sessao, depois de ele ter gravado as refs de rastreamento.
    /// </summary>
    async Task FixTrackingRefsAsync()
    {
        foreach (var (refName, sha) in _tracking)
            await git.UpdateRefAsync(refName, sha);
        _tracking.Clear();
    }

    async Task EnsureImportedAsync(string url)
    {
        if (_imported) return;
        var state = new StateStore(git.GitDir);
        var layout = _layout = state.LoadLayout() ?? await ProjectLayout.DiscoverAsync(svn, url);
        await LoadRefsAsync();
        // nada mudou no repositorio desde a ultima importacao completa: as refs locais ja estao em dia e
        // consultar branch por branch so custaria uma dezena de processos por sessao
        var head = await svn.InfoAsync(layout.RootUrl);
        if (state.GetImportedHead() is { } done && done.Uuid == head.Uuid && done.Revision == head.Revision &&
            _refs.ContainsKey("refs/heads/main"))
        {
            _imported = true;
            return;
        }

        var importer = new RevisionImporter(svn, git, state, authors);
        var target = PrivateRef("main");
        await importer.ImportTrunkAsync(layout, target);
        await git.ConfigSetAsync("branch.main.rebase", "true");
        // vale tambem para branches criadas a partir de origin/<x>: o push recusa historico com merge do pull
        await git.ConfigSetAsync("pull.rebase", "true");
        // branch apagada no SVN tambem some de refs/remotes no proximo fetch
        await git.ConfigSetAsync($"remote.{remote}.prune", "true");
        await importer.ImportBranchesAndTagsAsync(layout, $"refs/svn/{remote}");
        state.SetImportedHead(head.Uuid, head.Revision);
        await LoadRefsAsync();
        _imported = true;
    }

    /// <summary>Refs privadas ja existentes no repositorio, como anunciadas no protocolo.</summary>
    async Task LoadRefsAsync()
    {
        var prefix = $"refs/svn/{remote}/";
        _refs.Clear();
        var refs = await git.RunAsync("for-each-ref", "--format=%(objectname) %(refname)", prefix);
        foreach (var l in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var (sha, name) = (l.Substring(0, l.IndexOf(' ')), l.Substring(l.IndexOf(' ') + 1));
            var rest = name.Substring(prefix.Length);
            if (rest.StartsWith("heads/") || rest.StartsWith("tags/")) _refs["refs/" + rest] = sha;
        }
    }

    static async Task WriteAsync(TextWriter output, params string[] lines)
    {
        foreach (var l in lines) await output.WriteAsync(l + "\n");
        await output.FlushAsync();
    }
}
