using System.Text;

namespace GitRemoteSvn.Sync;

/// <summary>Log de diagnostico de uma sessao, em <c>&lt;gitDir&gt;/svn/log/yyyyMMdd-HHmmss.log</c>.</summary>
public class SyncLog(string gitDir)
{
    readonly string _path = Path.Combine(gitDir, "svn", "log", $"{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public string FilePath => _path;

    public void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} {line}\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // log e diagnostico: nunca derruba o push
        }
    }
}
