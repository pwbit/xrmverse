using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Validation;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The solution actions and functions of the Web API: the body translated into the SDK request with the SDK's parameter
/// names, unknown or mistyped parameters refused by name, and the operations declared in <c>$metadata</c>.
/// </summary>
public class ActionRoutingTests
{
    private static readonly Guid Id = Guid.Parse("0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70");

    private static OrganizationRequest Translate(string action, string json) =>
        ActionController.ToRequest(action, JsonNode.Parse(json)!.AsObject(), new FakeStore());

    private static XrmdFaultException Refused(string action, string json)
    {
        var act = () => Translate(action, json);
        return act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).Which;
    }

    [Fact]
    public void ImportSolution_body_becomes_the_sdk_request()
    {
        var request = Translate("ImportSolution", $$"""
            { "CustomizationFile": "AQID", "ImportJobId": "{{Id}}", "OverwriteUnmanagedCustomizations": true,
              "PublishWorkflows": false, "HoldingSolution": null }
            """);

        request.RequestName.Should().Be("ImportSolution");
        request["CustomizationFile"].Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        request["ImportJobId"].Should().Be(Id);
        request["OverwriteUnmanagedCustomizations"].Should().Be(true);
        request["PublishWorkflows"].Should().Be(false);
        request.Parameters.ContainsKey("HoldingSolution").Should().BeFalse("a null nullable parameter is not set");
    }

    [Fact]
    public void ImportSolutionAsync_and_ExportSolution_keep_the_sdk_names()
    {
        Translate("ImportSolutionAsync", """{ "CustomizationFile": "AQID" }""")["CustomizationFile"]
            .Should().BeEquivalentTo(new byte[] { 1, 2, 3 });

        var export = Translate("ExportSolution", """{ "SolutionName": "xrmverse", "Managed": false, "ExportCalendarSettings": true }""");
        export.RequestName.Should().Be("ExportSolution");
        export["SolutionName"].Should().Be("xrmverse");
        export["Managed"].Should().Be(false);
        export["ExportCalendarSettings"].Should().Be(true, "the SDK path decides whether an option can be honored");
    }

    [Fact]
    public void Unknown_parameters_are_400_naming_them()
    {
        Refused("ImportSolution", """{ "CustomizationFile": "AQID", "Bogus": 1 }""").Message.Should().Contain("'Bogus'").And.Contain("ImportSolution");
        Refused("PublishAllXml", """{ "ParameterXml": "<x/>" }""").Message.Should().Contain("'ParameterXml'");
        Refused("ExportSolution", """{ "solutionname": "x" }""").Message.Should().Contain("'solutionname'", "parameter names are case-sensitive");
    }

    [Theory]
    [InlineData("ImportSolution", """{ "CustomizationFile": "not base64!" }""", "CustomizationFile")]
    [InlineData("ImportSolution", """{ "CustomizationFile": "AQID", "OverwriteUnmanagedCustomizations": "yes" }""", "OverwriteUnmanagedCustomizations")]
    [InlineData("ImportSolution", """{ "CustomizationFile": "AQID", "ImportJobId": "x" }""", "ImportJobId")]
    [InlineData("ImportSolution", """{ "CustomizationFile": "AQID", "PublishWorkflows": null }""", "PublishWorkflows")]
    [InlineData("AddSolutionComponent", """{ "ComponentType": "1" }""", "ComponentType")]
    [InlineData("AddSolutionComponent", """{ "IncludedComponentSettingsValues": [1] }""", "IncludedComponentSettingsValues")]
    [InlineData("RemoveSolutionComponent", """{ "SolutionComponent": "x" }""", "SolutionComponent")]
    public void Mistyped_values_are_400_naming_the_parameter(string action, string json, string parameter) =>
        Refused(action, json).Message.Should().Contain($"'{parameter}'");

    [Fact]
    public void Solution_component_actions_map_to_the_sdk_parameters()
    {
        var add = Translate("AddSolutionComponent", $$"""
            { "ComponentId": "{{Id}}", "ComponentType": 1, "SolutionUniqueName": "x", "AddRequiredComponents": false,
              "DoNotIncludeSubcomponents": true, "IncludedComponentSettingsValues": [] }
            """);
        add.RequestName.Should().Be("AddSolutionComponent");
        add["ComponentId"].Should().Be(Id);
        add["ComponentType"].Should().Be(1);
        add["DoNotIncludeSubcomponents"].Should().Be(true);
        add["IncludedComponentSettingsValues"].Should().BeEquivalentTo(Array.Empty<string>());

        // The Web API names the component by a solutioncomponent entity; its key is the SDK's ComponentId.
        var remove = Translate("RemoveSolutionComponent", $$"""
            { "SolutionComponent": { "@odata.type": "Microsoft.Dynamics.CRM.solutioncomponent", "solutioncomponentid": "{{Id}}" },
              "ComponentType": 1, "SolutionUniqueName": "x" }
            """);
        remove.RequestName.Should().Be("RemoveSolutionComponent");
        remove["ComponentId"].Should().Be(Id);
        remove.Parameters.ContainsKey("SolutionComponent").Should().BeFalse();
    }

    [Fact]
    public void SolutionComponent_with_other_properties_is_400()
    {
        Refused("RemoveSolutionComponent", $$"""{ "SolutionComponent": { "solutioncomponentid": "{{Id}}", "objectid": "{{Id}}" } }""")
            .Message.Should().Contain("objectid");
    }

    [Fact]
    public void Complex_parameters_reach_the_sdk_path_as_set_values()
    {
        var import = Translate("ImportSolution", """
            { "CustomizationFile": "AQID", "ComponentParameters": [ { "@odata.type": "Microsoft.Dynamics.CRM.connectionreference" } ] }
            """);
        import["ComponentParameters"].Should().BeOfType<EntityCollection>().Which.Entities.Should().ContainSingle()
            .Which.LogicalName.Should().Be("connectionreference");

        var empty = Translate("ImportSolution", """{ "CustomizationFile": "AQID", "ComponentParameters": [], "SolutionParameters": {} }""");
        empty["ComponentParameters"].Should().BeOfType<EntityCollection>().Which.Entities.Should().BeEmpty();
        empty["SolutionParameters"].Should().BeOfType<SolutionParameters>();

        var staged = Translate("ImportSolution", $$"""{ "CustomizationFile": "AQID", "SolutionParameters": { "StageSolutionUploadId": "{{Id}}" } }""");
        staged["SolutionParameters"].Should().BeOfType<SolutionParameters>().Which.StageSolutionUploadId.Should().Be(Id);

        Translate("ExportSolution", """{ "SolutionName": "x", "ExportComponentsParams": {} }""")["ExportComponentsParams"]
            .Should().NotBeNull();
        Translate("ImportSolution", """{ "CustomizationFile": "AQID", "LayerDesiredOrder": {} }""")["LayerDesiredOrder"]
            .Should().NotBeNull();
    }

    [Fact]
    public void SolutionParameters_unknown_property_is_400()
    {
        Refused("ImportSolution", """{ "CustomizationFile": "AQID", "SolutionParameters": { "Bogus": true } }""")
            .Message.Should().Contain("'Bogus'");
    }

    [Fact]
    public void Web_api_only_import_options_are_refused_when_set()
    {
        Refused("ImportSolution", """{ "CustomizationFile": "AQID", "StageAndUpgrade": true }""").Message.Should().Contain("StageAndUpgrade");
        Refused("ImportSolutionAsync", """{ "CustomizationFile": "AQID", "SchemaUpdatesOnly": true }""").Message.Should().Contain("SchemaUpdatesOnly");
        Refused("ImportSolution", """{ "CustomizationFile": "AQID", "SolutionParameters": { "DeployMissingPackagesBeforeSolutionImport": true } }""")
            .Message.Should().Contain("SolutionParameters");
        var accepted = Translate("ImportSolution", """{ "CustomizationFile": "AQID", "StageAndUpgrade": false, "SchemaUpdatesOnly": null }""");
        accepted.Parameters.ContainsKey("StageAndUpgrade").Should().BeFalse("ImportSolutionRequest has no such parameter");
    }

    [Fact]
    public void Publish_actions_translate_with_their_parameters()
    {
        Translate("PublishXml", """{ "ParameterXml": "<importexportxml/>" }""")["ParameterXml"].Should().Be("<importexportxml/>");
        Translate("PublishAllXml", "{}").RequestName.Should().Be("PublishAllXml");
    }

    [Fact]
    public void Formatted_import_job_results_reads_the_parameter_inline_or_aliased()
    {
        ActionController.ParseImportJobId($"ImportJobId={Id}", _ => null).Should().Be(Id);
        ActionController.ParseImportJobId("ImportJobId=@p1", alias => alias == "@p1" ? Id.ToString() : null).Should().Be(Id);

        var missing = () => ActionController.ParseImportJobId("ImportJobId=@p1", _ => null);
        missing.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400);
        var other = () => ActionController.ParseImportJobId($"JobId={Id}", _ => null);
        other.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("JobId"));
    }

    [Fact]
    public void UpdateOptionValue_body_becomes_the_sdk_request_with_a_label()
    {
        var request = Translate("UpdateOptionValue", """
            { "OptionSetName": "x", "Value": 1, "Color": "#FFFFFF", "MergeLabels": true,
              "Label": { "LocalizedLabels": [{ "Label": "L", "LanguageCode": 1033 }] } }
            """);

        request.RequestName.Should().Be("UpdateOptionValue");
        (request["OptionSetName"], request["Value"], request["Color"], request["MergeLabels"]).Should().Be(("x", 1, "#FFFFFF", true));
        var label = request["Label"].Should().BeOfType<Label>().Which;
        label.LocalizedLabels.Should().ContainSingle().Which.Should().Match<LocalizedLabel>(l => l.Label == "L" && l.LanguageCode == 1033);
        request.Parameters.ContainsKey("Description").Should().BeFalse();
    }

    [Fact]
    public void UpdateOptionValue_takes_a_null_Color_as_a_clear()
    {
        Translate("UpdateOptionValue", """{ "OptionSetName": "x", "Value": 1, "MergeLabels": true, "Color": null }""")["Color"]
            .Should().Be("", "an explicit null clears the colour (ruling R1)");
        Translate("UpdateOptionValue", """{ "OptionSetName": "x", "Value": 1, "MergeLabels": true }""").Parameters
            .ContainsKey("Color").Should().BeFalse("an absent Color leaves the colour");
    }

    [Fact]
    public void InsertOptionValue_body_becomes_the_sdk_request()
    {
        var request = Translate("InsertOptionValue", """
            { "EntityLogicalName": "account", "AttributeLogicalName": "new_size", "Color": "#0078D4", "ParentValues": [],
              "Label": { "@odata.type": "Microsoft.Dynamics.CRM.Label",
                         "LocalizedLabels": [{ "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel", "Label": "M", "LanguageCode": 1033 }] },
              "Description": { "LocalizedLabels": [{ "Label": "Medium", "LanguageCode": 1033 }] } }
            """);

        request.RequestName.Should().Be("InsertOptionValue");
        request["Color"].Should().Be("#0078D4");
        ((Label)request["Label"]).LocalizedLabels.Single().Label.Should().Be("M");
        ((Label)request["Description"]).LocalizedLabels.Single().Label.Should().Be("Medium");
        request["ParentValues"].Should().BeEquivalentTo(Array.Empty<int>());
    }

    [Theory]
    [InlineData("""{ "OptionSetName": "x", "Value": 1, "MergeLabels": true, "Label": "L" }""", "Label")]
    [InlineData("""{ "OptionSetName": "x", "Value": 1, "MergeLabels": true, "Label": { "LocalizedLabels": 5 } }""", "Label")]
    [InlineData("""{ "OptionSetName": "x", "Value": 1, "MergeLabels": true, "Color": 5 }""", "Color")]
    [InlineData("""{ "OptionSetName": "x", "Value": 1, "MergeLabels": true, "ParentValues": ["a"] }""", "ParentValues")]
    [InlineData("""{ "OptionSetName": "x", "Value": "1", "MergeLabels": true }""", "Value")]
    public void UpdateOptionValue_mistyped_values_are_400_naming_the_parameter(string json, string parameter) =>
        Refused("UpdateOptionValue", json).Message.Should().Contain($"'{parameter}'");

    [Fact]
    public void Every_operation_name_is_a_reserved_entity_set_name()
    {
        ActionController.OperationNames.Should().BeEquivalentTo(
            ["ImportSolution", "ImportSolutionAsync", "ExportSolution", "AddSolutionComponent", "RemoveSolutionComponent",
             "UninstallSolutionAsync", "StageAndUpgrade", "StageAndUpgradeAsync", "DeleteAndPromote", "DeleteAndPromoteAsync", "PublishXml", "PublishAllXml", "RetrieveFormattedImportJobResults", "InitializeFileBlocksUpload", "UploadBlock",
             "CommitFileBlocksUpload", "InitializeFileBlocksDownload", "DownloadBlock", "DeleteFile",
             "SetAutoNumberSeed", "GetAutoNumberSeed", "GetNextAutoNumberValue", "RetrieveExchangeRate", "CloneAsPatch", "CloneAsSolution",
             "UnpublishDuplicateRule", "RetrieveDuplicates", "BulkDetectDuplicates",
             "RetrieveSharedPrincipalsAndAccess", "GrantAccess", "ModifyAccess", "RevokeAccess", "CalculateRollupField",
             "InsertOptionValue", "UpdateOptionValue"]);
        foreach (var name in ActionController.OperationNames)
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue($"'{name}' is reserved, ignoring case");
    }

    [Fact]
    public void Every_refused_activity_message_is_a_reserved_entity_set_name()
    {
        // Plan 2.11 (A18, review M1): the Web API answers these names as unbound operations, whatever their case.
        ActivitySchema.RefusedMessages.Should().Contain(["Book", "Rollup", "Restore", "AddRecurrence", "InitializeAnnotationBlocksUpload"]);
        // Plan 4.1a: the queue messages are implemented, none is refused any more.
        ActivitySchema.RefusedMessages.Should().NotContain(QueueSchema.Messages);
        foreach (var name in ActivitySchema.RefusedMessages)
        {
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue($"'{name}' is reserved, ignoring case");
            LogicalNameValidator.IsValidEntitySetName(name.ToLowerInvariant()).Should().BeFalse(name);
        }
    }

    [Fact]
    public void Metadata_declares_the_operations_and_the_metadata_entity_sets()
    {
        var solutionComponent = new EntityMetadata { LogicalName = "solutioncomponent", EntitySetName = "solutioncomponents" };
        solutionComponent.SetAttributes(SystemAttributes.Complete(solutionComponent, []));
        var model = new ODataEdmModelBuilder(new FakeStore(solutionComponent), TestCustomApis.None()).Build();

        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
        var import = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.ImportSolution").Should().ContainSingle().Which;
        import.IsAction().Should().BeTrue();
        import.GetReturn().Should().BeNull();
        import.Parameters.Select(p => p.Name).Should().Contain(["CustomizationFile", "ImportJobId", "ComponentParameters", "SolutionParameters"]);

        var async = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.ImportSolutionAsync").Single();
        async.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.ImportSolutionAsyncResponse");
        var response = (IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.ImportSolutionAsyncResponse");
        response.DeclaredProperties.Select(p => p.Name).Should().Equal("AsyncOperationId", "ImportJobKey");

        var remove = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RemoveSolutionComponent").Single();
        remove.FindParameter("SolutionComponent").Type.FullName().Should().Be("Microsoft.Dynamics.CRM.solutioncomponent");

        var function = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveFormattedImportJobResults").Single();
        function.IsFunction().Should().BeTrue();
        model.EntityContainer.FindOperationImports("RetrieveFormattedImportJobResults").Should().ContainSingle();
        model.EntityContainer.FindOperationImports("PublishAllXml").Should().ContainSingle();

        // The file messages (Learn Web API reference): parameters and response complex types.
        var upload = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.InitializeFileBlocksUpload").Single();
        upload.Parameters.Select(p => (p.Name, p.Type.FullName(), p.Type.IsNullable)).Should().Equal(
            ("Target", "Microsoft.Dynamics.CRM.crmbaseentity", false), ("FileName", "Edm.String", false), ("FileAttributeName", "Edm.String", false));
        upload.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.InitializeFileBlocksUploadResponse");
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.UploadBlock").Single().Parameters.Select(p => (p.Name, p.Type.FullName()))
            .Should().Equal(("BlockId", "Edm.String"), ("BlockData", "Edm.Binary"), ("FileContinuationToken", "Edm.String"));
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.CommitFileBlocksUpload").Single().Parameters.Select(p => (p.Name, p.Type.FullName()))
            .Should().Equal(("FileName", "Edm.String"), ("MimeType", "Edm.String"), ("BlockList", "Collection(Edm.String)"),
                ("FileContinuationToken", "Edm.String"));
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.DownloadBlock").Single().Parameters.Select(p => (p.Name, p.Type.FullName()))
            .Should().Equal(("Offset", "Edm.Int64"), ("BlockLength", "Edm.Int64"), ("FileContinuationToken", "Edm.String"));
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.DeleteFile").Single().Parameters.Select(p => (p.Name, p.Type.FullName()))
            .Should().Equal(("FileId", "Edm.Guid"));
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.CommitFileBlocksUploadResponse")).DeclaredProperties
            .Select(p => (p.Name, p.Type.FullName())).Should().Equal(("FileId", "Edm.Guid"), ("FileSizeInBytes", "Edm.Int64"));
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.InitializeFileBlocksDownloadResponse")).DeclaredProperties
            .Select(p => (p.Name, p.Type.FullName())).Should().Equal(("FileContinuationToken", "Edm.String"), ("FileName", "Edm.String"),
                ("FileSizeInBytes", "Edm.Int64"), ("IsChunkingSupported", "Edm.Boolean"));
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.DownloadBlockResponse")).DeclaredProperties
            .Select(p => (p.Name, p.Type.FullName())).Should().Equal(("Data", "Edm.Binary"));

        // The option value actions (Learn), with the Label complex type.
        var update = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.UpdateOptionValue").Should().ContainSingle().Which;
        update.IsAction().Should().BeTrue();
        update.GetReturn().Should().BeNull();
        update.FindParameter("Label").Type.FullName().Should().Be("Microsoft.Dynamics.CRM.Label");
        update.Parameters.Select(p => p.Name).Should().Contain(["Value", "MergeLabels", "Color", "OptionSetName", "ParentValues"]);
        var insert = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.InsertOptionValue").Should().ContainSingle().Which;
        insert.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.InsertOptionValueResponse");
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.InsertOptionValueResponse")).DeclaredProperties
            .Select(p => (p.Name, p.Type.FullName())).Should().Equal(("NewOptionValue", "Edm.Int32"));
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.Label")).DeclaredProperties.Select(p => p.Name)
            .Should().Contain(["LocalizedLabels", "UserLocalizedLabel"]);
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.LocalizedLabel")).DeclaredProperties.Select(p => p.Name)
            .Should().Contain(["Label", "LanguageCode"]);
        model.EntityContainer.FindOperationImports("UpdateOptionValue").Should().ContainSingle();
        model.EntityContainer.FindOperationImports("InsertOptionValue").Should().ContainSingle();

        model.EntityContainer.FindEntitySet("EntityDefinitions").EntityType.FullName().Should().Be("Microsoft.Dynamics.CRM.EntityMetadata");
        model.EntityContainer.FindEntitySet("GlobalOptionSetDefinitions").EntityType.FullName()
            .Should().Be("Microsoft.Dynamics.CRM.OptionSetMetadataBase");
        model.EntityContainer.FindEntitySet("RelationshipDefinitions").EntityType.FullName()
            .Should().Be("Microsoft.Dynamics.CRM.RelationshipMetadataBase");
    }
}
