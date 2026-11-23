using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 6: an incoming email is correlated with an earlier one by In-Reply-To/References (method 3), by tracking token
/// (method 4) and by conversation index (method 5), in that order; a hit copies the parent's regarding record.
/// </summary>
[Collection("xrmd")]
public sealed class EmailCorrelationTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private Task SetOrgAsync(string set) => TestSql.ExecAsync(fx.ConnectionString, $"UPDATE data.organization SET {set}");

    public Task InitializeAsync() => ResetAsync();

    public Task DisposeAsync() => ResetAsync();

    private Task ResetAsync() => SetOrgAsync($"trackingprefix = NULL, trackingtokeniddigits = NULL, {MailSchema.CorrelationEnabledColumn} = {TestSql.True}");

    private Task TokensOnAsync() => SetOrgAsync("trackingprefix = 'CRM:', trackingtokeniddigits = 4");

    private string NewId() => $"{Guid.NewGuid():N}.{_tag}@example.com";

    /// <summary>An email with the given stored columns; its Message-ID (when given) is registered as a key.</summary>
    private async Task<Guid> EmailAsync(string? messageId = null, string? token = null, string? index = null, EntityReference? regarding = null)
    {
        var e = new Entity("email") { ["subject"] = "Parent " + _tag };
        if (regarding is not null) e["regardingobjectid"] = regarding;
        var id = await Admin.CreateAsync(e);
        if (token is not null) await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET trackingtoken = @v WHERE activityid = @id", ("@v", token), ("@id", id));
        if (index is not null) await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.email SET conversationindex = @v WHERE activityid = @id", ("@v", index), ("@id", id));
        if (messageId is not null)
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await MailMessageKeys.RegisterOutgoingAsync(new DataSession(conn, null), id, messageId, default);
        }
        return id;
    }

    private async Task<EntityReference> AccountAsync() =>
        new("account", await Admin.CreateAsync(new Entity("account") { ["name"] = "Acme " + _tag }));

    private static MimeMessage Message(string? subject = "Re: hello", string? inReplyTo = null, params string[] references)
    {
        var m = new MimeMessage { Subject = subject! };
        if (inReplyTo is not null) m.InReplyTo = inReplyTo;
        foreach (var r in references) m.References.Add(r);
        return m;
    }

    private async Task<Correlation> CorrelateAsync(MimeMessage message, string? index = null)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await EmailCorrelator.CorrelateAsync(new DataSession(conn, null), fx.App.Services.GetRequiredService<IMetadataStore>(), message, index, default);
    }

    /// <summary>A token-shaped string (prefix CRM:, 18 digits) no other run or test has used.</summary>
    private static string UniqueToken() => "CRM:" + new System.Numerics.BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true).ToString()[..18];

    // Random bytes (the seed is ignored): an index never repeats across runs on a shared database.
    private static string Index(int children, byte seed = 1) =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Take(22)
            .Concat(Enumerable.Range(0, 5 * children).Select(_ => (byte)Random.Shared.Next(256))).ToArray());

    [Fact]
    public async Task InReplyTo_SentEmail_CopiesRegarding_Method3()
    {
        var regarding = await AccountAsync();
        var id = NewId();
        var parent = await EmailAsync(messageId: id, regarding: regarding);

        var result = await CorrelateAsync(Message(inReplyTo: id));

        result.Method.Should().Be(3);
        result.ParentEmailId.Should().Be(parent);
        result.Regarding.Should().NotBeNull();
        result.Regarding!.LogicalName.Should().Be("account");
        result.Regarding.Id.Should().Be(regarding.Id);
    }

    [Fact]
    public async Task References_OnlyLastKnown_Hits()
    {
        var known = NewId();
        var parent = await EmailAsync(messageId: known);

        // References are tried from the last to the first; only the first one is known here.
        var result = await CorrelateAsync(Message(references: [known, NewId(), NewId()]));

        result.Method.Should().Be(3);
        result.ParentEmailId.Should().Be(parent);
    }

    [Fact]
    public async Task InReplyTo_UnknownId_FallsThrough()
    {
        var known = NewId();
        var parent = await EmailAsync(messageId: known);

        var result = await CorrelateAsync(Message(inReplyTo: NewId(), references: [known]));

        result.Method.Should().Be(3);
        result.ParentEmailId.Should().Be(parent);
    }

    [Fact]
    public async Task Token_TrackingOff_Ignored()
    {
        var token = UniqueToken();
        await EmailAsync(token: token);

        var result = await CorrelateAsync(Message(subject: $"Re: hello {token}"));

        result.Method.Should().Be(0);
        result.ParentEmailId.Should().BeNull();
    }

    [Fact]
    public async Task Token_StoredOnEmail_Method4()
    {
        await TokensOnAsync();
        var token = UniqueToken();
        var regarding = await AccountAsync();
        var parent = await EmailAsync(token: token, regarding: regarding);

        var result = await CorrelateAsync(Message(subject: $"Re: hello {token}"));

        result.Method.Should().Be(4);
        result.ParentEmailId.Should().Be(parent);
        result.Regarding!.Id.Should().Be(regarding.Id);
    }

    [Fact]
    public async Task Token_NotStoredOnAnyEmail_NoHit()
    {
        await TokensOnAsync();

        var result = await CorrelateAsync(Message(subject: "Re: hello " + UniqueToken()));

        result.Method.Should().Be(0);
        result.ParentEmailId.Should().BeNull();
        result.Regarding.Should().BeNull();
    }

    [Fact]
    public async Task Token_TwoTokens_LastStoredOneWins()
    {
        await TokensOnAsync();
        var first = UniqueToken();
        var second = UniqueToken();
        var firstEmail = await EmailAsync(token: first);
        var secondEmail = await EmailAsync(token: second);

        (await CorrelateAsync(Message(subject: $"Fwd: {first} Re: {second}"))).ParentEmailId.Should().Be(secondEmail);

        // The last token of the subject stored on no email is no hit: the one before it is.
        var unknown = UniqueToken();
        var result = await CorrelateAsync(Message(subject: $"Re: {first} {unknown}"));
        result.Method.Should().Be(4);
        result.ParentEmailId.Should().Be(firstEmail);
    }

    [Fact]
    public async Task ConversationIndex_ParentMatch_Method5()
    {
        var regarding = await AccountAsync();
        var parentIndex = Index(1, seed: (byte)1);
        var parent = await EmailAsync(index: parentIndex, regarding: regarding);
        var child = Convert.ToBase64String([.. Convert.FromBase64String(parentIndex), .. new byte[] { 9, 8, 7, 6, 5 }]);

        var result = await CorrelateAsync(Message(), child);

        result.Method.Should().Be(5);
        result.ParentEmailId.Should().Be(parent);
        result.Regarding!.Id.Should().Be(regarding.Id);
    }

    [Fact]
    public async Task ConversationIndex_FromThreadIndexHeader_Method5()
    {
        // Ruling T6-a: without an index argument (IMAP), the message's Thread-Index header is used.
        var parentIndex = Index(2, seed: (byte)1);
        var parent = await EmailAsync(index: parentIndex);
        var child = Convert.ToBase64String([.. Convert.FromBase64String(parentIndex), .. new byte[] { 1, 2, 3, 4, 5 }]);
        var message = Message();
        message.Headers.Add("Thread-Index", child);

        var result = await CorrelateAsync(message, index: null);

        result.Method.Should().Be(5);
        result.ParentEmailId.Should().Be(parent);
    }

    [Fact]
    public async Task CorrelationDisabled_SkipsHeadersAndIndex_KeepsToken()
    {
        await TokensOnAsync();
        await SetOrgAsync($"{MailSchema.CorrelationEnabledColumn} = {TestSql.False}");
        var id = NewId();
        await EmailAsync(messageId: id);
        var parentIndex = Index(1, seed: (byte)1);
        await EmailAsync(index: parentIndex);
        var child = Convert.ToBase64String([.. Convert.FromBase64String(parentIndex), .. new byte[] { 1, 2, 3, 4, 5 }]);

        (await CorrelateAsync(Message(inReplyTo: id), child)).Method.Should().Be(0);

        var token = UniqueToken();
        var tokenEmail = await EmailAsync(token: token);
        var result = await CorrelateAsync(Message(subject: "Re: " + token, inReplyTo: id), child);
        result.Method.Should().Be(4);
        result.ParentEmailId.Should().Be(tokenEmail);
    }

    [Fact]
    public async Task NoHit_Method0_NoRegarding()
    {
        var result = await CorrelateAsync(Message(inReplyTo: NewId(), references: [NewId()]), Index(1, seed: 77));

        result.Should().Be(new Correlation(0, null, null));
    }

    [Fact]
    public async Task Hit_ParentWithoutRegarding_SetsParentOnly()
    {
        var id = NewId();
        var parent = await EmailAsync(messageId: id);

        var result = await CorrelateAsync(Message(inReplyTo: id));

        result.Method.Should().Be(3);
        result.ParentEmailId.Should().Be(parent);
        result.Regarding.Should().BeNull();
    }

    [Fact]
    public async Task Hit_RegardingTypeUnknown_Throws()
    {
        var id = NewId();
        var parent = await EmailAsync(messageId: id);
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE data.email SET regardingobjectid = @r, regardingobjectidtype = 2147000000 WHERE activityid = @id", ("@r", Guid.NewGuid()), ("@id", parent));

        var act = () => CorrelateAsync(Message(inReplyTo: id));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2147000000*");
    }
}
