using System.IO;
using System.Reflection;
using SysDeck.Infrastructure;

namespace SysDeck;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _layoutEditItem;
    private readonly ContextMenuStrip _menu;
    private readonly Icon? _ownedIcon; // null si on utilise une icône système partagée, qu'il ne faut pas libérer

    // Les services (ex. correction de texte) n'ont pas de référence vers l'icône : accès statique pour les notifications.
    private static TrayIconService? _current;

    /// <summary>Affiche une bulle discrète de la zone de notification (sans effet si l'icône n'existe pas encore).</summary>
    public static void ShowNotice(string message)
    {
        try
        {
            _current?._notifyIcon.ShowBalloonTip(2500, "SysDeck", message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("tray-notice", "Notification de la zone de notification impossible", ex);
        }
    }

    /// <summary>Coche l'entrée de menu du mode édition de la disposition.</summary>
    public void SetLayoutEditChecked(bool enabled) => _layoutEditItem.Checked = enabled;

    public event Action? SettingsRequested;
    public event Action? LayoutEditToggleRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        Icon icon;
        Icon? owned = null;
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var bundledIconPath = Path.Combine(baseDir, "Assets", "AppIcon.ico");

            owned = File.Exists(bundledIconPath)
                ? new Icon(bundledIconPath)
                : Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            icon = owned ?? SystemIcons.Application;
        }
        catch
        {
            icon = SystemIcons.Application;
        }
        _ownedIcon = owned;

        var menu = _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = System.Drawing.Color.FromArgb(0x20, 0x21, 0x26),
            ForeColor = System.Drawing.Color.FromArgb(0xE7, 0xE8, 0xEB),
            Font = new Font("Segoe UI", 9.5f)
        };
        menu.Items.Add("Paramètres", null, (_, _) => SettingsRequested?.Invoke());
        _layoutEditItem = new ToolStripMenuItem("Modifier la disposition des modules", null, (_, _) => LayoutEditToggleRequested?.Invoke());
        menu.Items.Add(_layoutEditItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "SysDeck",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseDoubleClick += (_, _) => SettingsRequested?.Invoke();
        _current = this;
    }

    /// <summary>Palette sombre et plate pour le menu de l'icône de notification (WinForms).</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = System.Drawing.Color.FromArgb(0xE7, 0xE8, 0xEB);
            base.OnRenderItemText(e);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x20, 0x21, 0x26);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3A, 0x3B, 0x42);
            private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x38, 0x3A, 0x42);

            public DarkColors() { UseSystemColors = false; }
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Border;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color SeparatorDark => Border;
            public override System.Drawing.Color SeparatorLight => Border;
            public override System.Drawing.Color CheckBackground => Hover;
            public override System.Drawing.Color CheckSelectedBackground => Hover;
            public override System.Drawing.Color CheckPressedBackground => Hover;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
