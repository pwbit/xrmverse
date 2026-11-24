using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7a, Task 5 (Review Focus: a <see cref="DateTime"/> written as UTC reads back with the same <see cref="DateTime.Kind"/> and
/// value on both providers; Npgsql refuses an unspecified kind with <c>timestamptz</c>). Runs on the run's provider
/// (<c>XRMD_TEST_PROVIDER</c>) and asserts against constants, so both providers answer the same.
/// </summary>
[Collection("xrmd")]
public sealed class DateTimeRoundTripTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string BirthDate = "pb_birthdate", When = "pb_roundtripon";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>The value written: 2026-11-19T10:15:30Z.</summary>
    private static readonly DateTime Written = new(2026, 11, 19, 10, 15, 30, DateTimeKind.Utc);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            var contact = Store.GetEntity("contact")!;
            if (contact.Attributes.All(a => a.LogicalName != BirthDate))
                await Store.AddAttributeAsync("contact", new DateTimeAttributeMetadata
                {
                    LogicalName = BirthDate, SchemaName = "pb_BirthDate",
                    DateTimeBehavior = DateTimeBehavior.DateOnly, Format = DateTimeFormat.DateOnly,
                });
            if (Store.GetEntity("contact")!.Attributes.All(a => a.LogicalName != When))
                await Store.AddAttributeAsync("contact", new DateTimeAttributeMetadata
                {
                    LogicalName = When, SchemaName = "pb_RoundTripOn",
                    DateTimeBehavior = DateTimeBehavior.UserLocal, Format = DateTimeFormat.DateAndTime,
                });
        }
        finally
        {
            Lock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_utc_datetime_reads_back_with_the_same_value_and_kind()
    {
        var svc = ServiceFor(fx, fx.AdminObjectId);
        var id = await svc.CreateAsync(new Entity("contact")
        {
            ["lastname"] = "RoundTrip " + Guid.NewGuid().ToString("N")[..8],
            [BirthDate] = Written,
            [When] = Written,
        });

        var read = await svc.RetrieveAsync("contact", id, new ColumnSet(BirthDate, When, "createdon"));

        var when = read.GetAttributeValue<DateTime>(When);
        when.Should().Be(new DateTime(2026, 11, 19, 10, 15, 30, DateTimeKind.Utc));
        when.Kind.Should().Be(DateTimeKind.Utc);

        // Date only: xrmverse stores the value as written (the SQL Server answer, pinned for both providers).
        var birth = read.GetAttributeValue<DateTime>(BirthDate);
        birth.Should().Be(new DateTime(2026, 11, 19, 10, 15, 30, DateTimeKind.Utc));
        birth.Kind.Should().Be(DateTimeKind.Utc);

        read.GetAttributeValue<DateTime>("createdon").Kind.Should().Be(DateTimeKind.Utc);
    }
}
