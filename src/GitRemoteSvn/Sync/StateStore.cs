using System.Text.Json;

namespace GitRemoteSvn.Sync;

public class StateStore
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    readonly string _file;
    StateData _data;

    public StateStore(string gitDir)
    {
        Root = Path.Combine(gitDir, "svn");
        Directory.CreateDirectory(Root);
        _file = Path.Combine(Root, "state.json");
        _data = File.Exists(_file)
            ? JsonSerializer.Deserialize<StateData>(File.ReadAllText(_file), JsonOpts) ?? new StateData()
            : new StateData();
    }

    public string Root { get; }

    public long? GetLastRev(string refName) => _data.LastRevs.TryGetValue(refName, out var r) ? r : null;

    public void SetLastRev(string refName, long rev)
    {
        _data.LastRevs[refName] = rev;
        Save();
    }

    public void RemoveLastRev(string refName)
    {
        if (_data.LastRevs.Remove(refName)) Save();
    }

    public IReadOnlyList<string> RefsWithPrefix(string prefix) =>
        _data.LastRevs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Revisoes criadas por um push ainda nao concluido (gravadas logo apos cada svn commit). So elas podem ser
    /// adotadas na reexecucao; revisao de outra pessoa com a mesma mensagem continua sendo novidade do SVN.
    /// </summary>
    public IReadOnlyCollection<long> GetPushedRevs(string refName) =>
        _data.Pushed.TryGetValue(refName, out var r) ? r : Array.Empty<long>();

    public void AddPushedRev(string refName, long rev)
    {
        if (!_data.Pushed.TryGetValue(refName, out var list)) _data.Pushed[refName] = list = new List<long>();
        list.Add(rev);
        Save();
    }

    public void ClearPushedRevs(string refName)
    {
        if (_data.Pushed.Remove(refName)) Save();
    }

    public void SaveLayout(ProjectLayout layout)
    {
        _data.Layout = layout;
        Save();
    }

    public ProjectLayout? LoadLayout() => _data.Layout;

    void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOpts));
        File.Move(tmp, _file, true);
    }

    class StateData
    {
        public Dictionary<string, long> LastRevs { get; set; } = new();
        public Dictionary<string, List<long>> Pushed { get; set; } = new();
        public ProjectLayout? Layout { get; set; }
    }
}
