using Microsoft.AspNetCore.Components;

namespace Klassenbibliothek.Services;

/// <summary>
/// Represents a single active floating overlay (dropdown, popover, etc.).
/// </summary>
public sealed class FloatingItem
{
    public Guid Id { get; init; }
    public RenderFragment Content { get; init; } = null!;
}

/// <summary>
/// Scoped service that manages global floating overlays.
/// Allows components to render dropdown/popover content outside their normal DOM position,
/// avoiding clipping caused by overflow:hidden on parent containers (e.g. FluentTabs).
/// </summary>
public sealed class FloatingLayerService
{
    private readonly List<FloatingItem> _items = new();
    private int _hostCount;

    /// <summary>Active floating items to be rendered by FloatingLayerHost.</summary>
    public IReadOnlyList<FloatingItem> Items => _items.AsReadOnly();

    /// <summary>True when a FloatingLayerHost is available in the current interactive render scope.</summary>
    public bool HasHost => _hostCount > 0;

    /// <summary>Fired whenever the item list changes so FloatingLayerHost can re-render.</summary>
    public event Action? OnChanged;

    public void RegisterHost()
    {
        _hostCount++;
    }

    public void UnregisterHost()
    {
        _hostCount = Math.Max(0, _hostCount - 1);
    }

    /// <summary>
    /// Adds a floating overlay and returns its ID.
    /// </summary>
    public Guid Show(RenderFragment content)
    {
        var item = new FloatingItem { Id = Guid.NewGuid(), Content = content };
        _items.Add(item);
        OnChanged?.Invoke();
        return item.Id;
    }

    /// <summary>Removes the overlay with the given ID.</summary>
    public void Hide(Guid id)
    {
        _items.RemoveAll(x => x.Id == id);
        OnChanged?.Invoke();
    }

    /// <summary>Removes all active overlays.</summary>
    public void HideAll()
    {
        _items.Clear();
        OnChanged?.Invoke();
    }
}
