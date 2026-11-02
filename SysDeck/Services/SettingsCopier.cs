using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysDeck.Services;

/// <summary>
/// Copie les valeurs d'un objet de réglages dans une instance EXISTANTE du même type.
/// Les fenêtres (Paramètres, widgets) sont abonnées aux instances de ThemeSettings / ModuleAppearance :
/// remplacer l'objet les couperait de leurs abonnés, il faut donc réécrire les valeurs sur place.
/// La copie est générique (réflexion sur les propriétés publiques) pour que toute propriété ajoutée
/// plus tard à un réglage soit copiée sans toucher à ce code.
/// </summary>
public static class SettingsCopier
{
    // Raise est protégée dans ObservableSettings : les dictionnaires (Texts, Elements) n'ont pas de
    // setter qui notifie, on prévient donc les abonnés à la main une fois leur contenu remplacé.
    private static readonly MethodInfo? RaiseMethod =
        typeof(ObservableSettings).GetMethod("Raise", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>Copie indépendante par aller-retour JSON (les abonnés de PropertyChanged ne suivent pas).</summary>
    public static T Clone<T>(T source)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;

    /// <summary>
    /// Recopie toutes les propriétés publiques lecture/écriture de <paramref name="source"/> dans
    /// <paramref name="target"/> (qui reste la même instance). Les propriétés [JsonIgnore] sont ignorées,
    /// les sous-objets (ex. ModuleAppearance.Theme) sont recopiés dans l'instance existante ou créés,
    /// les dictionnaires et listes voient leur contenu remplacé.
    /// </summary>
    public static void CopyInto<T>(T source, T target) where T : class
        => CopyProperties(typeof(T), source, target);

    private static void CopyProperties(Type type, object source, object target)
    {
        foreach (var property in GetCopiableProperties(type))
        {
            var value = property.GetValue(source);

            if (IsCollection(property.PropertyType))
            {
                CopyCollection(property, value, target);
            }
            else if (IsLeaf(property.PropertyType))
            {
                property.SetValue(target, value);
            }
            else if (value is null)
            {
                property.SetValue(target, null);
            }
            else if (property.GetValue(target) is { } existing)
            {
                CopyProperties(existing.GetType(), value, existing);
            }
            else
            {
                // Sous-objet absent côté cible : rempli avant d'être affecté, pour ne notifier qu'une fois.
                var created = Activator.CreateInstance(value.GetType())!;
                CopyProperties(created.GetType(), value, created);
                property.SetValue(target, created);
            }
        }
    }

    private static void CopyCollection(PropertyInfo property, object? sourceValue, object target)
    {
        var current = property.GetValue(target);
        if (current is null)
        {
            if (sourceValue is null || !property.CanWrite) return;
            current = Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(target, current);
        }

        if (current is IDictionary dictionary)
        {
            dictionary.Clear();
            if (sourceValue is IDictionary sourceDictionary)
                foreach (DictionaryEntry entry in sourceDictionary)
                    dictionary[entry.Key] = CopyValue(entry.Value);
        }
        else if (current is IList list)
        {
            list.Clear();
            if (sourceValue is IList sourceList)
                foreach (var item in sourceList)
                    list.Add(CopyValue(item));
        }

        if (target is ObservableSettings)
            RaiseMethod?.Invoke(target, new object?[] { property.Name });
    }

    /// <summary>Valeur d'un élément de collection : les objets sont dupliqués pour ne jamais partager d'instance avec la source.</summary>
    private static object? CopyValue(object? value)
    {
        if (value is null || IsLeaf(value.GetType())) return value;
        var copy = Activator.CreateInstance(value.GetType())!;
        CopyProperties(value.GetType(), value, copy);
        return copy;
    }

    private static bool IsLeaf(Type type) => type.IsValueType || type == typeof(string);

    private static bool IsCollection(Type type)
        => typeof(IDictionary).IsAssignableFrom(type) || (typeof(IList).IsAssignableFrom(type) && type != typeof(string));

    private static PropertyInfo[] GetCopiableProperties(Type type) => PropertyCache.GetOrAdd(type, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            // Une collection en lecture seule reste copiable (on remplit l'existante) ; le reste exige un setter public.
            .Where(p => IsCollection(p.PropertyType) || p.SetMethod is { IsPublic: true })
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .ToArray());
}
