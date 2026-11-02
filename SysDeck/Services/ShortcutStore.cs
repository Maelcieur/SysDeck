using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Services;

public sealed class ShortcutStore
{
    public List<ShortcutItem> Load()
        => JsonFile.Load(AppPaths.ShortcutsFile, () => new List<ShortcutItem>())
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path))
            .ToList();

    public void Save(IEnumerable<ShortcutItem> items) => JsonFile.Save(AppPaths.ShortcutsFile, items.ToList());
}
