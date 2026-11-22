using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>Raw-HTTP checks of the SOAP organization endpoint (the ServiceClient acceptance tests cover the happy path).</summary>
[Collection("xrmd")]
public sealed class OrganizationSoapEndpointTests(XrmdFixture fx)
{
    private const string Path = "XRMServices/2011/Organization.svc/web";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Contracts = "http://schemas.microsoft.com/xrm/2011/Contracts";

    private static readonly XNamespace Services = "http://schemas.microsoft.com/xrm/2011/Contracts/Services";

    private async Task<HttpResponseMessage> PostAsync(string body, bool authenticated = true, Guid? objectId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        if (authenticated)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    internal static string Envelope(string body, string header = "") =>
        $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">{header}<s:Body>{body}</s:Body></s:Envelope>""";

    /// <summary>An Execute body as the SDK client serializes it (SDK data contracts + KnownTypesResolver).</summary>
    internal static string Execute(OrganizationRequest request)
    {
        var serializer = new DataContractSerializer(typeof(OrganizationRequest), new DataContractSerializerSettings
        {
            RootName = new XmlDictionary().Add("request"),
            RootNamespace = new XmlDictionary().Add(Services.NamespaceName),
            DataContractResolver = new KnownTypesResolver(),
        });
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            w.WriteStartElement("Execute", Services.NamespaceName);
            serializer.WriteObject(w, request);
            w.WriteEndElement();
        }
        return sb.ToString();
    }

    private static async Task<(int Code, string Message)> FaultAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/xml");
        var fault = XDocument.Parse(await res.Content.ReadAsStringAsync()).Descendants(Soap + "Fault").Single();
        var detail = fault.Element("detail")!.Element(Contracts + "OrganizationServiceFault")!;
        return ((int)detail.Element(Contracts + "ErrorCode")!, (string)detail.Element(Contracts + "Message")!);
    }

    [Fact]
    public async Task Without_token_returns_401()
    {
        var res = await PostAsync(Envelope("<Execute/>"), authenticated: false);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Execute_WhoAmI_returns_ExecuteResponse_envelope()
    {
        var res = await PostAsync(Envelope(Execute(new WhoAmIRequest())));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/xml");
        var body = XDocument.Parse(await res.Content.ReadAsStringAsync()).Root!.Element(Soap + "Body")!;
        var result = body.Element(Services + "ExecuteResponse")!.Element(Services + "ExecuteResult")!;
        var type = (string)result.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!;
        type.Should().EndWith(":WhoAmIResponse");
        result.GetNamespaceOfPrefix(type.Split(':')[0])!.NamespaceName.Should().Be("http://schemas.microsoft.com/crm/2011/Contracts");
        ((string)result.Element(Contracts + "ResponseName")!).Should().Be("WhoAmI");
        result.Element(Contracts + "Results")!.Elements().Should().HaveCount(3);
    }

    [Fact]
    public async Task Caller_without_systemuser_gets_NotMemberOfOrganization_fault()
    {
        var res = await PostAsync(Envelope(Execute(new OrganizationRequest("RetrieveEntity") { ["LogicalName"] = "systemuser" })), objectId: Guid.NewGuid());

        (await FaultAsync(res)).Code.Should().Be(ErrorCodes.NotMemberOfOrganization);
    }

    [Fact]
    public async Task Malformed_xml_returns_InvalidArgument_fault()
    {
        var (code, message) = await FaultAsync(await PostAsync("<s:Envelope"));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Be("The SOAP request is malformed.");
    }

    [Fact]
    public async Task Doctype_returns_InvalidArgument_fault()
    {
        var body = """<!DOCTYPE s [<!ENTITY x "x">]>""" + Envelope(Execute(new WhoAmIRequest()));

        var (code, message) = await FaultAsync(await PostAsync(body));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Be("The SOAP request is malformed.");
    }

    [Fact]
    public async Task DataSet_parameter_is_refused()
    {
        // Serialize a string parameter, then retype it as the DataSet that KnownTypesResolver would otherwise resolve.
        var body = Execute(new OrganizationRequest("WhoAmI") { ["Target"] = "payload" });
        body.Should().Contain(":string\"");
        body = body.Replace(":string\"", ":DataSet\"").Replace(">payload<", "><x/><");

        var (code, message) = await FaultAsync(await PostAsync(Envelope(body)));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Be("The SOAP request is malformed.");
    }

    [Fact]
    public async Task Soap_request_with_non_sdk_type_is_rejected()
    {
        // KnownTypesResolver maps xsd:DBNull to System.DBNull: neither an SDK type nor an allowed primitive.
        var body = Execute(new OrganizationRequest("WhoAmI") { ["Target"] = "payload" });
        body.Should().Contain(":string\"");
        body = body.Replace(":string\"", ":DBNull\"").Replace(">payload<", "><");

        var (code, message) = await FaultAsync(await PostAsync(Envelope(body)));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Be("The SOAP request is malformed.");
    }

    private static string CallerIdHeader(Guid id) =>
        $"""<s:Header><CallerId xmlns="http://schemas.microsoft.com/xrm/2011/Contracts">{id}</CallerId></s:Header>""";

    [Fact]
    public async Task CallerId_header_impersonates()
    {
        var unit = await SecurityTestHelper.CreateBusinessUnitAsync(fx, await SecurityTestHelper.RootBusinessUnitAsync(fx), "SOAP-" + Guid.NewGuid().ToString("N"));
        var impersonated = await SecurityTestHelper.CreateUserAsync(fx, unit);
        await SecurityTestHelper.GrantAsync(fx, impersonated, false, ("prvReadpb_Project", DepthMask.Basic));
        var delegateOid = Guid.NewGuid();
        var delegateId = await SecurityTestHelper.CreateUserAsync(fx, unit, delegateOid);
        await SecurityTestHelper.AssignRoleAsync(fx, delegateId, StandardRoles.Delegate);

        var response = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new WhoAmIRequest()), CallerIdHeader(impersonated)),
            objectId: delegateOid));

        var whoAmI = response.Should().BeOfType<WhoAmIResponse>().Subject;
        whoAmI.UserId.Should().Be(impersonated);
        whoAmI.BusinessUnitId.Should().Be(unit);
        // Guid.Empty: no impersonation.
        ((WhoAmIResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(new WhoAmIRequest()), CallerIdHeader(Guid.Empty)),
            objectId: delegateOid))).UserId.Should().Be(delegateId);
    }

    [Fact]
    public async Task CallerId_header_without_privilege_is_fault_0x8004a110()
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(fx);
        var impersonated = await SecurityTestHelper.CreateUserAsync(fx, root);
        await SecurityTestHelper.GrantAsync(fx, impersonated, false, ("prvReadpb_Project", DepthMask.Basic));
        var plainOid = Guid.NewGuid();
        var plain = await SecurityTestHelper.CreateUserAsync(fx, root, plainOid);
        await SecurityTestHelper.GrantAsync(fx, plain, false, ("prvReadpb_Project", DepthMask.Global));

        var (code, message) = await FaultAsync(await PostAsync(Envelope(Execute(new WhoAmIRequest()), CallerIdHeader(impersonated)),
            objectId: plainOid));

        code.Should().Be(ErrorCodes.CannotActOnBehalfOfAnotherUser);
        message.Should().Be("The caller lacks prvActOnBehalfOfAnotherUser, which impersonating another user requires.");

        var (invalid, _) = await FaultAsync(await PostAsync(Envelope(Execute(new WhoAmIRequest()),
            """<s:Header><CallerId xmlns="http://schemas.microsoft.com/xrm/2011/Contracts">nope</CallerId></s:Header>""")));
        invalid.Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Header_after_Body_is_malformed()
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(fx);
        var impersonated = await SecurityTestHelper.CreateUserAsync(fx, root);
        var body = $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>{Execute(new WhoAmIRequest())}</s:Body>{CallerIdHeader(impersonated)}</s:Envelope>""";

        var (code, message) = await FaultAsync(await PostAsync(body));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Be("The SOAP request is malformed.");
    }

    [Fact]
    public async Task Operation_other_than_Execute_returns_fault()
    {
        var body = Envelope("""<Create xmlns="http://schemas.microsoft.com/xrm/2011/Contracts/Services"/>""");

        var (code, message) = await FaultAsync(await PostAsync(body));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Contain("'Create' is not supported");
    }

    [Fact]
    public async Task Unsupported_request_returns_fault()
    {
        var body = Envelope("""
            <Execute xmlns="http://schemas.microsoft.com/xrm/2011/Contracts/Services">
              <request xmlns:a="http://schemas.microsoft.com/xrm/2011/Contracts">
                <a:Parameters/><a:RequestId i:nil="true" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"/><a:RequestName>QualifyLead</a:RequestName>
              </request>
            </Execute>
            """);

        var (code, message) = await FaultAsync(await PostAsync(body));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Contain("'QualifyLead' is not supported");
    }

    /// <summary>Reads <c>ExecuteResponse/ExecuteResult</c> with the SDK data contracts, as the client does.</summary>
    internal static async Task<OrganizationResponse> ExecuteResultAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var result = XDocument.Parse(text).Root!.Element(Soap + "Body")!.Element(Services + "ExecuteResponse")!.Element(Services + "ExecuteResult")!;
        var serializer = new DataContractSerializer(typeof(OrganizationResponse), new DataContractSerializerSettings
        {
            RootName = new XmlDictionary().Add("ExecuteResult"),
            RootNamespace = new XmlDictionary().Add(Services.NamespaceName),
            DataContractResolver = new KnownTypesResolver(),
        });
        using var reader = result.CreateReader();
        return (OrganizationResponse)serializer.ReadObject(reader)!;
    }

    [Fact]
    public async Task Execute_RetrieveMultiple_over_soap()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var tag = "SOAP" + Guid.NewGuid().ToString("N");
        for (var i = 1; i <= 3; i++)
        {
            var created = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new CreateRequest
            {
                Target = new Entity(TestEntities.Project) { ["pb_name"] = $"{tag}-{i}", ["pb_count"] = i },
            }))));
            created.Should().BeOfType<CreateResponse>();
        }
        var query = new QueryExpression(TestEntities.Project) { ColumnSet = new ColumnSet("pb_name", "pb_count") };
        query.Criteria.AddCondition("pb_name", ConditionOperator.Like, tag + "%");
        query.AddOrder("pb_count", OrderType.Ascending);

        var response = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new RetrieveMultipleRequest { Query = query }))));

        var collection = response.Should().BeOfType<RetrieveMultipleResponse>().Subject.EntityCollection;
        collection.EntityName.Should().Be(TestEntities.Project);
        collection.Entities.Select(e => (int)e["pb_count"]).Should().Equal(1, 2, 3);
        collection.Entities.Select(e => (string)e["pb_name"]).Should().Equal($"{tag}-1", $"{tag}-2", $"{tag}-3");
        collection.Entities.Should().OnlyContain(e => e.Id != Guid.Empty);
        collection.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Deeply_nested_FetchExpression_over_soap_returns_InvalidArgument_fault()
    {
        var fetch = $"<fetch><entity name='{TestEntities.Project}'>{string.Concat(Enumerable.Repeat("<filter>", 10_000))}" +
                    $"<condition attribute='pb_name' operator='eq' value='x'/>{string.Concat(Enumerable.Repeat("</filter>", 10_000))}</entity></fetch>";

        var (code, message) = await FaultAsync(await PostAsync(Envelope(Execute(new RetrieveMultipleRequest { Query = new FetchExpression(fetch) }))));

        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Contain("nested more than 32");
    }

    [Fact]
    public async Task Soap_execute_assign_and_custom_action()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var assignee = Guid.NewGuid();
        try
        {
            // Custom action pb_Echo with its EchoActionPlugin step.
            await svc.CreateAsync(new Entity("sdkmessage", SdkMessageCatalog.MessageId("pb_Echo"))
            {
                ["name"] = "pb_Echo", ["categoryname"] = "CustomAction",
            });
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(svc, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(svc, assembly, "Xrmd.TestPlugins.EchoActionPlugin");
            await PluginTestHelper.RegisterStepAsync(svc, type, "pb_Echo", null, 40);

            var echo = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new OrganizationRequest("pb_Echo") { ["Text"] = "abc" }))));

            echo.ResponseName.Should().Be("pb_Echo");
            echo.Results["Result"].Should().Be("cba");

            // Assign a pb_pipe to another user.
            var id = await svc.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = "soap-assign" });
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var cmd = TestSql.Command(conn, """
                    INSERT INTO data.systemuser (systemuserid, fullname, domainname, businessunitid, owningbusinessunit,
                        createdon, modifiedon, statecode, statuscode)
                    SELECT @id, N'Soap Assignee', N'soap-assignee', businessunitid, businessunitid, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
                    FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid
                    """);
                cmd.Parameters.AddWithValue("@id", assignee);
                cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
                await cmd.ExecuteNonQueryAsync();
            }
            // A new owner must hold prvRead on the entity (rule of Assign).
            await SecurityTestHelper.GrantAsync(fx, assignee, team: false, ("prvReadpb_Pipe", DepthMask.Basic));

            var assigned = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new AssignRequest
            {
                Target = new EntityReference(TestEntities.Pipe, id),
                Assignee = new EntityReference("systemuser", assignee),
            }))));

            assigned.Should().BeOfType<AssignResponse>();
            (await svc.RetrieveAsync(TestEntities.Pipe, id, new ColumnSet("ownerid"))).GetAttributeValue<EntityReference>("ownerid").Id
                .Should().Be(assignee);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, $"DELETE FROM data.{TestEntities.Pipe} WHERE ownerid = @id; DELETE FROM data.systemuser WHERE systemuserid = @id");
            cmd.Parameters.AddWithValue("@id", assignee);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Soap_execute_grant_and_revoke_access()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var root = await SecurityTestHelper.RootBusinessUnitAsync(fx);
        var user = await SecurityTestHelper.CreateUserAsync(fx, root);
        await SecurityTestHelper.GrantAsync(fx, user, team: false, ("prvReadpb_Project", DepthMask.Basic));
        var id = await svc.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "soap-share" });

        async Task<object?> MaskAsync()
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @id AND principalid = @user");
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@user", user);
            return await cmd.ExecuteScalarAsync();
        }

        var granted = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new GrantAccessRequest
        {
            Target = new EntityReference(TestEntities.Project, id),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", user), AccessMask = AccessRights.ReadAccess },
        }))));
        granted.Should().BeOfType<GrantAccessResponse>();
        (await MaskAsync()).Should().Be((int)AccessRights.ReadAccess);

        var modified = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new ModifyAccessRequest
        {
            Target = new EntityReference(TestEntities.Project, id),
            PrincipalAccess = new PrincipalAccess
            {
                Principal = new EntityReference("systemuser", user), AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess,
            },
        }))));
        modified.Should().BeOfType<ModifyAccessResponse>();
        (await MaskAsync()).Should().Be((int)(AccessRights.ReadAccess | AccessRights.WriteAccess));

        var revoked = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new RevokeAccessRequest
        {
            Target = new EntityReference(TestEntities.Project, id), Revokee = new EntityReference("systemuser", user),
        }))));
        revoked.Should().BeOfType<RevokeAccessResponse>();
        (await MaskAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Soap_typed_AddUserToRecordTeamRequest_round_trips()
    {
        await using var s = await RecordTeamSetup.CreateAsync(fx);
        var response = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new AddUserToRecordTeamRequest
        {
            Record = s.Record, SystemUserId = s.User, TeamTemplateId = s.Template,
        }))));
        var team = response.Should().BeOfType<AddUserToRecordTeamResponse>().Which.AccessTeamId;
        team.Should().NotBeEmpty();
        var removed = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new RemoveUserFromRecordTeamRequest
        {
            Record = s.Record, SystemUserId = s.User, TeamTemplateId = s.Template,
        }))));
        removed.Should().BeOfType<RemoveUserFromRecordTeamResponse>().Which.AccessTeamId.Should().Be(team);
    }

    [Fact]
    public async Task Soap_fault_carries_0x80040333()
    {
        // Plan 2.15, ruling D12: the duplicate error of a typed CreateRequest reaches the SOAP client as OrganizationServiceFault.ErrorCode.
        var kit = new DuplicateDetectionTestKit(fx);
        await kit.InitializeAsync(onlineCreateUpdate: true, DuplicateDetectionTestKit.Contact);
        try
        {
            Entity Contact(string label) => new(DuplicateDetectionTestKit.Contact)
            {
                ["lastname"] = label + " " + kit.Run, [DuplicateDetectionTestKit.Email] = kit.Mail("soap"),
            };
            await kit.Admin.CreateAsync(Contact("Anna"));
            var rule = await kit.RuleAsync();
            await kit.PublishAndRunAsync(rule);

            var request = new CreateRequest { Target = Contact("Copy") };
            request["SuppressDuplicateDetection"] = false;
            var (code, message) = await FaultAsync(await PostAsync(Envelope(Execute(request))));

            code.Should().Be(ErrorCodes.DuplicateRecordsFound);
            code.Should().Be(unchecked((int)0x80040333));
            message.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await kit.DisposeAsync();
        }
    }

    [Fact]
    public async Task Soap_ExecuteAsync_queues_ImportSolution_and_refuses_other_requests()
    {
        var importJobId = Guid.NewGuid();
        var queued = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new ExecuteAsyncRequest
        {
            Request = new ImportSolutionRequest { CustomizationFile = [1, 2, 3], ImportJobId = importJobId },
        }))));
        try
        {
            var jobId = queued.Should().BeOfType<ExecuteAsyncResponse>().Which.AsyncJobId;
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND xrmverse_importjobid = @job");
            cmd.Parameters.AddWithValue("@id", jobId);
            cmd.Parameters.AddWithValue("@job", importJobId);
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(1);
        }
        finally
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "DELETE FROM data.asyncoperation WHERE xrmverse_importjobid = @job");
            cmd.Parameters.AddWithValue("@job", importJobId);
            await cmd.ExecuteNonQueryAsync();
        }

        var (code, message) = await FaultAsync(await PostAsync(Envelope(Execute(new ExecuteAsyncRequest { Request = new WhoAmIRequest() }))));
        code.Should().Be(ErrorCodes.InvalidArgument);
        message.Should().Contain("WhoAmI");
    }

    [Fact]
    public async Task Soap_retrieve_record_change_history_round_trips_audit_detail_types()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectTagAsync(store, fx.ConnectionString);
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        Guid organizationId;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid FROM data.organization");
            organizationId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        await svc.ExecuteAsync(new UpdateEntityRequest { Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            { LogicalName = TestEntities.Project, IsAuditEnabled = new BooleanManagedProperty(true) } });
        await svc.UpdateAsync(new Entity("organization", organizationId) { ["isauditenabled"] = true });
        try
        {
            var root = await SecurityTestHelper.RootBusinessUnitAsync(fx);
            var user = await SecurityTestHelper.CreateUserAsync(fx, root);
            await SecurityTestHelper.GrantAsync(fx, user, false, ("prvReadpb_Project", DepthMask.Basic));
            var project = await svc.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "soap-audit" });
            var tag = await svc.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "soap-tag" });
            await svc.UpdateAsync(new Entity(TestEntities.Project, project) { ["pb_name"] = "soap-audit-2" });
            await svc.ExecuteAsync(new AssociateRequest
            {
                Target = new EntityReference(TestEntities.Project, project), Relationship = new Relationship(TestEntities.ProjectTag),
                RelatedEntities = [new EntityReference(TestEntities.Tag, tag)],
            });
            await svc.ExecuteAsync(new GrantAccessRequest
            {
                Target = new EntityReference(TestEntities.Project, project),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", user), AccessMask = AccessRights.ReadAccess },
            });
            var role = await SecurityTestHelper.CreateRoleAsync(fx, "soap-audit-" + Guid.NewGuid().ToString("N")[..8]);
            await svc.ExecuteAsync(new AddPrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege(3, PrivilegeNames.IdOf("prvReadAccount"), root)],
            });

            var history = (RetrieveRecordChangeHistoryResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new RetrieveRecordChangeHistoryRequest
                {
                    Target = new EntityReference(TestEntities.Project, project),
                    PagingInfo = new PagingInfo { PageNumber = 1, Count = 10, ReturnTotalRecordCount = true },
                }))));
            var details = history.AuditDetailCollection.AuditDetails;
            history.AuditDetailCollection.TotalRecordCount.Should().Be(4);
            details[0].Should().BeOfType<ShareAuditDetail>().Which.Principal.Id.Should().Be(user);
            ((ShareAuditDetail)details[0]).NewPrivileges.Should().Be(AccessRights.ReadAccess);
            var relationship = details[1].Should().BeOfType<RelationshipAuditDetail>().Subject;
            relationship.RelationshipName.Should().Be(TestEntities.ProjectTag);
            relationship.TargetRecords.Should().ContainSingle().Which.Id.Should().Be(tag);
            var update = details[2].Should().BeOfType<AttributeAuditDetail>().Subject;
            (update.OldValue["pb_name"], update.NewValue["pb_name"]).Should().Be(("soap-audit", "soap-audit-2"));
            update.AuditRecord.FormattedValues["action"].Should().Be("Update");
            update.AuditRecord.GetAttributeValue<EntityReference>("userid").Name.Should().Be(XrmdFixture.AdminName);
            details[3].Should().BeOfType<AttributeAuditDetail>();

            var roleHistory = (RetrieveRecordChangeHistoryResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new RetrieveRecordChangeHistoryRequest { Target = new EntityReference("role", role) }))));
            var privileges = roleHistory.AuditDetailCollection.AuditDetails.Should().ContainSingle().Which
                .Should().BeOfType<RolePrivilegeAuditDetail>().Subject;
            privileges.OldRolePrivileges.Should().BeEmpty();
            privileges.NewRolePrivileges.Should().ContainSingle().Which.PrivilegeId.Should().Be(PrivilegeNames.IdOf("prvReadAccount"));

            var single = (RetrieveAuditDetailsResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new RetrieveAuditDetailsRequest { AuditId = update.AuditRecord.Id }))));
            single.AuditDetail.Should().BeOfType<AttributeAuditDetail>().Which.NewValue["pb_name"].Should().Be("soap-audit-2");

            var attribute = (RetrieveAttributeChangeHistoryResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new RetrieveAttributeChangeHistoryRequest { Target = new EntityReference(TestEntities.Project, project), AttributeLogicalName = "pb_name" }))));
            attribute.AuditDetailCollection.AuditDetails.Should().HaveCount(2);

            var deleted = (DeleteRecordChangeHistoryResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new DeleteRecordChangeHistoryRequest { Target = new EntityReference(TestEntities.Project, project) }))));
            deleted.DeletedEntriesCount.Should().Be(4);
            var partitions = (DeleteAuditDataResponse)await ExecuteResultAsync(await PostAsync(Envelope(Execute(
                new DeleteAuditDataRequest { EndDate = new DateTime(2000, 3, 31, 0, 0, 0, DateTimeKind.Utc) }))));
            partitions.PartitionsDeleted.Should().Be(0);
        }
        finally
        {
            await svc.UpdateAsync(new Entity("organization", organizationId) { ["isauditenabled"] = false });
            await svc.ExecuteAsync(new UpdateEntityRequest { Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
                { LogicalName = TestEntities.Project, IsAuditEnabled = new BooleanManagedProperty(false) } });
        }
    }

    [Fact]
    public async Task Soap_create_email_with_to_entitycollection()
    {
        // Plan 2.11, task 3: a party list travels as an EntityCollection of activityparty inside the Target's attributes.
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var contact = await svc.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Example", ["emailaddress1"] = "ada.soap@example.com" });
        var party = new Entity("activityparty") { ["partyid"] = new EntityReference("contact", contact) };

        var created = await ExecuteResultAsync(await PostAsync(Envelope(Execute(new CreateRequest
        {
            Target = new Entity("email") { ["subject"] = "Over SOAP", ["to"] = new EntityCollection([party]) },
        }))));

        var id = created.Should().BeOfType<CreateResponse>().Which.id;
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT partyid, addressused FROM data.activityparty WHERE activityid = @id AND participationtypemask = 2");
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        rd.GetGuid(0).Should().Be(contact);
        rd.GetString(1).Should().Be("ada.soap@example.com");
        (await rd.ReadAsync()).Should().BeFalse();
    }
}
