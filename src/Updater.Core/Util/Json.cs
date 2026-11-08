using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Updater.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Кириллица в файлах сборки остаётся читаемой, а не превращается в Р…
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Пустой файл: {path}");

    public static T Parse<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Пустой JSON");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Запись через временный файл: если флешку выдернут посреди записи, старая версия уцелеет.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Встроенный каталог (Catalog/*.json) по имени файла.</summary>
    public static T Embedded<T>(string fileName)
    {
        var asm = typeof(Json).Assembly;
        using var s = asm.GetManifestResourceStream("Updater.Catalog." + fileName)
            ?? throw new FileNotFoundException("Нет встроенного каталога " + fileName);
        return JsonSerializer.Deserialize<T>(s, Options) ?? throw new InvalidDataException(fileName);
    }

    public static IEnumerable<string> EmbeddedNames() =>
        typeof(Json).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Updater.Catalog.", StringComparison.Ordinal))
            .Select(n => n["Updater.Catalog.".Length..]);
}
