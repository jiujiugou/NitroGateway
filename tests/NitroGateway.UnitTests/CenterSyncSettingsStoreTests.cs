using System.IO;
using System.Text.Json;
using NitroGateway.Desktop.Services.Sync;

using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>ADR-037 S5：中心同步设置 Token 落盘 DPAPI 加密与旧明文迁移。</summary>
public sealed class CenterSyncSettingsStoreTests : IDisposable
{
    private readonly string _filePath =
        Path.Combine(Path.GetTempPath(), "nitrogateway-tests", $"{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void Save_encrypts_token_at_rest_and_load_roundtrips()
    {
        var store = new CenterSyncSettingsStore(_filePath);

        store.Save(new CenterSyncSettings { CenterUrl = "http://center:5100", CenterToken = "secret-token" });

        var raw = File.ReadAllText(_filePath);
        Assert.DoesNotContain("secret-token", raw);
        Assert.Contains("centerTokenEncrypted", raw);

        var loaded = store.Load();
        Assert.Equal("http://center:5100", loaded.CenterUrl);
        Assert.Equal("secret-token", loaded.CenterToken);
    }

    [Fact]
    public void Load_migrates_legacy_plaintext_file_to_encrypted()
    {
        File.WriteAllText(_filePath, """{"centerUrl":"http://center:5100","centerToken":"legacy-token"}""");
        var store = new CenterSyncSettingsStore(_filePath);

        var loaded = store.Load();

        Assert.Equal("legacy-token", loaded.CenterToken);
        // 迁移：读取后立即改写为密文形态，明文不再留盘
        var raw = File.ReadAllText(_filePath);
        Assert.DoesNotContain("legacy-token", raw);
        Assert.Contains("centerTokenEncrypted", raw);
    }

    [Fact]
    public void Save_empty_token_does_not_write_plaintext_property()
    {
        var store = new CenterSyncSettingsStore(_filePath);

        store.Save(new CenterSyncSettings { CenterUrl = "http://center:5100", CenterToken = "" });

        using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
        var propertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("centerToken", propertyNames);
        Assert.Contains("centerUrl", propertyNames);
        Assert.Equal("", store.Load().CenterToken);
    }

    [Fact]
    public void Load_corrupt_encrypted_token_returns_empty_token()
    {
        File.WriteAllText(_filePath, """{"centerUrl":"http://center:5100","centerTokenEncrypted":"not-base64!!"}""");

        var loaded = new CenterSyncSettingsStore(_filePath).Load();

        Assert.Equal("http://center:5100", loaded.CenterUrl);
        Assert.Equal("", loaded.CenterToken);
    }

    [Fact]
    public void Save_url_remains_plaintext_for_reference()
    {
        var store = new CenterSyncSettingsStore(_filePath);

        store.Save(new CenterSyncSettings { CenterUrl = "http://center:5100", CenterToken = "tok" });

        Assert.Contains("http://center:5100", File.ReadAllText(_filePath));
    }
}
