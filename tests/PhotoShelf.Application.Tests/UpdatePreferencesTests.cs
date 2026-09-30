using PhotoShelf.Application.Updates;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class UpdatePreferencesTests
{
    [Fact]
    public async Task FreshInstallEnablesCheckAndSavedOptOutSurvivesNewStore()
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Root, "preferences.json");
        Assert.True((await new UpdatePreferencesStore(path).LoadAsync()).AutoCheck);
        var value = new UpdatePreferences
        {
            AutoCheck = false, SkippedVersion = "0.10.14", SnoozeUntil = DateTimeOffset.UtcNow.AddDays(1),
            LastSuccessfulCheck = DateTimeOffset.UtcNow, PreparedStageId = Guid.NewGuid().ToString("N")
        };
        await new UpdatePreferencesStore(path).SaveAsync(value);
        Assert.Equal(value, await new UpdatePreferencesStore(path).LoadAsync());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":2,\"autoCheck\":true}")]
    [InlineData("{\"schema\":null,\"autoCheck\":true}")]
    [InlineData("{\"schema\":\"1\",\"autoCheck\":true}")]
    [InlineData("{\"schema\":true,\"autoCheck\":true}")]
    [InlineData("{\"schema\":{},\"autoCheck\":true}")]
    [InlineData("{\"schema\":[],\"autoCheck\":true}")]
    [InlineData("{\"schema\":1.5,\"autoCheck\":true}")]
    [InlineData("{\"schema\":1,\"autoCheck\":false,\"autoCheck\":true}")]
    [InlineData("{\"schema\":1,\"autoCheck\":\"true\"}")]
    public async Task DamagedPreferencesNeverReenableNetwork(string json)
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Root, "preferences.json");
        await File.WriteAllTextAsync(path, json);
        Assert.False((await new UpdatePreferencesStore(path).LoadAsync()).AutoCheck);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedSavePreservesPreviousOptOut()
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Root, "preferences.json");
        var store = new UpdatePreferencesStore(path);
        await store.SaveAsync(new UpdatePreferences { AutoCheck = false });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new UpdatePreferences(), cancellation.Token));
        Assert.False((await new UpdatePreferencesStore(path).LoadAsync()).AutoCheck);
    }

    [Fact]
    public async Task UnreadablePreferencesFailClosed()
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Root, "preferences.json");
        Directory.CreateDirectory(path);
        Assert.False((await new UpdatePreferencesStore(path).LoadAsync()).AutoCheck);
    }

    [Fact]
    public async Task UntrustedPreparedPathIsDiscardedWithoutChangingOptOut()
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Root, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"schema\":1,\"autoCheck\":false,\"preparedStageId\":\"../catalog\"}");
        var preferences = await new UpdatePreferencesStore(path).LoadAsync();
        Assert.False(preferences.AutoCheck);
        Assert.Null(preferences.PreparedStageId);
    }

    [Fact]
    public async Task PreferencesDoNotReadOrWriteThroughSymlink()
    {
        using var fixture = new UpdateFixture();
        var target = Path.Combine(fixture.Root, "target.json");
        var link = Path.Combine(fixture.Root, "settings.json");
        const string original = "{\"schema\":1,\"autoCheck\":true}";
        await File.WriteAllTextAsync(target, original);
        File.CreateSymbolicLink(link, target);
        var store = new UpdatePreferencesStore(link);
        Assert.False((await store.LoadAsync()).AutoCheck);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new UpdatePreferences { AutoCheck = false }));
        Assert.Equal(original, await File.ReadAllTextAsync(target));
    }

    [Fact]
    public void SkipAndSnoozeApplyOnlyToSuggestionPolicy()
    {
        using var fixture = new UpdateFixture();
        var release = UpdateManifestVerifier.Verify(fixture.ManifestBytes, fixture.Signature, fixture.PublicKey);
        var now = DateTimeOffset.UtcNow;
        Assert.True(new UpdatePreferences().ShouldSuggest(release, now));
        Assert.False(new UpdatePreferences { SkippedVersion = release.Version }.ShouldSuggest(release, now));
        Assert.False(new UpdatePreferences { SnoozeUntil = now.AddMinutes(1) }.ShouldSuggest(release, now));
        Assert.True(new UpdatePreferences { SnoozeUntil = now.AddMinutes(-1) }.ShouldSuggest(release, now));
        Assert.True(new UpdatePreferences { SkippedVersion = "0.10.13" }.ShouldSuggest(release, now));
    }
}
