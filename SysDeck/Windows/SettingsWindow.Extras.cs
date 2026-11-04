using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SysDeck;

/// <summary>Personnalisation des modules : ville de la météo, couleurs de l'horloge à cases et des courbes de températures.</summary>
public partial class SettingsWindow
{
    private void InitExtraWidgets()
    {
        // Réglages globaux (pas l'apparence du module) : contexte propre, qui remplace celui de l'éditeur.
        TileClockCard.DataContext = TemperatureCard.DataContext = SimHubCard.DataContext = _widgetManager.Config.WidgetExtras;
        SensorBarsCard.DataContext = _widgetManager.Config.SensorBars;
        WeatherCityBox.Text = _widgetManager.Config.Weather.City;
        ShowWeatherStatus();
    }

    private void ShowWeatherStatus()
    {
        var weather = _widgetManager.Config.Weather;
        WeatherStatusText.Text = string.IsNullOrWhiteSpace(weather.City)
            ? "Aucune ville choisie."
            : _widgetManager.Weather.Error ?? (string.IsNullOrEmpty(weather.Place) ? "" : $"Ville trouvée : {weather.Place}");
    }

    private void WeatherCityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        WeatherSearch_Click(sender, e);
    }

    private async void WeatherSearch_Click(object sender, RoutedEventArgs e)
    {
        WeatherStatusText.Text = "Recherche…";
        await _widgetManager.Weather.SetCityAsync(WeatherCityBox.Text);
        ShowWeatherStatus();
    }
}
