using System.Text.Json;

namespace RegWatch.Core;

/// <summary>Which digests have been opened, in &lt;root&gt;\read.json.</summary>
public sealed class ReadMarks
{
    private readonly string _path;
    private readonly HashSet<string> _read;

    /// <param name="existingDigests">
    /// Digests already present. The first time (no read.json yet) they all count as read, so
    /// only digests written from then on show as unread.
    /// </param>
    public ReadMarks(string root, IEnumerable<string> existingDigests)
    {
        _path = Path.Combine(root, "read.json");
        if (File.Exists(_path))
        {
            try
            {
                _read = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path)) ?? [];
                return;
            }
            catch (JsonException)
            {
                // Fall through and start again from the digests present now.
            }
        }

        _read = [.. existingDigests];
        Save();
    }

    public bool IsRead(string digest) => _read.Contains(digest);

    public void MarkRead(string digest)
    {
        if (_read.Add(digest)) Save();
    }

    public void MarkAllRead(IEnumerable<string> digests)
    {
        var before = _read.Count;
        _read.UnionWith(digests);
        if (_read.Count != before) Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_read.Order().ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read marks are a convenience; a locked or read-only folder must not break browsing.
        }
    }
}
