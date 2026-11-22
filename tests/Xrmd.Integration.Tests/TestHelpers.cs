using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>Helpers shared by the integration tests (faults, labels, entity definitions).</summary>
internal static class TestHelpers
{
    /// <summary>
    /// Waits until the host's background blob deletions (<see cref="Xrmd.Files.FileDeletionQueue"/>: files released by a commit,
    /// written by a rolled-back one, staged blocks of ended sessions) have run, before a test reads the file store.
    /// </summary>
    public static Task DrainFileDeletionsAsync(this XrmdFixture fx) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Xrmd.Files.FileDeletionQueue>(fx.App.Services)
            .WhenIdleAsync(TimeSpan.FromSeconds(30));

    /// <summary>The <see cref="XrmdFaultException"/> <paramref name="call"/> throws (the assertion fails otherwise).</summary>
    public static async Task<XrmdFaultException> FaultAsync(Func<Task> call) =>
        (await call.Should().ThrowAsync<XrmdFaultException>()).Which;

    /// <summary>
    /// The message of the 400 <paramref name="call"/> fails with: an <see cref="XrmdFaultException"/> with status 400, or an
    /// <see cref="XrmdValidationException"/> (which every front end answers with 400).
    /// </summary>
    public static async Task<string> BadRequestAsync(Func<Task> call)
    {
        var thrown = (await call.Should().ThrowAsync<Exception>()).Which;
        if (thrown is XrmdFaultException fault) fault.HttpStatus.Should().Be(400, fault.ToString());
        else thrown.Should().BeOfType<XrmdValidationException>(thrown.ToString());
        return thrown.Message;
    }

    /// <summary>A label in English (1033).</summary>
    public static Label Label(string text) => new(text, 1033);

    /// <summary>A user-owned entity definition named <paramref name="displayName"/> (1033) with <paramref name="attributes"/>.</summary>
    public static EntityMetadata Entity(string logicalName, string schemaName, string displayName, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata
        {
            LogicalName = logicalName,
            SchemaName = schemaName,
            OwnershipType = OwnershipTypes.UserOwned,
            DisplayName = SystemAttributes.CreateLabel(displayName, 1033),
        };
        entity.SetAttributes(attributes);
        return entity;
    }
}
