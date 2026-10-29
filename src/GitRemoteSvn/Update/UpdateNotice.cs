namespace GitRemoteSvn.Update;

/// <summary>
/// Checagem que roda ao final de uma sessao do helper: no maximo uma consulta ao GitHub por dia (cache de
/// 24h), aviso de uma linha no stderr e, quando ligada, a auto-instalacao. Toda falha e silenciosa: o
/// resultado do fetch/push nunca depende dela.
/// </summary>
public static class UpdateNotice
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <param name="autoUpdate">Consultada so quando ha versao nova; le o git config apenas nesse caso.</param>
    /// <returns>A linha a imprimir no stderr, ou <c>null</c> quando nao ha nada a dizer.</returns>
    public static async Task<string?> CheckAndApplyAsync(UpdateOptions options, HttpClient http, string exePath,
        UpdateCache cache, Func<Task<bool>> autoUpdate, CancellationToken ct = default)
    {
        try
        {
            var svc = new UpdateService(options, http, exePath);
            var entry = cache.Read();
            ReleaseInfo? release = null;
            string? versao;

            if (entry is not null && cache.IsFresh(entry, Ttl))
            {
                versao = entry.LatestVersion;
                if (versao is null || !VersionUtil.IsNewer(versao, VersionUtil.Current)) return null;
            }
            else
            {
                release = await svc.CheckAsync(ct);
                if (release is null)
                {
                    // offline/limite do GitHub tambem fica em cache: nao vira uma tentativa a cada fetch
                    cache.Write(new UpdateCacheEntry(DateTimeOffset.UtcNow, null, null));
                    return null;
                }
                var nova = VersionUtil.IsNewer(release.Version, VersionUtil.Current);
                versao = nova ? release.Version : null;
                cache.Write(new UpdateCacheEntry(DateTimeOffset.UtcNow, versao, null));
                if (!nova) return null;
            }

            if (await autoUpdate())
            {
                release ??= await svc.CheckAsync(ct);
                if (release is null) return null;
                var result = await svc.UpdateAsync(release, ct);
                return result.Updated
                    ? $"git-remote-svn: atualizado para v{release.Version} (novas sessoes usam a versao nova)"
                    : $"git-remote-svn: {result.Message}";
            }

            return options.Quiet ? null : $"git-remote-svn: nova versao v{versao} disponivel (git-remote-svn update)";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
