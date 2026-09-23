using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Storage;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

/// <summary>A backup of the data root and opening a storage from a folder (docs/BACKUP_PROTOCOL.md).</summary>
public sealed class DataRootBackupServiceTests : IDisposable
{
    private const string Incomplete = ".incomplete";
    private const string ExpectedName = "Clipensk-backup-2026-09-23_09-30-05";

    private static readonly string[] SourceFiles =
    [
        Path.Combine("Current", "current.db"),
        Path.Combine("Current", "storage-catalog.db"),
        Path.Combine("Archive", "archive_000001.db"),
        Path.Combine("Archive", ".clipensk-archive-rotation-1", "staged.db"),
        Path.Combine("Files", "2026-09-01", "image.png"),
        Path.Combine("Languages", "en.json"),
    ];

    private readonly string _root;
    private readonly string _source;
    private readonly string _backups;

    public DataRootBackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clipensk-backup-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "Data");
        _backups = Path.Combine(_root, "Backups");
        for (int index = 0; index < SourceFiles.Length; index++)
        {
            string path = Path.Combine(_source, SourceFiles[index]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Content(index));
        }

        Directory.CreateDirectory(Path.Combine(_source, "Trash"));
        Directory.CreateDirectory(_backups);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_CopiesTheWholeTree_UnderItsFinalName_AndLeavesTheDataRootAlone()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        Dictionary<string, byte[]> expected = ReadTree(_source);

        DataRootBackupPreview preview = await service.InspectAsync(_backups);
        DataRootBackupResult result = await service.CreateAsync(preview.BackupPath);

        string backup = Path.Combine(_backups, ExpectedName);
        Assert.Equal(backup, preview.BackupPath);
        Assert.Equal(backup, result.BackupPath);
        Assert.Equal(SourceFiles.Length, result.FileCount);
        Assert.Equal(expected.Values.Sum(static bytes => (long)bytes.Length), result.ByteCount);
        AssertSameTree(expected, ReadTree(backup));
        Assert.True(Directory.Exists(Path.Combine(backup, "Trash")));
        Assert.False(Directory.Exists(backup + Incomplete));
        AssertSameTree(expected, ReadTree(_source));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Backup_ProtectsItsUnfinishedFolderBeforeTheFirstByte()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        DataRootBackupPreview preview = await service.InspectAsync(_backups);
        string? protectedPath = null;
        bool emptyWhenProtected = false;

        await service.CreateAsync(preview.BackupPath, path =>
        {
            protectedPath = path;
            emptyWhenProtected = !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();
        });

        Assert.Equal(preview.BackupPath + Incomplete, protectedPath);
        Assert.True(emptyWhenProtected);
    }

    [Fact]
    public async Task Backup_Cancelled_LeavesNoBackupAndNoUnfinishedFolder()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        DataRootBackupPreview preview = await service.InspectAsync(_backups);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
            preview.BackupPath,
            progress: new SynchronousProgress(update =>
            {
                if (update.FilesCopied == 2)
                {
                    cancellation.Cancel();
                }
            }),
            cancellationToken: cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_backups));
    }

    [Fact]
    public async Task Backup_CopyAlteredBeforeVerification_NeverTakesTheFinalName()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        DataRootBackupPreview preview = await service.InspectAsync(_backups);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            preview.BackupPath,
            progress: new SynchronousProgress(update =>
            {
                if (update.FilesCopied == update.FileCount)
                {
                    // The same length, different bytes: only the digest can tell.
                    File.WriteAllBytes(
                        Path.Combine(preview.BackupPath + Incomplete, SourceFiles[0]),
                        Content(0, "altered"));
                }
            })));

        Assert.False(Directory.Exists(preview.BackupPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_backups));
    }

    [Fact]
    public async Task Backup_SourceFileInUse_RefusesBeforeWritingAnything()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        DataRootBackupPreview preview = await service.InspectAsync(_backups);

        using (new FileStream(
                   Path.Combine(_source, "Current", "current.db"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            await AssertRefused(DataRootRelocationRefusal.SourceBusy, service.CreateAsync(preview.BackupPath));
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(_backups));
    }

    [Fact]
    public async Task Backup_RefusesLocationsItCannotSafelyUse()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService service = Service(store);
        string backup = Path.Combine(_backups, ExpectedName);

        await AssertRefused(DataRootRelocationRefusal.TargetMissing, service.InspectAsync(Path.Combine(_root, "Missing")));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.InspectAsync(_source));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.InspectAsync(Path.Combine(_source, "Files")));

        Directory.CreateDirectory(backup);
        await AssertRefused(DataRootRelocationRefusal.TargetNotEmpty, service.InspectAsync(_backups));
        await AssertRefused(DataRootRelocationRefusal.TargetNotEmpty, service.CreateAsync(backup));
        Directory.Delete(backup);

        Directory.CreateDirectory(backup + Incomplete);
        await AssertRefused(DataRootRelocationRefusal.TargetNotEmpty, service.CreateAsync(backup));
        Directory.Delete(backup + Incomplete);

        await AssertRefused(
            DataRootRelocationRefusal.InsufficientSpace,
            Service(store, freeBytes: 1024).InspectAsync(_backups));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_backups));
    }

    [Fact]
    public async Task Backup_RefusesWithoutAStorageOrWhileARelocationIsPending()
    {
        string empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(empty);
        await AssertRefused(
            DataRootRelocationRefusal.SourceNotStorage,
            Service(new MemoryLocationStore(empty)).InspectAsync(_backups));
        await AssertRefused(
            DataRootRelocationRefusal.DataRootNotConfigured,
            Service(new MemoryLocationStore(null)).InspectAsync(_backups));

        var pending = new MemoryLocationStore(_source)
        {
            State = new DataRootLocationState(_source, PendingRelocation()),
        };
        await AssertRefused(DataRootRelocationRefusal.RelocationPending, Service(pending).InspectAsync(_backups));
    }

    [Fact]
    public async Task Open_MakesTheBackupTheDataRoot_AndLeavesThePreviousOneAlone()
    {
        var store = new MemoryLocationStore(_source);
        DataRootBackupService backups = Service(store);
        DataRootBackupResult backup = await backups.CreateAsync((await backups.InspectAsync(_backups)).BackupPath);
        Dictionary<string, byte[]> expected = ReadTree(_source);
        var service = new DataRootOpenService(store);

        DataRootOpenPreview preview = await service.InspectAsync(backup.BackupPath + Path.DirectorySeparatorChar);
        string opened = await service.OpenAsync(backup.BackupPath);

        Assert.Equal(_source, preview.CurrentPath);
        Assert.Equal(backup.BackupPath, preview.NewPath);
        Assert.Equal(3, preview.DatabaseCount);
        Assert.Equal(backup.BackupPath, opened);
        Assert.Equal(new DataRootLocationState(backup.BackupPath, null), store.State);
        Assert.Single(store.Writes);
        AssertSameTree(expected, ReadTree(_source));
    }

    [Fact]
    public async Task Open_RefusesFoldersThatAreNotAnotherStorage()
    {
        var store = new MemoryLocationStore(_source);
        var service = new DataRootOpenService(store);
        string other = Path.Combine(_root, "Other");
        Directory.CreateDirectory(Path.Combine(other, "Current"));
        File.WriteAllBytes(Path.Combine(other, "Current", "current.db"), Content(0));
        string unfinished = Path.Combine(_backups, ExpectedName + Incomplete);
        Directory.CreateDirectory(Path.Combine(unfinished, "Current"));
        File.WriteAllBytes(Path.Combine(unfinished, "Current", "current.db"), Content(0));
        string notStorage = Path.Combine(_root, "NotStorage");
        Directory.CreateDirectory(Path.Combine(notStorage, "Files"));

        await AssertRefused(DataRootRelocationRefusal.TargetMissing, service.OpenAsync(Path.Combine(_root, "Missing")));
        await AssertRefused(DataRootRelocationRefusal.TargetIsFile, service.OpenAsync(Path.Combine(other, "Current", "current.db")));
        await AssertRefused(DataRootRelocationRefusal.TargetSameAsSource, service.OpenAsync(_source));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.OpenAsync(Path.Combine(_source, "Archive")));
        await AssertRefused(DataRootRelocationRefusal.TargetNested, service.OpenAsync(_root));
        await AssertRefused(DataRootRelocationRefusal.TargetIncompleteBackup, service.OpenAsync(unfinished));
        await AssertRefused(DataRootRelocationRefusal.TargetNotStorage, service.OpenAsync(notStorage));

        store.State = new DataRootLocationState(_source, PendingRelocation());
        await AssertRefused(DataRootRelocationRefusal.RelocationPending, service.OpenAsync(other));

        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Open_WithoutAConfiguredDataRoot_TakesTheFolder()
    {
        var store = new MemoryLocationStore(null);

        string opened = await new DataRootOpenService(store).OpenAsync(_source);

        Assert.Equal(_source, opened);
        Assert.Equal(new DataRootLocationState(_source, null), store.State);
    }

    private static DataRootBackupService Service(MemoryLocationStore store, long freeBytes = long.MaxValue) =>
        new(store, _ => freeBytes, new FixedTime());

    private DataRootRelocationMarker PendingRelocation() =>
        new(
            Guid.NewGuid(),
            _source,
            Path.Combine(_root, "Target"),
            TargetExisted: false,
            DataRootRelocationPhase.Copying,
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

    /// <summary>09:30:05 local time on 2026-09-23, in a fixed zone three hours ahead of UTC.</summary>
    private sealed class FixedTime : TimeProvider
    {
        private static readonly TimeZoneInfo Zone =
            TimeZoneInfo.CreateCustomTimeZone("Clipensk.Test", TimeSpan.FromHours(3), "Test", "Test");

        public override DateTimeOffset GetUtcNow() => new(2026, 9, 23, 6, 30, 5, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class MemoryLocationStore : IDataRootLocationStore
    {
        public MemoryLocationStore(string? dataRootPath)
        {
            State = new DataRootLocationState(dataRootPath, null);
        }

        public DataRootLocationState State { get; set; }

        public List<DataRootLocationState> Writes { get; } = [];

        public Task<DataRootLocationState> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task WriteAsync(DataRootLocationState state, CancellationToken cancellationToken = default)
        {
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
