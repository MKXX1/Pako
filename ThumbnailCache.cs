using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using Veldrid;

namespace Pako;

public sealed class ThumbnailCache : IDisposable
{
    private sealed class Entry
    {
        public bool Requested;
        public bool Failed;
        public string Error = "";
        public Texture? Texture;
        public IntPtr Binding;
    }

    private readonly OPP_PakMount _assets;
    private readonly int _size;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Texture> _textures = new();
    private readonly ConcurrentQueue<CompletedThumbnail> _completed = new();
    private readonly SemaphoreSlim _workers = new(2, 2);

    public ThumbnailCache(OPP_PakMount assets, int size)
    {
        _assets = assets;
        _size = size;
    }

    public IntPtr? GetOrRequest(ModelItem model)
    {
        var entry = GetEntry(model.ObjectPath);
        if (entry.Binding != IntPtr.Zero) return entry.Binding;
        if (!entry.Requested && !entry.Failed)
        {
            entry.Requested = true;
            _ = Task.Run(() => GenerateAsync(model));
        }

        return null;
    }

    public bool IsPending(ModelItem model)
    {
        if (!_entries.TryGetValue(model.ObjectPath, out var entry)) return false;
        return entry.Requested && !entry.Failed && entry.Binding == IntPtr.Zero;
    }

    public string GetError(ModelItem model)
    {
        return _entries.TryGetValue(model.ObjectPath, out var entry) ? entry.Error : "";
    }

    public void PumpCompleted(GraphicsDevice graphicsDevice, ImGuiController controller)
    {
        var factory = graphicsDevice.ResourceFactory;
        var processed = 0;

        while (processed++ < 8 && _completed.TryDequeue(out var item))
        {
            var entry = GetEntry(item.ObjectPath);
            if (item.Rgba == null)
            {
                entry.Failed = true;
                entry.Error = item.Error;
                continue;
            }

            var texture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)_size,
                (uint)_size,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));
            graphicsDevice.UpdateTexture(texture, item.Rgba, 0, 0, 0, (uint)_size, (uint)_size, 1, 0, 0);

            entry.Texture = texture;
            _textures.Add(texture);
            entry.Binding = controller.GetOrCreateImGuiBinding(factory, texture);
            entry.Failed = false;
            entry.Error = "";
        }
    }

    public void Clear()
    {
        _entries.Clear();
        while (_completed.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        Clear();
        foreach (var texture in _textures)
            texture.Dispose();

        _textures.Clear();
        _workers.Dispose();
    }

    private Entry GetEntry(string objectPath)
    {
        if (_entries.TryGetValue(objectPath, out var entry)) return entry;

        entry = new Entry();
        _entries.Add(objectPath, entry);
        return entry;
    }

    private async Task GenerateAsync(ModelItem model)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        try
        {
            var rgba = _assets.CreateThumbnailRgba(model, _size, out var error);
            _completed.Enqueue(new CompletedThumbnail(model.ObjectPath, rgba, error));
        }
        catch (Exception ex)
        {
            _completed.Enqueue(new CompletedThumbnail(model.ObjectPath, null, ex.Message));
        }
        finally
        {
            _workers.Release();
        }
    }

    private readonly record struct CompletedThumbnail(string ObjectPath, byte[]? Rgba, string Error);
}
