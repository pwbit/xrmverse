namespace Xrmd.Files;

/// <summary>The <c>Files:Store</c> values.</summary>
public enum FileStoreKind
{
    FileSystem,
    S3Compatible,
    AzureBlob,
}

/// <summary>Configuration of the file store (section <c>Files</c>).</summary>
public sealed class FileStoreOptions
{
    public const string DefaultFileSystemRoot = "/var/lib/xrmd/files";

    public FileStoreKind Store { get; set; } = FileStoreKind.FileSystem;

    /// <summary><c>Files:FileSystem:Root</c>.</summary>
    public string FileSystemRoot { get; set; } = DefaultFileSystemRoot;

    /// <summary>The default of <see cref="MaxNoteBodyBytesPerRead"/>: 64 MB.</summary>
    public const long DefaultMaxNoteBodyBytesPerRead = 64L * 1024 * 1024;

    /// <summary>
    /// <c>Files:MaxNoteBodyBytesPerRead</c>: the most bytes of note bodies (<c>annotation.documentbody</c>) one query reads into
    /// memory (plan 2.11, controller ruling Y5). A page whose bodies are larger is refused with a 400 before any blob is read. A
    /// server-side bound, not a Dataverse rule (known limit, checklist).
    /// </summary>
    public long MaxNoteBodyBytesPerRead { get; set; } = DefaultMaxNoteBodyBytesPerRead;

    /// <summary><c>Files:S3:*</c>; only meaningful (and validated) when <see cref="Store"/> is <see cref="FileStoreKind.S3Compatible"/>.</summary>
    public S3FileStoreOptions S3 { get; set; } = new();

    /// <summary><c>Files:Azure:*</c>; only meaningful (and validated) when <see cref="Store"/> is <see cref="FileStoreKind.AzureBlob"/>.</summary>
    public AzureBlobFileStoreOptions Azure { get; set; } = new();
}

/// <summary>Settings of the S3-compatible store (section <c>Files:S3</c>).</summary>
public sealed class S3FileStoreOptions
{
    public const string DefaultRegion = "us-east-1";

    /// <summary><c>Files:S3:ServiceUrl</c>, e.g. <c>http://minio:9000</c>. Required.</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary><c>Files:S3:Bucket</c>. Required; the bucket must exist.</summary>
    public string Bucket { get; set; } = "";

    /// <summary><c>Files:S3:AccessKey</c>. Required.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary><c>Files:S3:SecretKey</c>. Required.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary><c>Files:S3:Region</c>, used for request signing. Default <c>us-east-1</c>.</summary>
    public string Region { get; set; } = DefaultRegion;

    /// <summary><c>Files:S3:ForcePathStyle</c>. Default <c>true</c> (what MinIO and most self-hosted stores need).</summary>
    public bool ForcePathStyle { get; set; } = true;
}

/// <summary>Settings of the Azure Blob store (section <c>Files:Azure</c>). Set exactly one of <see cref="ConnectionString"/> and <see cref="ServiceUri"/>.</summary>
public sealed class AzureBlobFileStoreOptions
{
    /// <summary><c>Files:Azure:ConnectionString</c>: an account key or SAS connection string (also what Azurite uses). A secret; never logged.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary><c>Files:Azure:ServiceUri</c>: the blob endpoint, e.g. <c>https://account.blob.core.windows.net</c>. Authenticates with Microsoft Entra ID (<c>DefaultAzureCredential</c>).</summary>
    public string ServiceUri { get; set; } = "";

    /// <summary><c>Files:Azure:Container</c>. Required; the container must exist (the store never creates it).</summary>
    public string Container { get; set; } = "";
}
