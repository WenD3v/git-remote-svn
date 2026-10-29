namespace GitRemoteSvn.Svn;

public record SvnEntry(string Name, string Kind, long Revision);

public record SvnPathChange(char Action, string Path, string? CopyFromPath, long? CopyFromRev, string Kind);

public record SvnRevision(long Revision, string Author, DateTimeOffset Date, string Message,
    IReadOnlyList<SvnPathChange> Paths);

/// <param name="Revision">Revisao da URL consultada (normalmente HEAD).</param>
/// <param name="LastChangedRev">Ultima revisao que alterou esse caminho.</param>
public record SvnInfo(string Url, string RepoRootUrl, string Uuid, long Revision, long LastChangedRev);

public class SvnException(string message) : Exception(message);
