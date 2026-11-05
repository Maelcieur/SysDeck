using System.Windows;
using System.Windows.Controls;
using SysDeck.Models;

namespace SysDeck;

public abstract class TileEditorWindow : Window
{
    protected TileEditorWindow(Window? owner = null)
    {
        if (owner is not null) Owner = owner;
    }

    public ShortcutItem? Result { get; protected set; }
    public event Action<bool>? Completed;
    public event Action<IconChooserWindow, Action<IconChoice?>>? IconChooserRequested;
    protected bool IsEmbedded { get; private set; }
    protected Window DialogOwner => IsEmbedded ? Owner ?? this : this;

    public void AttachTo(ContentControl host)
    {
        if (Content is not UIElement content)
            throw new InvalidOperationException("The tile editor has no content to display.");

        IsEmbedded = true;
        Content = null;
        host.Content = content;
        OnAttached();
    }

    protected virtual void OnAttached()
    {
    }

    public void CancelInline() => Complete(false);

    protected bool ChooseIconInline(IconChooserWindow chooser, Action<IconChoice> chosen)
    {
        if (!IsEmbedded || IconChooserRequested is null) return false;
        IconChooserRequested(chooser, choice =>
        {
            if (choice is not null) chosen(choice);
        });
        return true;
    }

    protected void Complete(bool accepted)
    {
        if (!IsEmbedded)
        {
            DialogResult = accepted;
            return;
        }

        Completed?.Invoke(accepted);
    }
}
