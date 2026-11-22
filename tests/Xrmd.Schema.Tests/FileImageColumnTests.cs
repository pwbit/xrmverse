using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Schema.Tests;

/// <summary>
/// The column types <c>file</c> and <c>image</c> of the schema YAML (ruling F11), with the options <c>maxSizeInKB</c> (both),
/// <c>canStoreFullImage</c> and <c>primary</c> (image).
/// </summary>
public class FileImageColumnTests
{
    private const string Yaml = """
        publisher: xv
        tables:
          - name: xv_project
            label: Project
            primaryName: { name: xv_name, label: Name }
            columns:
              - { name: xv_contract, type: file, maxSizeInKB: 65536, label: Contract }
              - { name: xv_photo, type: image, label: Photo }
              - { name: xv_logo, type: image, maxSizeInKB: 2048, canStoreFullImage: true, primary: true, label: Logo }
        """;

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    private static InMemoryOrg Applied(string yaml)
    {
        var org = new InMemoryOrg();
        var plan = Diff(SchemaYaml.Read(yaml), org);
        plan.Rejections.Should().BeEmpty();
        org.Apply(plan.Changes);
        return org;
    }

    [Fact]
    public void Read_write_roundtrip_keeps_the_file_and_image_options()
    {
        var document = SchemaYaml.Read(Yaml);

        var columns = document.Tables.Single().Columns;
        (columns[0].Type, columns[0].MaxSizeInKB).Should().Be(("file", 65536));
        (columns[1].Type, columns[1].MaxSizeInKB, columns[1].CanStoreFullImage, columns[1].Primary).Should().Be(("image", null, null, null));
        (columns[2].MaxSizeInKB, columns[2].CanStoreFullImage, columns[2].Primary).Should().Be((2048, true, true));
        SchemaYaml.Write(SchemaYaml.Read(SchemaYaml.Write(document))).Should().Be(SchemaYaml.Write(document));
        SchemaYaml.Write(document).Should().Contain("{name: xv_logo, type: image, label: Logo, maxSizeInKB: 2048, canStoreFullImage: true, primary: true}");
    }

    [Fact]
    public void Yaml_apply_creates_file_and_image_columns()
    {
        var plan = Diff(SchemaYaml.Read(Yaml), new InMemoryOrg());

        plan.Rejections.Should().BeEmpty();
        var created = plan.Changes.OfType<CreateAttributeRequest>().Select(r => r.Attribute).ToList();
        var contract = created.OfType<FileAttributeMetadata>().Single();
        (contract.LogicalName, contract.MaxSizeInKB).Should().Be(("xv_contract", 65536));
        var photo = created.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "xv_photo");
        (photo.MaxSizeInKB, photo.CanStoreFullImage, photo.IsPrimaryImage).Should().Be((null, null, null), "the server's defaults apply");
        var logo = created.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "xv_logo");
        (logo.MaxSizeInKB, logo.CanStoreFullImage, logo.IsPrimaryImage).Should().Be((2048, true, null),
            "IsPrimaryImage true is ignored on create when another image column exists: an update follows");
        created.Select(a => a.LogicalName).Should().ContainInOrder(["xv_contract", "xv_photo", "xv_logo"], "the image columns are created in the file's order");
        var logoCreate = plan.Changes.ToList().FindIndex(r => r is CreateAttributeRequest c && c.Attribute.LogicalName == "xv_logo");
        var primary = plan.Changes[logoCreate + 1].Should().BeOfType<UpdateAttributeRequest>().Subject;
        (primary.EntityName, primary.Attribute.LogicalName, ((ImageAttributeMetadata)primary.Attribute).IsPrimaryImage).Should().Be(("xv_project", "xv_logo", true));

        var org = new InMemoryOrg();
        org.Apply(plan.Changes);
        ((ImageAttributeMetadata)org.Attribute("xv_project", "xv_logo")).IsPrimaryImage.Should().BeTrue();
        ((ImageAttributeMetadata)org.Attribute("xv_project", "xv_photo")).IsPrimaryImage.Should().BeFalse();
        Diff(SchemaYaml.Read(Yaml), org).Changes.Should().BeEmpty("a second apply changes nothing");
    }

    [Fact]
    public void Yaml_export_emits_file_and_image()
    {
        var org = Applied(Yaml);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");

        var columns = exported.Tables.Single().Columns;
        columns.Select(c => c.Name).Should().Equal(["xv_contract", "xv_logo", "xv_photo"], "the companions (xv_contract_name, xv_photoid, …) are the server's");
        var contract = columns.Single(c => c.Name == "xv_contract");
        (contract.Type, contract.MaxSizeInKB, contract.CanStoreFullImage, contract.Primary).Should().Be(("file", 65536, null, null));
        var logo = columns.Single(c => c.Name == "xv_logo");
        (logo.Type, logo.MaxSizeInKB, logo.CanStoreFullImage, logo.Primary).Should().Be(("image", 2048, true, true));
        var photo = columns.Single(c => c.Name == "xv_photo");
        (photo.Type, photo.MaxSizeInKB, photo.CanStoreFullImage, photo.Primary).Should().Be(("image", 10240, null, null),
            "only the primary image says so, and canStoreFullImage is written when true");
        var plan = Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org);
        plan.Changes.Should().BeEmpty();
        plan.Rejections.Should().BeEmpty();
    }

    [Fact]
    public void Changed_options_become_UpdateAttribute()
    {
        var org = Applied(Yaml);
        var edited = Yaml
            .Replace("maxSizeInKB: 65536", "maxSizeInKB: 1024", StringComparison.Ordinal)
            .Replace("{ name: xv_photo, type: image, label: Photo }", "{ name: xv_photo, type: image, canStoreFullImage: true, primary: true, label: Photo }", StringComparison.Ordinal)
            .Replace(", primary: true, label: Logo", ", label: Logo", StringComparison.Ordinal);

        var plan = Diff(SchemaYaml.Read(edited), org);

        plan.Rejections.Should().BeEmpty();
        var updates = plan.Changes.OfType<UpdateAttributeRequest>().ToDictionary(u => u.Attribute.LogicalName, u => u.Attribute);
        updates.Keys.Should().BeEquivalentTo(["xv_contract", "xv_photo"], "an image without primary keeps its flag until another one takes it");
        ((FileAttributeMetadata)updates["xv_contract"]).MaxSizeInKB.Should().Be(1024);
        var photo = (ImageAttributeMetadata)updates["xv_photo"];
        (photo.CanStoreFullImage, photo.IsPrimaryImage, photo.MaxSizeInKB).Should().Be((true, true, null));
        org.Apply(plan.Changes);
        Diff(SchemaYaml.Read(edited), org).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{ name: xv_x, type: string, maxSizeInKB: 10 }", "maxSizeInKB")]
    [InlineData("{ name: xv_x, type: file, canStoreFullImage: true }", "canStoreFullImage")]
    [InlineData("{ name: xv_x, type: file, primary: true }", "primary")]
    [InlineData("{ name: xv_x, type: image, primary: false }", "primary")]
    [InlineData("{ name: xv_x, type: file, maxSizeInKB: 0 }", "10485760")]
    [InlineData("{ name: xv_x, type: file, maxSizeInKB: 10485761 }", "10485760")]
    [InlineData("{ name: xv_x, type: image, maxSizeInKB: 30721 }", "30720")]
    [InlineData("{ name: xv_x, type: image, canStoreFullImage: maybe }", "maybe")]
    public void Invalid_file_or_image_column_is_an_error_naming_it(string column, string named)
    {
        var yaml = $"""
            publisher: xv
            tables:
              - name: xv_project
                label: Project
                primaryName: {"{"} name: xv_name {"}"}
                columns:
                  - {column}
            """;

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain(named);
        error.Line.Should().Be(7);
    }

    [Fact]
    public void Two_primary_images_in_one_table_are_an_error_with_the_line()
    {
        var yaml = Yaml.Replace("{ name: xv_photo, type: image, label: Photo }", "{ name: xv_photo, type: image, primary: true, label: Photo }", StringComparison.Ordinal);

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain("xv_project").And.Contain("xv_photo").And.Contain("xv_logo").And.Contain("one primary image");
        error.Line.Should().Be(9);
    }

    [Fact]
    public void A_type_change_to_or_from_file_is_a_rejection()
    {
        var org = Applied(Yaml);

        var plan = Diff(SchemaYaml.Read(Yaml.Replace("{ name: xv_contract, type: file, maxSizeInKB: 65536", "{ name: xv_contract, type: image", StringComparison.Ordinal)), org);

        plan.Rejections.Should().ContainSingle().Which.Should().Contain("xv_project.xv_contract").And.Contain("file");
    }

    [Fact]
    public void Web_api_bodies_carry_the_options()
    {
        var file = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_contract", Type = "file", MaxSizeInKB = 65536 });
        file["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.FileAttributeMetadata");
        file["MaxSizeInKB"]!.GetValue<int>().Should().Be(65536);

        var image = SchemaWebApiBodies.Attribute(new SchemaColumn { Name = "xv_logo", Type = "image", CanStoreFullImage = true, Primary = true });
        image["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.ImageAttributeMetadata");
        (image["CanStoreFullImage"]!.GetValue<bool>(), image["IsPrimaryImage"]!.GetValue<bool>()).Should().Be((true, true));
        image.ContainsKey("MaxSizeInKB").Should().BeFalse();
    }
}
