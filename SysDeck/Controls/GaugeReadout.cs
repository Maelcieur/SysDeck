using System.Windows.Documents;

namespace SysDeck.Controls;

/// <summary>
/// Relie une jauge circulaire au chiffre affiché au centre : le nombre défile pendant que l'arc
/// se remplit, et reste « -- » tant qu'aucune mesure n'est disponible. Remplace le motif
/// « _hasX + DisplayValueChanged » qui était recopié dans chaque module.
/// </summary>
public sealed class GaugeReadout
{
    private readonly RadialGauge _gauge;
    private readonly Run _run;
    private bool _hasValue;

    public GaugeReadout(RadialGauge gauge, Run run)
    {
        _gauge = gauge;
        _run = run;
        gauge.DisplayValueChanged += v => _run.Text = _hasValue ? $"{v:0}" : "--";
    }

    /// <summary>Nouvelle mesure (null = indisponible : la jauge se vide et le texte passe à « -- »).</summary>
    public void Set(double? value)
    {
        _hasValue = value is not null;
        _gauge.Value = value ?? 0;
        // Valeur inchangée (0 % dès le départ) : l'animation ne se déclenche pas, on écrit le chiffre directement.
        _run.Text = _hasValue ? $"{_gauge.DisplayValue:0}" : "--";
    }
}
