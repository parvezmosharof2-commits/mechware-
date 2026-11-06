using ShopVerse.Application.Catalog;

namespace ShopVerse.Infrastructure.Documents;

/// <summary>
/// Keeps files in a directory. Keys are made by the server from ids, and are checked anyway: only lower-case letters, digits,
/// '/', '-' and '.', no '..', and the resolved path must stay inside the root.
/// </summary>
public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;

    public LocalObjectStorage(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        string path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        string path = PathOf(key);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 200 || key.Contains("..", StringComparison.Ordinal) || key.StartsWith('/')
            || !key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '/' or '-' or '.'))
            throw new ArgumentException("Not a valid storage key.", nameof(key));
        string full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal)) throw new ArgumentException("Not a valid storage key.", nameof(key));
        return full;
    }
}
