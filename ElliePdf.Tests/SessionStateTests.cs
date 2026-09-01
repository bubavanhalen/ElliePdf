using Xunit;

namespace ElliePdf.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public async Task RoundTrip_StoresNavigationOnly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new AtomicSessionStateStore(Path.Combine(dir.FullName, "session.json"));
            await store.SaveAsync(new() { Tabs = [new SessionTabState { Path = "C:\\docs\\a.pdf", PageIndex = 4, Zoom = 1.25, IsLockedPlaceholder = true }], RecentFiles = ["C:\\docs\\a.pdf"] }, new());
            var loaded = await store.LoadAsync();
            Assert.Single(loaded.Tabs);
            Assert.Equal(4, loaded.Tabs[0].PageIndex);
            Assert.True(loaded.Tabs[0].IsLockedPlaceholder);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task CorruptFile_IsTreatedAsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "session.json");
            await File.WriteAllTextAsync(path, "not json");
            var state = await new AtomicSessionStateStore(path).LoadAsync();
            Assert.Empty(state.Tabs);
            Assert.Empty(state.RecentFiles);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task PrivatePolicy_DisablesReopenAndRecents()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new AtomicSessionStateStore(Path.Combine(dir.FullName, "session.json"));
            await store.SaveAsync(new()
            {
                Tabs = [new SessionTabState { Path = "secret.pdf" }],
                RecentFiles = ["secret.pdf"],
                ActiveTabPath = "secret.pdf"
            }, SessionPrivacyPolicy.PrivateByDefault);
            var state = await store.LoadAsync();
            Assert.Empty(state.Tabs);
            Assert.Empty(state.RecentFiles);
            Assert.Null(state.ActiveTabPath);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ClearRecents_DoesNotClearTabs()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new AtomicSessionStateStore(Path.Combine(dir.FullName, "session.json"));
            await store.SaveAsync(new() { Tabs = [new SessionTabState { Path = "a.pdf" }], RecentFiles = ["a.pdf"] }, new());
            await store.ClearAsync(SessionDataKind.Recents);
            var state = await store.LoadAsync();
            Assert.Single(state.Tabs);
            Assert.Empty(state.RecentFiles);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ClearReopenState_RemovesActivePath()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new AtomicSessionStateStore(Path.Combine(dir.FullName, "session.json"));
            await store.SaveAsync(new()
            {
                Tabs = [new SessionTabState { Path = "a.pdf" }],
                ActiveTabPath = "a.pdf"
            }, new());

            await store.ClearAsync(SessionDataKind.ReopenState);

            var state = await store.LoadAsync();
            Assert.Empty(state.Tabs);
            Assert.Null(state.ActiveTabPath);
        }
        finally { dir.Delete(true); }
    }
}
