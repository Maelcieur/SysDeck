using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SysDeck.Services;

public sealed class GeminiCorrector
{
    private const string SystemInstruction =
        "Tu es un correcteur orthographique et grammatical strict. Corrige le texte fourni en reformulant " +
        "légèrement si nécessaire pour que ce soit naturel, ajoute la ponctuation manquante et corrige toutes " +
        "les fautes. Tu dois UNIQUEMENT renvoyer le texte corrigé, sans commentaires, sans explications, et sans guillemets.";

    // Délai borné mais généreux : les modèles récents « réfléchissent » parfois plus de 30 s avant de répondre.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta";

    private readonly Func<string?> _getApiKey;
    private string? _cachedModel;

    public GeminiCorrector(Func<string?> getApiKey)
    {
        _getApiKey = getApiKey;
    }

    public async Task<string?> CorrectAsync(string text, CancellationToken ct = default)
    {
        var apiKey = _getApiKey();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(text))
            return null;

        var model = _cachedModel ??= await FindBestModelAsync(apiKey, ct);
        if (model is null)
            throw new InvalidOperationException("Aucun modèle Gemini disponible pour cette clé API.");


        var request = new GeminiRequest
        {
            SystemInstruction = new GeminiContent { Parts = [new GeminiPart { Text = SystemInstruction }] },
            Contents = [new GeminiContent { Parts = [new GeminiPart { Text = text }] }]
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}/{model}:generateContent")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await SendAsync(message, apiKey, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            // Seul un 404 (modèle retiré) ou une 400 « modèle invalide » justifie de rechercher le modèle ;
            // un 429 ou une erreur réseau n'a rien à voir avec lui.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                || (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorBody.Contains("model", StringComparison.OrdinalIgnoreCase)))
                _cachedModel = null;
            throw new InvalidOperationException($"Gemini a renvoyé l'erreur {(int)response.StatusCode} : {Shorten(errorBody)}");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
        var corrected = result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        return string.IsNullOrWhiteSpace(corrected) ? null : corrected.Trim();
    }

    /// <summary>
    /// La clé passe dans l'en-tête x-goog-api-key, pas dans l'adresse : une adresse peut se retrouver
    /// dans des journaux (proxy, antivirus, message d'erreur), un en-tête beaucoup moins.
    /// </summary>
    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, string apiKey, CancellationToken ct)
    {
        message.Headers.Add("x-goog-api-key", apiKey.Trim());
        return Http.SendAsync(message, ct);
    }

    private static string Shorten(string text)
        => text.Length <= 300 ? text : text[..300] + "…";

    private static async Task<string?> FindBestModelAsync(string apiKey, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/models");
        using var response = await SendAsync(message, apiKey, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Clé API refusée par Gemini (erreur {(int)response.StatusCode}) : {Shorten(await response.Content.ReadAsStringAsync(ct))}");
        var list = await response.Content.ReadFromJsonAsync<GeminiModelList>(cancellationToken: ct);

        var candidate = list?.Models?
            .Where(m => m.SupportedGenerationMethods?.Contains("generateContent") == true
                        && m.Name.Contains("flash")
                        && !m.Name.Contains("2.5")
                        && !m.Name.Contains("preview")
                        && !m.Name.Contains("omni"))
            .OrderByDescending(m => m.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return candidate?.Name;
    }

    private sealed class GeminiRequest
    {
        [JsonPropertyName("systemInstruction")]
        public GeminiContent? SystemInstruction { get; init; }

        [JsonPropertyName("contents")]
        public required GeminiContent[] Contents { get; init; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("parts")]
        public required GeminiPart[] Parts { get; init; }
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public GeminiCandidate[]? Candidates { get; init; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; init; }
    }

    private sealed class GeminiModelList
    {
        [JsonPropertyName("models")]
        public GeminiModel[]? Models { get; init; }
    }

    private sealed class GeminiModel
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("supportedGenerationMethods")]
        public string[]? SupportedGenerationMethods { get; init; }
    }
}
