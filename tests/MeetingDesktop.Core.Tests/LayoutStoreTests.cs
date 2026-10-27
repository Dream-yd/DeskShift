using MeetingDesktop.Core.Models;

namespace MeetingDesktop.Core.Tests;

public sealed class LayoutStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MeetingDesktopLayoutTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("foo.json")]
    [InlineData("foo.JSON")]
    public void LoadAcceptsFileExtensionWhenExactLogicalNameIsAbsent(string name)
    {
        var store = new LayoutStore(_root);
        store.Save(new LayoutSnapshot { Name = "stored", Icons = [new("Alpha", 1, 2)] }, "foo");

        Assert.Equal("stored", store.Load(name)?.Name);
        Assert.Equal("stored", store.Load("foo")?.Name);
    }

    [Fact]
    public void LoadPrefersExactLogicalNameWhenBothNamesExist()
    {
        var store = new LayoutStore(_root);
        store.Save(new LayoutSnapshot { Name = "plain" }, "foo");
        store.Save(new LayoutSnapshot { Name = "literal extension" }, "foo.json");

        Assert.Equal(new[] { "foo", "foo.json" }, store.List());
        Assert.Equal("plain", store.Load("foo")?.Name);
        Assert.Equal("literal extension", store.Load("foo.json")?.Name);
        Assert.Equal("literal extension", store.Load("foo.json.json")?.Name);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"SchemaVersion\":999}")]
    public void ExistingUnusableExactNameDoesNotFallBackToAnotherLayout(string contents)
    {
        var store = new LayoutStore(_root);
        store.Save(new LayoutSnapshot { Name = "plain" }, "foo");
        string exact = store.Save(new LayoutSnapshot(), "foo.json");
        File.WriteAllText(exact, contents);

        Assert.Null(store.Load("foo.json"));
    }

    [Fact]
    public void MissingLayoutReturnsNull()
    {
        var store = new LayoutStore(_root);
        Assert.Null(store.Load("missing.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
