namespace GitRemoteSvn.Update;

/// <summary>
/// Configuracao da atualizacao. O ambiente sempre vence; o <c>git config</c> so entra na checagem automatica.
/// </summary>
public sealed record UpdateOptions(string Repo, string ApiBase, bool Quiet)
{
    public const string DefaultRepo = "WenD3v/git-remote-svn";
    public const string DefaultApiBase = "https://api.github.com";

    public static UpdateOptions FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var repo = getEnv("GIT_REMOTE_SVN_UPDATE_REPO");
        var api = getEnv("GIT_REMOTE_SVN_UPDATE_API");
        return new UpdateOptions(
            string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim(),
            (string.IsNullOrWhiteSpace(api) ? DefaultApiBase : api.Trim()).TrimEnd('/'),
            IsTrue(getEnv("GIT_REMOTE_SVN_QUIET")));
    }

    /// <summary>Liga a instalacao automatica de versao nova (opt-in por ambiente ou <c>remote-svn.autoUpdate</c>).</summary>
    public static bool AutoUpdateRequested(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        return IsTrue(getEnv("GIT_REMOTE_SVN_AUTO_UPDATE"));
    }

    static bool IsTrue(string? value) =>
        value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
