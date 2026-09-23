using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Storage;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class DataRootRelocationServiceTests : IDisposable
{
    private const string Temporary = ".clipensk-relocating";

    private static readonly string[] SourceFiles =
    [
        "storage-crypto.json",
        Path.Combine("Current", "current.db"),
        Path.Combine("Current", "storage-catalog.db"),
        Path.Combine("Archive", "2026-09-01.db"),
        Path.Combine("Archive", ".clipensk-archive-rotation-1", "staged.db"),
        Path.Combine("Files", "2026-09-01", "image.png"),
        Path.Combine("Languages", "en.json"),
    ];

    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public DataRootRelocationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clipensk-relocation-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "Source");
        _target = Path.Combine(_root, "Target");
        for (int index = 0; index < SourceFiles.Length; index++)
        {
            string path = Path.Combine(_source, SourceFiles[index]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Content(index));
        }

        Directory.CreateDirectory(Path.Combine(_source, "Trash"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Relocate_MovesTheWholeTreeAndRemovesTheSource()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        Dictionary<string, byte[]> expected = ReadTree(_source);

        DataRootRelocationResult result = await service.RelocateAsync(_target);

        Assert.Equal(_target, result.DataRootPath);
        Assert.Equal(SourceFiles.Length, result.FileCount);
        Assert.Empty(result.SourceRemoval.Retained);
        Assert.False(Directory.Exists(_source));
        AssertSameTree(expected, ReadTree(_target));
        Assert.True(Directory.Exists(Path.Combine(_target, "Trash")));
        Assert.Equal(new DataRootLocationState(_target, null), store.State);

        // Copying with the source, then the commit with the target, then the cleared marker.
        Assert.Collection(
            store.Writes,
            write =>
            {
                Assert.Equal(_source, write.DataRootPath);
                Assert.Equal(DataRootRelocationPhase.Copying, write.PendingRelocation!.Phase);
                Assert.False(write.PendingRelocation.TargetExisted);
            },
            write =>
            {
                Assert.Equal(_target, write.DataRootPath);
                Assert.Equal(DataRootRelocationPhase.Switched, write.PendingRelocation!.Phase);
            },
            write => Assert.Equal(new DataRootLocationState(_target, null), write));
    }

    [Fact]
    public async Task Relocate_ProtectsTheTargetAfterTheMarkerAndBeforeTheFirstByte()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        bool protectedTarget = false;

        await service.RelocateAsync(
            _target,
            protectTarget: path =>
            {
                Assert.Equal(_target, path);
                Assert.Equal(DataRootRelocationPhase.Copying, store.State.PendingRelocation?.Phase);
                Assert.False(Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());
                protectedTarget = true;
            });

        Assert.True(protectedTarget);
    }

    [Fact]
    public async Task Inspect_SizesTheRelocationWithoutChangingAnything()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => 1_000_000_000);

        DataRootRelocationPreview preview = await service.InspectAsync(_target + Path.DirectorySeparatorChar);

        Assert.Equal(_source, preview.SourcePath);
        Assert.Equal(_target, preview.TargetPath);
        Assert.Equal(SourceFiles.Length, preview.FileCount);
        Assert.Equal(SourceFiles.Select((_, index) => (long)Content(index).Length).Sum(), preview.ByteCount);
        Assert.Equal(preview.ByteCount + 64L * 1024 * 1024, preview.RequiredBytes);
        Assert.Empty(store.Writes);
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public async Task Relocate_RefusesTargetsItCannotSafelyUse()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);

        await AssertRefused(DataRootRelocationRefusal.TargetSameAsSource, service.RelocateAsync(_source.ToUpperInvariant()));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.RelocateAsync(Path.Combine(_source, "Inner")));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.RelocateAsync(_root));

        File.WriteAllText(Path.Combine(_root, "file"), "x");
        await AssertRefused(DataRootRelocationRefusal.TargetIsFile, service.RelocateAsync(Path.Combine(_root, "file")));

        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "foreign.txt"), "x");
        await AssertRefused(DataRootRelocationRefusal.TargetNotEmpty, service.RelocateAsync(_target));

        var tight = new DataRootRelocationService(store, _ => 10);
        await AssertRefused(DataRootRelocationRefusal.InsufficientSpace, tight.RelocateAsync(Path.Combine(_root, "Other")));

        Assert.Empty(store.Writes);
        Assert.True(File.Exists(Path.Combine(_target, "foreign.txt")));
    }

    [Fact]
    public async Task Relocate_RefusesWithoutAStorageOrWhileARelocationIsPending()
    {
        var unconfigured = new DataRootRelocationService(new MemoryLocationStore(null), _ => long.MaxValue);
        await AssertRefused(DataRootRelocationRefusal.DataRootNotConfigured, unconfigured.RelocateAsync(_target));

        string empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(empty);
        var notStorage = new DataRootRelocationService(new MemoryLocationStore(empty), _ => long.MaxValue);
        await AssertRefused(DataRootRelocationRefusal.SourceNotStorage, notStorage.RelocateAsync(_target));

        var pendingStore = new MemoryLocationStore(_source)
        {
            State = new DataRootLocationState(_source, Marker(DataRootRelocationPhase.Copying)),
        };
        var pending = new DataRootRelocationService(pendingStore, _ => long.MaxValue);
        await AssertRefused(DataRootRelocationRefusal.RelocationPending, pending.RelocateAsync(_target));
    }

    [Fact]
    public async Task Relocate_SourceFileInUse_RefusesBeforeWritingAnything()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);

        using (new FileStream(
                   Path.Combine(_source, "Current", "current.db"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            await AssertRefused(DataRootRelocationRefusal.SourceBusy, service.RelocateAsync(_target));
        }

        Assert.Empty(store.Writes);
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public async Task Relocate_SourceWithALink_IsRefused()
    {
        File.CreateSymbolicLink(Path.Combine(_source, "Files", "link.png"), Path.Combine(_root, "elsewhere.png"));
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);

        await AssertRefused(DataRootRelocationRefusal.SourceContainsLink, service.RelocateAsync(_target));

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Relocate_CancelledWhileCopying_RemovesTheCopyAndKeepsTheSource()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        Dictionary<string, byte[]> expected = ReadTree(_source);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RelocateAsync(
            _target,
            progress: new SynchronousProgress(update =>
            {
                if (update.FilesCopied == 2)
                {
                    cancellation.Cancel();
                }
            }),
            cancellationToken: cancellation.Token));

        Assert.Equal(new DataRootLocationState(_source, null), store.State);
        Assert.False(Directory.Exists(_target));
        AssertSameTree(expected, ReadTree(_source));
    }

    [Fact]
    public async Task Relocate_EmptyTargetThatExisted_IsKeptWhenTheCopyIsUndone()
    {
        Directory.CreateDirectory(_target);
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RelocateAsync(
            _target,
            progress: new SynchronousProgress(update =>
            {
                if (update.FilesCopied == 1)
                {
                    cancellation.Cancel();
                }
            }),
            cancellationToken: cancellation.Token));

        Assert.True(store.Writes[0].PendingRelocation!.TargetExisted);
        Assert.True(Directory.Exists(_target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_target));
    }

    [Fact]
    public async Task Relocate_CommitWriteFails_UndoesTheCopyAndKeepsTheSource()
    {
        var store = new MemoryLocationStore(_source)
        {
            FailWhen = state => state.PendingRelocation?.Phase == DataRootRelocationPhase.Switched,
        };
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        Dictionary<string, byte[]> expected = ReadTree(_source);

        await Assert.ThrowsAsync<IOException>(() => service.RelocateAsync(_target));

        Assert.Equal(new DataRootLocationState(_source, null), store.State);
        Assert.False(Directory.Exists(_target));
        AssertSameTree(expected, ReadTree(_source));
    }

    [Fact]
    public async Task Relocate_CopyAlteredBeforeVerification_IsNotCommitted()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootRelocationService(store, _ => long.MaxValue);
        Dictionary<string, byte[]> expected = ReadTree(_source);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RelocateAsync(
            _target,
            progress: new SynchronousProgress(update =>
            {
                if (update.FilesCopied == update.FileCount)
                {
                    // The same length, different bytes: only the digest can tell.
                    File.WriteAllBytes(Path.Combine(_target, SourceFiles[0]), Content(0, "altered"));
                }
            })));

        Assert.Equal(new DataRootLocationState(_source, null), store.State);
        Assert.False(Directory.Exists(_target));
        AssertSameTree(expected, ReadTree(_source));
    }

    [Fact]
    public async Task Recover_Copying_RemovesWhatTheCopyCreatedAndKeepsForeignFiles()
    {
        Directory.CreateDirectory(Path.Combine(_target, "Current"));
        File.WriteAllBytes(Path.Combine(_target, SourceFiles[0]), Content(0));
        File.WriteAllBytes(Path.Combine(_target, SourceFiles[1] + Temporary), [1, 2]);
        File.WriteAllText(Path.Combine(_target, "foreign.txt"), "keep");
        var store = new MemoryLocationStore(_source)
        {
            State = new DataRootLocationState(_source, Marker(DataRootRelocationPhase.Copying)),
        };
        Dictionary<string, byte[]> expected = ReadTree(_source);

        DataRootRelocationRecoveryResult result = await new DataRootRelocationService(store).RecoverAsync();

        Assert.Equal(DataRootRelocationRecoveryOutcome.RolledBack, result.Outcome);
        Assert.Equal(_source, result.DataRootPath);
        Assert.Equal(["foreign.txt"], result.Retained);
        Assert.Equal(["foreign.txt"], Directory.EnumerateFileSystemEntries(_target).Select(Path.GetFileName));
        Assert.Equal(new DataRootLocationState(_source, null), store.State);
        AssertSameTree(expected, ReadTree(_source));
    }

    [Fact]
    public async Task Recover_Switched_DeletesOnlySourceFilesTheTargetHoldsIdentically()
    {
        CopyTree(_source, _target);
        File.WriteAllBytes(Path.Combine(_target, SourceFiles[1]), Content(1, "changed after unlock"));
        File.WriteAllText(Path.Combine(_source, "foreign.txt"), "keep");
        var store = new MemoryLocationStore(_target)
        {
            State = new DataRootLocationState(_target, Marker(DataRootRelocationPhase.Switched)),
        };

        DataRootRelocationRecoveryResult result = await new DataRootRelocationService(store).RecoverAsync();

        Assert.Equal(DataRootRelocationRecoveryOutcome.Completed, result.Outcome);
        Assert.Equal(_target, result.DataRootPath);
        Assert.Contains(SourceFiles[1], result.Retained);
        Assert.Contains("foreign.txt", result.Retained);
        Assert.True(File.Exists(Path.Combine(_source, SourceFiles[1])));
        Assert.True(File.Exists(Path.Combine(_source, "foreign.txt")));
        Assert.False(File.Exists(Path.Combine(_source, SourceFiles[0])));
        Assert.False(File.Exists(Path.Combine(_source, SourceFiles[2])));
        Assert.Equal(new DataRootLocationState(_target, null), store.State);
    }

    [Fact]
    public async Task Recover_Switched_AlreadyRemovedSource_Completes()
    {
        CopyTree(_source, _target);
        Directory.Delete(_source, recursive: true);
        var store = new MemoryLocationStore(_target)
        {
            State = new DataRootLocationState(_target, Marker(DataRootRelocationPhase.Switched)),
        };

        DataRootRelocationRecoveryResult result = await new DataRootRelocationService(store).RecoverAsync();

        Assert.Equal(DataRootRelocationRecoveryOutcome.Completed, result.Outcome);
        Assert.Empty(result.Retained);
        Assert.Equal(new DataRootLocationState(_target, null), store.State);
    }

    [Theory]
    [InlineData(DataRootRelocationPhase.Copying)]
    [InlineData(DataRootRelocationPhase.Switched)]
    public async Task Recover_DataRootMatchingNeitherSide_FailsClosed(DataRootRelocationPhase phase)
    {
        CopyTree(_source, _target);
        string other = Path.Combine(_root, "Other");
        DataRootLocationState state = new(other, Marker(phase));
        var store = new MemoryLocationStore(other) { State = state };

        await Assert.ThrowsAsync<InvalidDataException>(() => new DataRootRelocationService(store).RecoverAsync());

        Assert.Same(state, store.State);
        Assert.Equal(SourceFiles.Length, Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Count());
        Assert.Equal(SourceFiles.Length, Directory.EnumerateFiles(_target, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Recover_SwitchedWithoutTheTarget_FailsClosed()
    {
        var store = new MemoryLocationStore(_target)
        {
            State = new DataRootLocationState(_target, Marker(DataRootRelocationPhase.Switched)),
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new DataRootRelocationService(store).RecoverAsync());

        Assert.NotNull(store.State.PendingRelocation);
        Assert.Equal(SourceFiles.Length, Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Recover_NothingPending_ChangesNothing()
    {
        var store = new MemoryLocationStore(_source);

        DataRootRelocationRecoveryResult result = await new DataRootRelocationService(store).RecoverAsync();

        Assert.Equal(DataRootRelocationRecoveryOutcome.NothingPending, result.Outcome);
        Assert.Equal(_source, result.DataRootPath);
        Assert.Empty(store.Writes);
    }

    private DataRootRelocationMarker Marker(DataRootRelocationPhase phase) =>
        new(
            Guid.NewGuid(),
            _source,
            _target,
            TargetExisted: false,
            phase,
            new DateTimeOffset(2026, 9, 23, 6, 0, 0, TimeSpan.Zero));

    private static async Task AssertRefused(DataRootRelocationRefusal reason, Task operation)
    {
        DataRootRelocationRefusedException refused =
            await Assert.ThrowsAsync<DataRootRelocationRefusedException>(() => operation);
        Assert.Equal(reason, refused.Reason);
    }

    private static byte[] Content(int index, string? salt = null)
    {
        // Distinct bytes per file; the same length for the same file whatever the salt.
        int length = 1000 + index % 7 * 131;
        byte[] bytes = new byte[length];
        new Random(index * 7919 + (salt?.Length ?? 0) + (salt is null ? 0 : 1)).NextBytes(bytes);
        return bytes;
    }

    private static Dictionary<string, byte[]> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);

    private static void AssertSameTree(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expected)
        {
            Assert.Equal(bytes, actual[path]);
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string directory in Directory.EnumerateDirectories(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            string destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private sealed class MemoryLocationStore : IDataRootLocationStore
    {
        public MemoryLocationStore(string? dataRootPath)
        {
            State = new DataRootLocationState(dataRootPath, null);
        }

        public DataRootLocationState State { get; set; }

        public List<DataRootLocationState> Writes { get; } = [];

        public Func<DataRootLocationState, bool>? FailWhen { get; init; }

        public Task<DataRootLocationState> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task WriteAsync(DataRootLocationState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWhen?.Invoke(state) == true)
            {
                throw new IOException("Injected settings write failure.");
            }

            State = state;
            Writes.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class SynchronousProgress(Action<DataRootRelocationProgress> report) : IProgress<DataRootRelocationProgress>
    {
        public void Report(DataRootRelocationProgress value) => report(value);
    }
}
