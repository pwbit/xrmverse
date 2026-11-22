using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

public sealed class FileSystemFileStoreTests : FileStoreContract
{
    private string _root = "";

    protected override Task<IFileStore> CreateStore()
    {
        _root = Path.Combine(Path.GetTempPath(), "xrmd-files-" + Guid.NewGuid().ToString("N"));
        return Task.FromResult<IFileStore>(new FileSystemFileStore(_root));
    }

    protected override Task CleanupAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Check_is_ok_for_an_existing_directory_and_leaves_nothing_behind()
    {
        Directory.CreateDirectory(_root);
        var check = await Store.CheckAsync(default);
        check.IsOk.Should().BeTrue(check.Message);
        check.Message.Should().Contain(_root);
        Directory.GetFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_warns_for_a_missing_directory_that_can_be_created_and_does_not_create_it()
    {
        var check = await Store.CheckAsync(default);
        check.Status.Should().Be(FileStoreStatus.Warning);
        check.Message.Should().Contain(_root).And.Contain("created on the first write");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task Check_fails_for_a_directory_that_is_not_writable()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root" || geteuid() == 0) return; // root ignores permissions
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var check = await Store.CheckAsync(default);
            check.Status.Should().Be(FileStoreStatus.Error);
            check.Message.Should().Contain(_root).And.Contain("not writable");
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();

    [Fact]
    public async Task Check_fails_when_the_root_is_a_file_or_its_parent_is_a_file()
    {
        Directory.CreateDirectory(Path.GetTempPath());
        var file = Path.Combine(Path.GetTempPath(), "xrmd-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "x");
        try
        {
            (await new FileSystemFileStore(file).CheckAsync(default)).Status.Should().Be(FileStoreStatus.Error);
            var below = await new FileSystemFileStore(Path.Combine(file, "sub")).CheckAsync(default);
            below.Status.Should().Be(FileStoreStatus.Error);
            below.Message.Should().Contain("cannot be created");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Write_creates_parent_directories_and_leaves_no_temp_files()
    {
        await Store.WriteAsync("a/b/c/file", new MemoryStream([1, 2, 3]), default);
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("a", "b", "c", "file"));
    }

    [Fact]
    public async Task Failed_write_leaves_the_old_blob_and_no_temp_file()
    {
        await Store.WriteAsync("a/f", new MemoryStream([1]), default);
        var act = async () => await Store.WriteAsync("a/f", new ThrowingStream(), default);
        await act.Should().ThrowAsync<IOException>();
        (await Store.GetLengthAsync("a/f", default)).Should().Be(1);
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Theory]
    [InlineData("/tmp/x", "/tmp/x/f", true)]
    [InlineData("/tmp/x", "/tmp/x/a/b", true)]
    [InlineData("/tmp/x", "/tmp/x-evil/f", false)]
    [InlineData("/tmp/x", "/tmp/x", false)]
    [InlineData("/tmp/x", "/tmp/y/f", false)]
    [InlineData("/tmp/x/", "/tmp/x/f", true)]
    public void Root_guard_rejects_sibling_directories_sharing_the_root_name(string root, string path, bool inside) =>
        FileSystemFileStore.IsUnderRoot(root, path).Should().Be(inside);

    [Fact]
    public async Task DeletePrefix_removes_the_emptied_prefix_directory_but_keeps_siblings()
    {
        await Store.WriteAsync("org/_upload/s1/0", new MemoryStream([1]), default);
        await Store.WriteAsync("org/_upload/s2/0", new MemoryStream([1]), default);
        await Store.DeletePrefixAsync("org/_upload/s1/", default);
        Directory.Exists(Path.Combine(_root, "org", "_upload", "s1")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "org", "_upload", "s2")).Should().BeTrue();
    }

    [Fact]
    public async Task Writes_survive_concurrent_DeletePrefix_pruning_in_the_same_directory()
    {
        // A non-slash prefix makes DeletePrefix prune empty directories across all of org/_upload,
        // including the writers' freshly created (still empty) directories.
        using var stop = new CancellationTokenSource();
        var deleters = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await Store.DeletePrefixAsync("org/_upload/g", default);
        })).ToArray();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 1000; i++)
                await Store.WriteAsync($"org/_upload/w{w}/d{i}/f", new MemoryStream([2]), default);
        })).ToArray();
        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await Task.WhenAll(deleters);
        var count = 0;
        await foreach (var _ in Store.ListAsync("org/_upload/w", default)) count++;
        count.Should().Be(4 * 1000);
    }

    [Fact]
    public async Task OpenRead_of_a_directory_key_is_FileNotFound()
    {
        await Store.WriteAsync("org/dir/file", new MemoryStream([1]), default);
        var act = async () => await Store.OpenReadAsync("org/dir", 0, null, default);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task PurgeStaleTempFiles_removes_only_old_leftovers()
    {
        await Store.WriteAsync("org/real", new MemoryStream([1]), default);
        var old = Path.Combine(_root, "org", "a.0123.xrmd-tmp");
        var fresh = Path.Combine(_root, "org", "b.4567.xrmd-tmp");
        await File.WriteAllBytesAsync(old, [1]);
        await File.WriteAllBytesAsync(fresh, [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        var purged = await ((FileSystemFileStore)Store).PurgeStaleTempFilesAsync(TimeSpan.FromHours(1), default);

        purged.Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
        (await Store.GetLengthAsync("org/real", default)).Should().Be(1);
    }

    [Theory]
    [InlineData(null, "FileSystem")]
    [InlineData("FileSystem", "FileSystem")]
    [InlineData("filesystem", "FileSystem")]
    public void Registration_selects_the_file_system_store(string? configured, string expected)
    {
        var values = new Dictionary<string, string?> { ["Files:FileSystem:Root"] = Path.Combine(Path.GetTempPath(), "xrmd-unused") };
        if (configured != null) values["Files:Store"] = configured;
        var provider = new ServiceCollection()
            .AddXrmdFiles(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();
        provider.GetRequiredService<IFileStore>().Should().BeOfType<FileSystemFileStore>();
        provider.GetRequiredService<FileStoreOptions>().Store.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("1")]
    public void Unknown_store_fails_startup_naming_key_and_allowed_values(string value)
    {
        var act = () => new ServiceCollection().AddXrmdFiles(Config("Files:Store", value));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Files:Store*FileSystem*S3Compatible*");
    }

    [Fact]
    public void Note_body_read_cap_defaults_to_64_MB_and_reads_the_setting()
    {
        var root = Path.Combine(Path.GetTempPath(), "xrmd-unused");
        new ServiceCollection().AddXrmdFiles(Config("Files:FileSystem:Root", root)).BuildServiceProvider()
            .GetRequiredService<FileStoreOptions>().MaxNoteBodyBytesPerRead.Should().Be(64L * 1024 * 1024);
        var values = new Dictionary<string, string?> { ["Files:FileSystem:Root"] = root, ["Files:MaxNoteBodyBytesPerRead"] = "1000" };
        new ServiceCollection().AddXrmdFiles(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider()
            .GetRequiredService<FileStoreOptions>().MaxNoteBodyBytesPerRead.Should().Be(1000);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("big")]
    public void Invalid_note_body_read_cap_fails_startup(string value)
    {
        var act = () => new ServiceCollection().AddXrmdFiles(Config("Files:MaxNoteBodyBytesPerRead", value));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Files:MaxNoteBodyBytesPerRead*");
    }

    private static IConfiguration Config(string key, string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    private sealed class ThrowingStream : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_first) throw new IOException("boom");
            _first = false;
            buffer[offset] = 9;
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
