using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// The <c>customapis/</c> folder of a solution file (plan 15, task 2): one <c>customapi.xml</c> per API, with its request
/// parameters and response properties in folders of their own, read into <see cref="PackageCustomApi"/> and written back
/// with what xrmverse does not model (<c>iscustomizable</c>, the label sets, any unknown child) in place.
/// </summary>
public class CustomApiXmlTests
{
    private const string Folder = "customapis/";

    private static byte[] RealExport() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));

    private static SolutionPackage ReadRealExport()
    {
        var (solution, customizations, files) = SolutionZip.Read(RealExport());
        // Autonumber columns are refused (issue #38) before anything else is read: drop their formats.
        foreach (var format in customizations.Descendants("AutoNumberFormat").ToList()) format.Remove();
        return CustomizationsReader.Read(solution, customizations, files);
    }

    /// <summary>The fixture solution with <paramref name="extra"/> archive entries added (text, UTF-8).</summary>
    private static SolutionPackage ReadWith(params (string Path, string Content)[] extra)
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());
        // Ordinal: SolutionZip.Read already refuses two entries whose paths differ only in case; the reader checks again.
        var all = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        foreach (var (path, content) in extra) all[path] = Encoding.UTF8.GetBytes(content);
        return CustomizationsReader.Read(solution, customizations, all);
    }

    /// <summary>A global API in the shape of Learn's sample: the optional elements written empty.</summary>
    private const string SampleApi = """
        <customapi uniquename="xv_Sample">
          <allowedcustomprocessingsteptype>0</allowedcustomprocessingsteptype>
          <bindingtype>0</bindingtype>
          <boundentitylogicalname />
          <description default="Sample operation">
            <label description="Sample operation" languagecode="1033" />
          </description>
          <displayname default="Sample">
            <label description="Sample" languagecode="1033" />
          </displayname>
          <executeprivilegename />
          <iscustomizable>1</iscustomizable>
          <isfunction>0</isfunction>
          <isprivate>0</isprivate>
          <name>xv_Sample</name>
          <plugintypeid />
          <workflowsdkstepenabled>0</workflowsdkstepenabled>
        </customapi>
        """;

    private const string SampleParameter = """
        <customapirequestparameter uniquename="Subject">
          <description default="What to work on">
            <label description="What to work on" languagecode="1033" />
          </description>
          <displayname default="Subject">
            <label description="Subject" languagecode="1033" />
          </displayname>
          <iscustomizable>1</iscustomizable>
          <isoptional>0</isoptional>
          <logicalentityname />
          <name>xv_Sample.Subject</name>
          <type>10</type>
        </customapirequestparameter>
        """;

    private static string ApiPath(string api) => $"customapis/{api}/customapi.xml";

    private static string ParameterPath(string api, string parameter) =>
        $"customapis/{api}/customapirequestparameters/{parameter}/customapirequestparameter.xml";

    [OpenProject365Fact]
    public void Real_export_custom_apis_are_read()
    {
        var package = ReadRealExport();

        var apis = package.CustomApis!;
        apis.Should().HaveCount(27);
        apis.Sum(a => a.RequestParameters.Count).Should().Be(32);
        apis.Sum(a => a.ResponseProperties.Count).Should().Be(23);
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase));
        apis.Count(a => a.BindingType == 1).Should().Be(20);
        apis.SelectMany(a => a.ResponseProperties).Should().OnlyContain(p => p.IsOptional == null);

        var labour = apis.Single(a => a.UniqueName == "op365_GetLabourCost");
        labour.IsFunction.Should().BeTrue();
        labour.BindingType.Should().Be(0);
        labour.BoundEntity.Should().BeNull();
        labour.RequestParameters.Should().HaveCount(2);

        var receipt = apis.Single(a => a.UniqueName == "op365_RegisterGoodsReceipt");
        receipt.Name.Should().Be("Register Goods Receipt");
        receipt.BindingType.Should().Be(1);
        receipt.BoundEntity.Should().Be("op365_purchaseorder");
        receipt.PluginTypeKey.Should().Be(new Guid("1cc5c8e2-fdcf-f111-a05b-7ced8d2d53e4"));
        receipt.IsFunction.Should().BeFalse();
        receipt.ExecutePrivilegeName.Should().BeNull();
        var lines = receipt.RequestParameters.Single(p => p.UniqueName == "Lines");
        lines.Type.Should().Be(11);
        lines.IsOptional.Should().BeTrue();
        lines.Name.Should().Be("op365_RegisterGoodsReceipt.Lines");
        receipt.ResponseProperties.Single(p => p.UniqueName == "GoodsReceiptId").Type.Should().Be(12);
        receipt.DisplayName.LocalizedLabels.Single(l => l.LanguageCode == 1031).Label.Should().Be("Wareneingang erfassen");
        receipt.DisplayName.UserLocalizedLabel!.Label.Should().Be("Register Goods Receipt");
        receipt.Description.LocalizedLabels.Should().HaveCount(4);

        // Kept: the modelled children are markers, iscustomizable and the label sets stay as they are.
        receipt.Kept!.Element("iscustomizable")!.Value.Should().Be("1");
        receipt.Kept!.Element("displayname")!.Elements("label").Should().HaveCount(4);
        UnmodelledXml.IsMarker(receipt.Kept!.Element("bindingtype")!).Should().BeTrue();
        UnmodelledXml.IsMarker(lines.Kept!.Element("type")!).Should().BeTrue();
    }

    [Fact]
    public void Both_plugin_type_reference_forms_are_read()
    {
        var key = Guid.NewGuid();
        var id = Guid.NewGuid();
        string Api(string name, string reference) => SampleApi.Replace("xv_Sample", name).Replace("<plugintypeid />", reference);

        var package = ReadWith(
            (ApiPath("xv_ByKey"), Api("xv_ByKey", $"<plugintypeid><plugintypeexportkey>{key}</plugintypeexportkey></plugintypeid>")),
            (ApiPath("xv_ById"), Api("xv_ById", $"<plugintypeid><plugintypeid>{id}</plugintypeid></plugintypeid>")));

        package.CustomApis!.Single(a => a.UniqueName == "xv_ByKey").PluginTypeKey.Should().Be(key);
        package.CustomApis!.Single(a => a.UniqueName == "xv_ById").PluginTypeKey.Should().Be(id);
    }

    [Fact]
    public void Empty_optional_elements_are_read_as_null()
    {
        var package = ReadWith((ApiPath("xv_Sample"), SampleApi), (ParameterPath("xv_Sample", "Subject"), SampleParameter));

        var api = package.CustomApis.Should().ContainSingle().Subject;
        api.BoundEntity.Should().BeNull();
        api.ExecutePrivilegeName.Should().BeNull();
        api.PluginTypeKey.Should().BeNull();
        api.AllowedStepType.Should().Be(0);
        api.WorkflowEnabled.Should().BeFalse();
        api.IsPrivate.Should().BeFalse();
        var parameter = api.RequestParameters.Should().ContainSingle().Subject;
        parameter.LogicalEntityName.Should().BeNull();
        parameter.IsOptional.Should().BeFalse();
        parameter.Type.Should().Be(10);
        parameter.DisplayName.UserLocalizedLabel!.Label.Should().Be("Subject");
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_optional_elements_are_written_back_empty()
    {
        var package = ReadWith((ApiPath("xv_Sample"), SampleApi), (ParameterPath("xv_Sample", "Subject"), SampleParameter));

        var files = CustomizationsWriter.Files(package);

        Normalized(files[ApiPath("xv_Sample")]).Should().Be(Normalized(Encoding.UTF8.GetBytes(SampleApi)));
        Normalized(files[ParameterPath("xv_Sample", "Subject")]).Should().Be(Normalized(Encoding.UTF8.GetBytes(SampleParameter)));
    }

    [OpenProject365Fact]
    public void Custom_api_files_round_trip()
    {
        var (_, _, original) = SolutionZip.Read(RealExport());
        var package = ReadRealExport();

        var written = CustomizationsWriter.Files(package);

        var expected = original.Where(f => f.Key.StartsWith(Folder, StringComparison.Ordinal)).ToList();
        expected.Should().HaveCount(27 + 32 + 23);
        written.Keys.Where(k => k.StartsWith(Folder, StringComparison.Ordinal)).Should().BeEquivalentTo(expected.Select(f => f.Key));
        foreach (var (path, content) in expected)
            Normalized(written[path]).Should().Be(Normalized(content), "{0} is written back as it was read", path);
    }

    [Fact]
    public void Unmatched_file_under_customapis_stays_kept()
    {
        const string notes = "customapis/xv_Sample/notes.txt";
        const string stray = "customapis/xv_Sample/customapirequestparameters/Subject/other.xml";
        const string orphanRoot = "customapis/customapi.xml";

        var package = ReadWith((ApiPath("xv_Sample"), SampleApi), (notes, "kept"), (stray, "<x />"), (orphanRoot, "<customapi />"));

        package.CustomApis.Should().ContainSingle().Which.RequestParameters.Should().BeEmpty();
        package.KeptFiles!.Keys.Where(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(notes, stray, orphanRoot);
        CustomizationsWriter.Files(package).Should().ContainKeys(notes, stray, orphanRoot, ApiPath("xv_Sample"));
    }

    [Fact]
    public void A_file_without_uniquename_is_refused_naming_its_path()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi.Replace(" uniquename=\"xv_Sample\"", "")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Sample/customapi.xml*uniquename*");
    }

    [Fact]
    public void A_non_numeric_flag_is_refused_naming_its_path()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi),
            (ParameterPath("xv_Sample", "Subject"), SampleParameter.Replace("<isoptional>0</isoptional>", "<isoptional>maybe</isoptional>")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Sample/customapirequestparameters/Subject/customapirequestparameter.xml*isoptional*maybe*");
    }

    [Fact]
    public void A_non_numeric_choice_is_refused_naming_its_path()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi.Replace("<bindingtype>0</bindingtype>", "<bindingtype>Entity</bindingtype>")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Sample/customapi.xml*bindingtype*Entity*");
    }

    [Fact]
    public void A_file_that_is_not_xml_is_refused_naming_its_path()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), "<customapi uniquename=\"xv_Sample\">"));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).WithMessage("*customapis/xv_Sample/customapi.xml*");
    }

    [Fact]
    public void A_parameter_folder_without_its_api_file_is_refused()
    {
        var act = () => ReadWith((ParameterPath("xv_Missing", "Subject"), SampleParameter));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Missing/customapirequestparameters/Subject/customapirequestparameter.xml*customapis/xv_Missing/customapi.xml*");
    }

    [Fact]
    public void Power_fx_markers_stay_in_the_kept_xml()
    {
        var api = SampleApi.Replace("<workflowsdkstepenabled>", "<powerfxruleid>{0b8d4e8e-1111-4c3b-9d0e-2a1f3c4d5e6f}</powerfxruleid><workflowsdkstepenabled>");
        var parameter = SampleParameter.Replace("<type>", "<fxexpressionid>{4f1d2c3b-2222-4a5b-8c7d-6e5f4a3b2c1d}</fxexpressionid><type>");

        var read = ReadWith((ApiPath("xv_Sample"), api), (ParameterPath("xv_Sample", "Subject"), parameter)).CustomApis!.Single();

        read.Kept!.Element("powerfxruleid").Should().NotBeNull();
        UnmodelledXml.IsMarker(read.Kept!.Element("powerfxruleid")!).Should().BeFalse();
        read.RequestParameters.Single().Kept!.Element("fxexpressionid").Should().NotBeNull();
    }

    [Fact]
    public void An_api_without_kept_xml_gets_one_label_in_the_base_language()
    {
        var package = ReadWith() with
        {
            CustomApis =
            [
                new PackageCustomApi("xv_Fresh", "Fresh", 1, "xv_project", 0, false, false, false, null, new Guid("5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e"),
                    Label("Fresh one"), Label("Made in xrmverse"),
                    [new PackageCustomApiField("Count", "xv_Fresh.Count", 7, null, true, Label("Count"), Label("How many"), null)],
                    [new PackageCustomApiField("Result", "xv_Fresh.Result", 10, null, null, Label("Result"), new Microsoft.Xrm.Sdk.Label(), null)],
                    null),
            ],
        };

        var files = CustomizationsWriter.Files(package);

        var api = XElement.Parse(Encoding.UTF8.GetString(files[ApiPath("xv_Fresh")]));
        api.Elements().Select(e => e.Name.LocalName).Should().Equal("allowedcustomprocessingsteptype", "bindingtype", "boundentitylogicalname",
            "description", "displayname", "iscustomizable", "isfunction", "isprivate", "name", "plugintypeid", "workflowsdkstepenabled");
        var displayName = api.Element("displayname")!;
        ((string?)displayName.Attribute("default")).Should().Be("Fresh one");
        displayName.Elements("label").Should().ContainSingle().Which.Attribute("languagecode")!.Value.Should().Be("1033");
        api.Element("plugintypeid")!.Element("plugintypeexportkey")!.Value.Should().Be("5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e");
        api.Element("iscustomizable")!.Value.Should().Be("1");

        var count = XElement.Parse(Encoding.UTF8.GetString(files[ParameterPath("xv_Fresh", "Count")]));
        count.Elements().Select(e => e.Name.LocalName).Should().Equal("description", "displayname", "iscustomizable", "isoptional", "name", "type");
        var result = XElement.Parse(Encoding.UTF8.GetString(
            files["customapis/xv_Fresh/customapiresponseproperties/Result/customapiresponseproperty.xml"]));
        result.Elements().Select(e => e.Name.LocalName).Should().Equal("displayname", "iscustomizable", "name", "type");

        // What it writes reads back as it was.
        var (s, c, f) = SolutionZip.Read(SolutionZip.Write(CustomizationsWriter.Write(package).Solution,
            CustomizationsWriter.Write(package).Customizations, files));
        var again = CustomizationsReader.Read(s, c, f).CustomApis!.Single();
        again.Should().BeEquivalentTo(package.CustomApis![0], o => o.Excluding(a => a.Kept).Excluding(a => a.RequestParameters)
            .Excluding(a => a.ResponseProperties));
        again.RequestParameters.Single().Should().BeEquivalentTo(package.CustomApis[0].RequestParameters[0], o => o.Excluding(p => p.Kept));
    }

    [Theory]
    [InlineData("<plugintypeid><plugintypeexportKey>5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e</plugintypeexportKey></plugintypeid>", "plugintypeexportKey")]
    [InlineData("<plugintypeid><plugintypeexportkey>5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e</plugintypeexportkey><solution>x</solution></plugintypeid>", "solution")]
    public void A_plugin_type_reference_with_an_unknown_child_is_refused(string reference, string child)
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi.Replace("<plugintypeid />", reference)));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage($"*customapis/xv_Sample/customapi.xml*plugintypeid*'{child}'*");
    }

    [Fact]
    public void A_plugin_type_reference_naming_two_types_is_refused()
    {
        var reference = "<plugintypeid><plugintypeexportkey>5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e</plugintypeexportkey>"
                        + "<plugintypeid>6e3c8f1d-2b18-4a68-8b54-4d7a2e3b9c2f</plugintypeid></plugintypeid>";
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi.Replace("<plugintypeid />", reference)));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Sample/customapi.xml*two plug-in types*");
    }

    [Fact]
    public void The_same_plugin_type_in_both_children_is_read()
    {
        var reference = "<plugintypeid><plugintypeexportkey>5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e</plugintypeexportkey>"
                        + "<plugintypeid>5D2B7E0C-1A07-4F57-9A43-3C6F1D2A8B1E</plugintypeid></plugintypeid>";

        ReadWith((ApiPath("xv_Sample"), SampleApi.Replace("<plugintypeid />", reference))).CustomApis!.Single().PluginTypeKey
            .Should().Be(new Guid("5d2b7e0c-1a07-4f57-9a43-3c6f1d2a8b1e"));
    }

    [Fact]
    public void Two_apis_sharing_a_uniquename_are_refused_naming_both_paths()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi), (ApiPath("XV_SAMPLE"), SampleApi.Replace("xv_Sample", "XV_SAMPLE")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Sample/customapi.xml*customapis/XV_SAMPLE/customapi.xml*");
    }

    [Fact]
    public void Two_request_parameters_sharing_a_uniquename_are_refused_naming_both_paths()
    {
        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi), (ParameterPath("xv_Sample", "Subject"), SampleParameter),
            (ParameterPath("xv_Sample", "SUBJECT"), SampleParameter.Replace("uniquename=\"Subject\"", "uniquename=\"SUBJECT\"")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapirequestparameters/S*customapirequestparameter.xml*customapirequestparameters/S*request parameters*");
    }

    [Fact]
    public void Two_response_properties_sharing_a_uniquename_are_refused_naming_both_paths()
    {
        static string Property(string name) => $"<customapiresponseproperty uniquename=\"{name}\"><name>{name}</name><type>10</type></customapiresponseproperty>";
        static string Path(string name) => $"customapis/xv_Sample/customapiresponseproperties/{name}/customapiresponseproperty.xml";

        var act = () => ReadWith((ApiPath("xv_Sample"), SampleApi), (Path("Result"), Property("Result")), (Path("result"), Property("result")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapiresponseproperties/*customapiresponseproperties/*response properties*");
    }

    [Fact]
    public void A_folder_not_named_by_its_uniquename_is_refused()
    {
        var api = () => ReadWith((ApiPath("xv_Other"), SampleApi));
        var parameter = () => ReadWith((ApiPath("xv_Sample"), SampleApi), (ParameterPath("xv_Sample", "Topic"), SampleParameter));

        api.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapis/xv_Other/customapi.xml*folder 'xv_Other'*uniquename 'xv_Sample'*");
        parameter.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400)
            .WithMessage("*customapirequestparameters/Topic/*folder 'Topic'*uniquename 'Subject'*");
    }

    [Fact]
    public void A_folder_differing_only_in_case_is_read()
    {
        ReadWith((ApiPath("XV_sample"), SampleApi)).CustomApis!.Single().UniqueName.Should().Be("xv_Sample");
    }

    [Fact]
    public void Files_are_written_with_crlf_and_without_byte_order_mark()
    {
        var bytes = CustomizationsWriter.Files(ReadWith((ApiPath("xv_Sample"), SampleApi)))[ApiPath("xv_Sample")];

        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().StartWith("<customapi uniquename=\"xv_Sample\">\r\n  <allowedcustomprocessingsteptype>");
        text.Replace("\r\n", "").Should().NotContain("\n");
    }

    [Fact]
    public void Nil_elements_are_read_as_empty()
    {
        var api = SampleApi.Replace("<customapi uniquename=\"xv_Sample\">",
                "<customapi uniquename=\"xv_Sample\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">")
            .Replace("<executeprivilegename />", "<executeprivilegename xsi:nil=\"true\">ignored</executeprivilegename>")
            .Replace("<plugintypeid />", "<plugintypeid xsi:nil=\"true\" />");

        var read = ReadWith((ApiPath("xv_Sample"), api)).CustomApis!.Single();

        read.ExecutePrivilegeName.Should().BeNull();
        read.PluginTypeKey.Should().BeNull();
    }

    [Fact]
    public void Labels_without_the_base_language_have_no_user_localized_label()
    {
        var api = SampleApi.Replace("<label description=\"Sample\" languagecode=\"1033\" />", "<label description=\"Beispiel\" languagecode=\"1031\" />");

        var label = ReadWith((ApiPath("xv_Sample"), api)).CustomApis!.Single().DisplayName;

        label.UserLocalizedLabel.Should().BeNull();
        label.LocalizedLabels.Should().ContainSingle().Which.Label.Should().Be("Beispiel");
    }

    private static Microsoft.Xrm.Sdk.Label Label(string text)
    {
        var label = new Microsoft.Xrm.Sdk.LocalizedLabel(text, 1033);
        return new Microsoft.Xrm.Sdk.Label(label, new[] { label });
    }

    private static string Normalized(byte[] xml) =>
        XmlNormalizer.Normalize(XDocument.Parse(Encoding.UTF8.GetString(xml).TrimStart('﻿')));
}
