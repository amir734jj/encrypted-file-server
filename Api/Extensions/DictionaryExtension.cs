namespace Api.Extensions;

public static class DictionaryExtension
{
    public static bool ContainKeys<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> source, params TKey[] keys)
        => keys.All(k => source.ContainsKey(k));
}
