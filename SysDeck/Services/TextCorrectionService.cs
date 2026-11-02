using System.IO;
using System.Runtime.InteropServices;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using DataObject = System.Windows.DataObject;

namespace SysDeck.Services;

/// <summary>
/// Correction du texte sélectionné dans n'importe quelle application (Ctrl+Espace) : copie la sélection,
/// la fait corriger par Gemini, colle le résultat à la place, puis restaure le presse-papiers d'origine.
/// </summary>
public sealed class TextCorrectionService
{
    private readonly GeminiCorrector _corrector;
    private readonly Action<string> _showWarning;
    private bool _busy;

    public TextCorrectionService(GeminiCorrector corrector, Action<string> showWarning)
    {
        _corrector = corrector;
        _showWarning = showWarning;
    }

    // Au-delà, la sélection n'est pas envoyée : vie privée (texte copié par erreur) et coût de l'appel.
    private const int MaxSelectionLength = 20_000;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>Ne lève jamais d'exception : appelée depuis un raccourci global, une erreur fermerait l'application.</summary>
    public async Task RunAsync()
    {
        if (_busy)
            return;

        _busy = true;
        var sourceWindow = GetForegroundWindow(); // la correction ne doit être collée que dans cette fenêtre
        DataObject? snapshot = null;
        var clipboardTouched = false;
        string? expectedText = null; // dernier texte que NOUS avons placé : sert à détecter une copie de l'utilisateur
        try
        {
            snapshot = SnapshotClipboard();

            clipboardTouched = true;
            var selectedText = await CopySelectionAsync();
            expectedText = selectedText;
            if (string.IsNullOrWhiteSpace(selectedText))
                return;

            if (selectedText.Length > MaxSelectionLength)
            {
                _showWarning($"Sélection trop longue ({selectedText.Length} caractères, maximum {MaxSelectionLength}) : rien n'a été envoyé à Gemini.");
                return;
            }

            TrayIconService.ShowNotice("Envoi du texte à Gemini…");

            string? corrected;
            try
            {
                corrected = await _corrector.CorrectAsync(selectedText);
            }
            catch (Exception ex)
            {
                Log.Warn("Correction Gemini en échec", ex);
                _showWarning($"Échec de la correction :\n{ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(corrected))
                return;

            // L'utilisateur a pu changer de fenêtre pendant l'appel réseau : coller ailleurs serait dangereux.
            if (GetForegroundWindow() != sourceWindow)
            {
                _showWarning("La fenêtre active a changé pendant la correction : le texte n'a pas été collé.");
                return;
            }

            if (!TrySetClipboardText(corrected))
            {
                _showWarning("Le presse-papiers est occupé par une autre application : la correction n'a pas pu être collée.");
                return;
            }
            expectedText = corrected;
            InputSimulator.SendCtrlV();
            await Task.Delay(500); // laisse l'application cible lire le presse-papiers avant de le restaurer
        }
        catch (Exception ex)
        {
            Log.Error("Correction du texte sélectionné en échec", ex);
        }
        finally
        {
            if (clipboardTouched)
                RestoreClipboard(snapshot, expectedText);
            _busy = false;
        }
    }

    /// <summary>
    /// Copie tous les formats du presse-papiers dans un objet indépendant : l'objet renvoyé par
    /// Clipboard.GetDataObject() est un accès direct, il devient inutilisable après Clear().
    /// </summary>
    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var live = Clipboard.GetDataObject();
            if (live is null) return null;

            var copy = new DataObject();
            foreach (var format in live.GetFormats(false))
            {
                try
                {
                    var data = live.GetData(format, false);
                    if (data is null) continue;
                    if (data is Stream stream)
                    {
                        var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        memory.Position = 0;
                        data = memory;
                    }
                    copy.SetData(format, data, false);
                }
                catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or OutOfMemoryException)
                {
                    Log.WarnOnce("clipboard-format:" + format, $"Format de presse-papiers « {format} » non sauvegardé", ex);
                }
            }
            return copy;
        }
        catch (COMException ex)
        {
            Log.WarnOnce("clipboard-snapshot", "Presse-papiers occupé : contenu d'origine non sauvegardé", ex);
            return null;
        }
    }

    /// <summary>Envoie Ctrl+C à l'application active et attend que la sélection arrive dans le presse-papiers.</summary>
    private static async Task<string?> CopySelectionAsync()
    {
        try { Clipboard.Clear(); } catch (COMException) { /* occupé : on détectera quand même le nouveau texte */ }
        InputSimulator.SendCtrlC();

        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(100);
            try
            {
                if (Clipboard.ContainsText())
                    return Clipboard.GetText();
            }
            catch (COMException) { /* presse-papiers momentanément verrouillé par une autre application */ }
        }
        return null;
    }

    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    /// <summary>
    /// Remet le contenu d'origine, sauf si le presse-papiers ne contient plus ce que nous y avons placé
    /// (l'utilisateur a copié autre chose entre-temps : on ne l'écrase pas).
    /// </summary>
    private static void RestoreClipboard(DataObject? snapshot, string? expectedText)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (current != expectedText)
                    return;

                if (snapshot is null)
                    Clipboard.Clear();
                else
                    Clipboard.SetDataObject(snapshot, true);
                return;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                if (attempt == 4)
                {
                    Log.Warn("Restauration du presse-papiers impossible", ex);
                    return;
                }
                Thread.Sleep(80);
            }
        }
    }
}
