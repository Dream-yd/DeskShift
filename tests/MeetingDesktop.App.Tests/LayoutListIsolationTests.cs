using System.IO;
using MeetingDesktop.App.ViewModels;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Tests;

public sealed class LayoutListIsolationTests
{
    [Fact]
    public void DamagedSnapshotIsShownWithoutBlockingHealthyRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetingDesktopLayoutList", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LayoutStore(root);
            store.Save(new LayoutSnapshot { Name = "Healthy" }, "healthy");
            File.WriteAllText(Path.Combine(root, "broken.json"), "{\"Icons\":[null]}");
            var rows = LayoutsViewModel.LoadItems(store);
            Assert.Equal(2, rows.Count);
            Assert.False(Assert.Single(rows, row => row.FileName == "broken").HasSnapshot);
            Assert.True(Assert.Single(rows, row => row.FileName == "healthy").HasSnapshot);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
