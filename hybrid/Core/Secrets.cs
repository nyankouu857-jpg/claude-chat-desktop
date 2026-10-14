namespace ClaudeChat.Core;

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

// DPAPI が使えない開発用ホストでだけ使う
public sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string protectedValue) => protectedValue;
}

public sealed class SecretStore(AppPaths paths, ISecretProtector protector)
{
    public const string ApiKey = "apiKey";
    public const string AdminKey = "adminKey";

    readonly SemaphoreSlim gate = new(1, 1);

    async Task<Dictionary<string, string>> ReadAllAsync() =>
        await Json.ReadAsync<Dictionary<string, string>>(paths.SecretsFile) ?? [];

    public async Task<string?> GetAsync(string name)
    {
        var all = await ReadAllAsync();
        if (all.TryGetValue(name, out var stored))
        {
            try
            {
                return protector.Unprotect(stored);
            }
            catch (Exception)
            {
                // 別の PC や別ユーザーで暗号化された値。入れ直してもらうしかない
                return null;
            }
        }
        return name == ApiKey ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : null;
    }

    public async Task SetAsync(string name, string? value)
    {
        await gate.WaitAsync();
        try
        {
            var all = await ReadAllAsync();
            if (string.IsNullOrWhiteSpace(value)) all.Remove(name);
            else all[name] = protector.Protect(value.Trim());
            await Json.WriteAtomicAsync(paths.SecretsFile, all);
        }
        finally
        {
            gate.Release();
        }
    }

    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 12 ? "••••" : $"{key[..10]}••••{key[^4..]}";
}
