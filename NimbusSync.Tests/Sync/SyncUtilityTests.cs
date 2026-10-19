using NimbusSync.Engine.Models;
using NimbusSync.Engine.Sync;

namespace NimbusSync.Tests.Sync;

public class PathNormalizerTests
{
    [Fact]
    public void ToRelative_StripsRoot_NormalizesSeparators()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbus-root");
        var abs = Path.Combine(root, "docs", "file.txt");
        Assert.Equal("docs/file.txt", PathNormalizer.ToRelative(root, abs));
        Assert.Equal("docs/file.txt", PathNormalizer.ToRelative(root + Path.DirectorySeparatorChar, abs));
    }

    [Fact]
    public void ToRelative_RootItself_IsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "nimbus-root");
        Assert.Equal(string.Empty, PathNormalizer.ToRelative(root, root));
    }

    [Fact]
    public void ToRelative_SiblingWithSharedPrefix_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync");
        var sibling = Path.Combine(Path.GetTempPath(), "sync2", "file.txt");
        Assert.Throws<ArgumentException>(() => PathNormalizer.ToRelative(root, sibling));
    }

    [Theory]
    [InlineData("a/b/c.txt", "a/b", "c.txt")]
    [InlineData("c.txt", "", "c.txt")]
    public void ParentOf_NameOf(string path, string parent, string name)
    {
        Assert.Equal(parent, PathNormalizer.ParentOf(path));
        Assert.Equal(name, PathNormalizer.NameOf(path));
    }

    [Fact]
    public void IsUnder_RequiresSeparator()
    {
        Assert.True(PathNormalizer.IsUnder("dir/a", "dir"));
        Assert.False(PathNormalizer.IsUnder("dir2/a", "dir"));
    }
}

public class ExclusionMatcherTests
{
    [Theory]
    [InlineData("~$report.docx", true)]
    [InlineData("docs/~$report.docx", true)]
    [InlineData("build/out.tmp", true)]
    [InlineData(".nimbus-trash/20260101/x.txt", true)]
    [InlineData("a/b/file.txt.nimbus-tmp", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("report.docx", false)]
    [InlineData("tmp/keep.txt", false)]
    public void DefaultPatterns(string path, bool excluded)
    {
        var m = new ExclusionMatcher(SyncProfile.DefaultExcludes);
        Assert.Equal(excluded, m.IsExcluded(path));
    }

    [Fact]
    public void PathPatterns_MatchFullPathOnly()
    {
        var m = new ExclusionMatcher(["node_modules", "src/generated/*"]);
        Assert.True(m.IsExcluded("app/node_modules/x.js"));
        Assert.True(m.IsExcluded("src/generated/types.cs"));
        Assert.False(m.IsExcluded("other/generated/types.cs"));
    }
}

public class FileHasherTests
{
    [Fact]
    public async Task Md5_MatchesKnownVector()
    {
        using var s = new MemoryStream("hello world"u8.ToArray());
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", await FileHasher.ComputeMd5Async(s));
        Assert.Equal(0, s.Position);
    }

    [Fact]
    public async Task Md5_File_ReadsWhileOtherWritersHoldHandle()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var w = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                await w.WriteAsync("abc"u8.ToArray());
                await w.FlushAsync();
                var hash = await FileHasher.ComputeMd5Async(path);
                Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hash);
            }
        }
        finally { File.Delete(path); }
    }
}

public class DebouncingFileSystemMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nimbus_fsw_{Guid.NewGuid():N}");
    public DebouncingFileSystemMonitorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Coalescing_DeleteWins_CreatedBeatsModified()
    {
        using var m = new DebouncingFileSystemMonitor(TimeSpan.Zero);
        m.Start(_dir, []);
        m.Inject("a.txt", ChangeType.Created);
        m.Inject("b.txt", ChangeType.Modified);
        m.Inject("b.txt", ChangeType.Deleted);
        var changes = m.DrainChanges().ToDictionary(c => c.RelativePath, c => c.Type);
        Assert.Equal(ChangeType.Created, changes["a.txt"]);
        Assert.Equal(ChangeType.Deleted, changes["b.txt"]);
        Assert.Empty(m.DrainChanges());
    }

    [Fact]
    public void Drain_HoldsChangesStillInsideDebounceWindow()
    {
        using var m = new DebouncingFileSystemMonitor(TimeSpan.FromSeconds(30));
        m.Start(_dir, []);
        typeof(DebouncingFileSystemMonitor).GetMethod("Record", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(m, [Path.Combine(_dir, "fresh.txt"), ChangeType.Created]);
        Assert.Empty(m.DrainChanges());
    }

    [Fact]
    public void ExcludedPaths_AreNeverRecorded()
    {
        using var m = new DebouncingFileSystemMonitor(TimeSpan.Zero);
        m.Start(_dir, ["*.log"]);
        var record = typeof(DebouncingFileSystemMonitor).GetMethod("Record", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        record.Invoke(m, [Path.Combine(_dir, "app.log"), ChangeType.Created]);
        record.Invoke(m, [Path.Combine(_dir, "x.nimbus-tmp"), ChangeType.Created]);
        record.Invoke(m, [Path.Combine(_dir, "keep.txt"), ChangeType.Created]);
        Assert.Equal(["keep.txt"], m.DrainChanges().Select(c => c.RelativePath));
    }

    /// <summary>
    /// Real OS watcher. Reliable on Windows; inotify in containers is best-effort, so the
    /// test is skipped elsewhere rather than weakened into one that cannot fail.
    /// </summary>
    [Fact]
    public async Task RealWatcher_DetectsCreateAndCoalesces_WindowsOnly()
    {
        if (!IsWindows) return;

        using var m = new DebouncingFileSystemMonitor(TimeSpan.FromMilliseconds(100));
        m.Start(_dir, []);
        var path = Path.Combine(_dir, "watched.txt");
        await File.WriteAllTextAsync(path, "1");
        await File.WriteAllTextAsync(path, "22");
        await File.WriteAllTextAsync(path, "333");
        await Task.Delay(400);

        var changes = m.DrainChanges();
        var forFile = changes.Where(c => c.RelativePath == "watched.txt").ToList();
        Assert.Single(forFile);
        Assert.Equal(ChangeType.Created, forFile[0].Type);
    }
}
