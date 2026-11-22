using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Xrmd.Files;

public static class FilesServiceCollectionExtensions
{
    private static readonly string AllowedStores = string.Join(", ", Enum.GetNames<FileStoreKind>());

    /// <summary>
    /// Registers <see cref="IFileStore"/> selected by <c>Files:Store</c>. The configuration is validated here, so a bad
    /// value fails host composition (startup) instead of the first upload.
    /// </summary>
    public static IServiceCollection AddXrmdFiles(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ReadOptions(configuration);
        services.AddSingleton(options);
        switch (options.Store)
        {
            case FileStoreKind.FileSystem:
                services.AddSingleton<IFileStore>(_ => new FileSystemFileStore(options.FileSystemRoot));
                break;
            case FileStoreKind.S3Compatible:
                services.AddSingleton<IFileStore>(_ => new S3FileStore(options.S3));
                break;
            case FileStoreKind.AzureBlob:
                services.AddSingleton<IFileStore>(_ => new AzureBlobFileStore(options.Azure));
                break;
        }
        return services;
    }

    private static FileStoreOptions ReadOptions(IConfiguration configuration)
    {
        var options = new FileStoreOptions();
        var store = configuration["Files:Store"];
        if (!string.IsNullOrWhiteSpace(store))
        {
            // Enum.TryParse also accepts numbers; only the names are allowed values.
            if (!Enum.GetNames<FileStoreKind>().Contains(store, StringComparer.OrdinalIgnoreCase)
                || !Enum.TryParse<FileStoreKind>(store, ignoreCase: true, out var kind))
                throw new InvalidOperationException($"Files:Store '{store}' is not valid; allowed values: {AllowedStores}.");
            options.Store = kind;
        }
        var root = configuration["Files:FileSystem:Root"];
        if (!string.IsNullOrWhiteSpace(root)) options.FileSystemRoot = root;
        var cap = configuration["Files:MaxNoteBodyBytesPerRead"];
        if (!string.IsNullOrWhiteSpace(cap))
        {
            if (!long.TryParse(cap, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) || bytes < 1)
                throw new InvalidOperationException($"Files:MaxNoteBodyBytesPerRead '{cap}' is not valid; use a whole number of bytes, 1 or more.");
            options.MaxNoteBodyBytesPerRead = bytes;
        }
        if (options.Store == FileStoreKind.S3Compatible) ReadS3Options(configuration, options.S3);
        if (options.Store == FileStoreKind.AzureBlob) ReadAzureOptions(configuration, options.Azure);
        return options;
    }

    private static void ReadS3Options(IConfiguration configuration, S3FileStoreOptions s3)
    {
        s3.ServiceUrl = Required(configuration, "ServiceUrl");
        s3.Bucket = Required(configuration, "Bucket");
        s3.AccessKey = Required(configuration, "AccessKey");
        s3.SecretKey = Required(configuration, "SecretKey");
        if (!Uri.TryCreate(s3.ServiceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Files:S3:ServiceUrl is not a valid absolute http(s) URL.");
        var region = configuration["Files:S3:Region"];
        if (!string.IsNullOrWhiteSpace(region)) s3.Region = region;
        var pathStyle = configuration["Files:S3:ForcePathStyle"];
        if (!string.IsNullOrWhiteSpace(pathStyle))
        {
            if (!bool.TryParse(pathStyle, out var value))
                throw new InvalidOperationException($"Files:S3:ForcePathStyle '{pathStyle}' is not valid; use true or false.");
            s3.ForcePathStyle = value;
        }
    }

    private static void ReadAzureOptions(IConfiguration configuration, AzureBlobFileStoreOptions azure)
    {
        azure.Container = Required(configuration, "Azure", "Container", "AzureBlob");
        var connectionString = configuration["Files:Azure:ConnectionString"];
        var serviceUri = configuration["Files:Azure:ServiceUri"];
        var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString);
        var hasServiceUri = !string.IsNullOrWhiteSpace(serviceUri);
        // The messages name the settings only: the connection string carries a key.
        if (hasConnectionString == hasServiceUri)
            throw new InvalidOperationException("Files:Store is 'AzureBlob': set exactly one of Files:Azure:ConnectionString and Files:Azure:ServiceUri.");
        if (hasConnectionString)
        {
            try
            {
                _ = new Azure.Storage.Blobs.BlobServiceClient(connectionString);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or UriFormatException or InvalidOperationException)
            {
                // Dropped on purpose, inner exception included: parser messages may quote the string.
                throw new InvalidOperationException("Files:Azure:ConnectionString is not a valid Azure Storage connection string.");
            }
            azure.ConnectionString = connectionString!;
            return;
        }
        // https only: the Azure SDK refuses a token credential over http. Local tests (Azurite) use a connection string.
        if (!Uri.TryCreate(serviceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Files:Azure:ServiceUri is not a valid absolute https URL.");
        azure.ServiceUri = serviceUri!;
    }

    private static string Required(IConfiguration configuration, string name) => Required(configuration, "S3", name, "S3Compatible");

    private static string Required(IConfiguration configuration, string section, string name, string store)
    {
        var value = configuration[$"Files:{section}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Files:Store is '{store}' but the required setting Files:{section}:{name} is missing.");
        return value;
    }
}
