namespace SysDeck.Services;

/// <summary>Alertes : chacune s'active à part, avec son seuil. Liées à la page Alertes des Paramètres.</summary>
public sealed class AlertSettings : ObservableSettings
{
    private bool _cpuEnabled;
    private double _cpuTemperature = 85;
    private bool _gpuEnabled;
    private double _gpuTemperature = 83;
    private bool _mouseEnabled = true;
    private double _mousePercent = 20;
    private bool _claudeSessionEnabled = true;
    private double _claudeSessionPercent = 80;
    private bool _claudeWeekEnabled;
    private double _claudeWeekPercent = 90;
    private bool _soundEnabled = true;
    private double _durationSeconds = 8;

    /// <summary>Température du processeur (°C) à partir de laquelle prévenir.</summary>
    public bool CpuEnabled { get => _cpuEnabled; set => Set(ref _cpuEnabled, value); }
    public double CpuTemperature { get => _cpuTemperature; set => Set(ref _cpuTemperature, value); }

    /// <summary>Température de la carte graphique (°C) à partir de laquelle prévenir.</summary>
    public bool GpuEnabled { get => _gpuEnabled; set => Set(ref _gpuEnabled, value); }
    public double GpuTemperature { get => _gpuTemperature; set => Set(ref _gpuTemperature, value); }

    /// <summary>Batterie de la souris (%) en dessous de laquelle prévenir (jamais pendant la charge).</summary>
    public bool MouseEnabled { get => _mouseEnabled; set => Set(ref _mouseEnabled, value); }
    public double MousePercent { get => _mousePercent; set => Set(ref _mousePercent, value); }

    /// <summary>Quota Claude de la session de 5 h (%) à partir duquel prévenir.</summary>
    public bool ClaudeSessionEnabled { get => _claudeSessionEnabled; set => Set(ref _claudeSessionEnabled, value); }
    public double ClaudeSessionPercent { get => _claudeSessionPercent; set => Set(ref _claudeSessionPercent, value); }

    /// <summary>Quota Claude de la semaine (%) à partir duquel prévenir.</summary>
    public bool ClaudeWeekEnabled { get => _claudeWeekEnabled; set => Set(ref _claudeWeekEnabled, value); }
    public double ClaudeWeekPercent { get => _claudeWeekPercent; set => Set(ref _claudeWeekPercent, value); }

    public bool SoundEnabled { get => _soundEnabled; set => Set(ref _soundEnabled, value); }

    /// <summary>Durée d'affichage d'une alerte avant qu'elle disparaisse seule (secondes).</summary>
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }

    public bool AnyClaude => ClaudeSessionEnabled || ClaudeWeekEnabled;

    // ---------- Apparence ----------

    private string _backgroundColor = "#FF1A1C27";
    private double _backgroundOpacity = 0.96;
    private double _cornerRadius = 18;
    private bool _autoAccent = true;
    private string _accentColor = "#FF00E0FF";
    private bool _showBorder = true;
    private bool _showGlow = true;
    private string _position = "BottomRight";
    private double _scale = 1;

    /// <summary>Fond de la carte (ARGB) ; un léger dégradé plus sombre vers le bas est ajouté.</summary>
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, value); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }

    /// <summary>Vrai : couleur selon le type d'alerte (orange, jaune, violet ; rouge si critique). Faux : <see cref="AccentColor"/>.</summary>
    public bool AutoAccent { get => _autoAccent; set => Set(ref _autoAccent, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

    /// <summary>Contour fin teinté de la couleur de l'alerte.</summary>
    public bool ShowBorder { get => _showBorder; set => Set(ref _showBorder, value); }
    /// <summary>Ombre colorée autour de la carte et halo de la valeur.</summary>
    public bool ShowGlow { get => _showGlow; set => Set(ref _showGlow, value); }

    /// <summary>Coin de l'écran : "BottomRight", "TopRight", "BottomLeft" ou "TopLeft".</summary>
    public string Position { get => _position; set => Set(ref _position, value); }
    /// <summary>Taille de l'alerte (1 = normale).</summary>
    public double Scale { get => _scale; set => Set(ref _scale, value); }
}
