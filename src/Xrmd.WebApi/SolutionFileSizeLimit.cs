using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The request body limit of the routes that carry a solution file (the SOAP endpoint, which carries
/// <c>ImportSolution</c>/<c>ImportSolutionAsync</c>/<c>ExecuteAsync</c>, and the Web API actions <c>ImportSolution</c>,
/// <c>ImportSolutionAsync</c>, <c>StageAndUpgrade</c> and <c>StageAndUpgradeAsync</c>) and of the routes that carry a file (the <c>PATCH</c> of a file or image column, whose
/// single-request upload is "less than 128 MB" in Learn's "Use file column data", and the <c>UploadBlock</c> action, a 4 MB
/// block that is about 5.6 MB once base64 encoded): 128 MB, Dataverse's maximum request payload (Learn "Work with data using
/// code", "Request and Response payload size limitations"), so that a solution file up to Dataverse's 95 MB fits once base64
/// encoded. Every other route keeps Kestrel's default (30,000,000 bytes). A larger body is 413, as in Dataverse.
/// </summary>
public sealed class SolutionFileSizeLimit : IRequestSizeLimitMetadata
{
    /// <summary>128 MB.</summary>
    public const long MaxBytes = 128L * 1024 * 1024;

    private static readonly SolutionFileSizeLimit Instance = new();

    /// <inheritdoc />
    public long? MaxRequestBodySize => MaxBytes;

    /// <summary>Applies <see cref="MaxBytes"/> to the request body of <paramref name="builder"/>'s endpoints.</summary>
    public static TBuilder ApplyTo<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(Instance);
}
