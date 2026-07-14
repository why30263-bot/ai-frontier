using System.Collections.Concurrent;
using System.Text.Json;
using AIFrontier.Models;

namespace AIFrontier.Services;

/// <summary>
/// Persists the last known good edition without exposing partial JSON to readers.
/// Storage failures are deliberately reported as false/null instead of escaping into
/// the reading path.
/// </summary>
public sealed class QualifiedEditionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathWriteGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly EditionQualityPolicy _policy;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeGate;

    public QualifiedEditionStore(
        string path,
        EditionQualityPolicy policy,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
        _writeGate = PathWriteGates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public string Path => _path;

    public async Task<NewsEdition?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            var edition = await JsonSerializer.DeserializeAsync<NewsEdition>(
                stream,
                _jsonOptions,
                cancellationToken);
            return _policy.IsQualified(edition) ? edition : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SaveAsync(
        NewsEdition edition,
        CancellationToken cancellationToken = default)
    {
        if (!_policy.IsQualified(edition))
        {
            return false;
        }

        var lockTaken = false;
        string? temporaryPath = null;
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            lockTaken = true;

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, edition, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            temporaryPath = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Cleanup is best-effort and must not hide the original result.
                }
            }

            if (lockTaken)
            {
                _writeGate.Release();
            }
        }
    }
}
