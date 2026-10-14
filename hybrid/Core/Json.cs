using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ClaudeChat.Core;

public static class Json
{
    // 手で開いて編集する前提なので、日本語をエスケープせず整形して書く
    public static readonly JsonSerializerOptions File = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAtomicAsync<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 書き込み中に落ちてもファイルが壊れないように
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await System.IO.File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, options ?? File));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path, JsonSerializerOptions? options = null)
    {
        if (!System.IO.File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(await System.IO.File.ReadAllTextAsync(path), options ?? File);
    }

    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Wire)!;
}
