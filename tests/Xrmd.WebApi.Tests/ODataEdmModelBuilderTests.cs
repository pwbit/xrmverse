using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm.Csdl;
using System.Xml;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Validation;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Workflows;
using Xrmd.WebApi;

namespace Xrmd.WebApi.Tests;

public class ODataEdmModelBuilderTests
{
    private static EntityMetadata Entity(string logicalName, string setName, OwnershipTypes ownership, params AttributeMetadata[] attributes)
    {
        var e = new EntityMetadata { LogicalName = logicalName, EntitySetName = setName, OwnershipType = ownership };
        e.SetAttributes(SystemAttributes.Complete(e, attributes));
        return e;
    }

    private static EntityMetadata Project(string logicalName = "pb_project", string setName = "pb_projects") =>
        Entity(logicalName, setName, OwnershipTypes.UserOwned,
            new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 },
            new LookupAttributeMetadata { LogicalName = "pb_customerid", Targets = ["systemuser"] },
            new MoneyAttributeMetadata { LogicalName = "pb_budget" },
            new IntegerAttributeMetadata { LogicalName = "pb_count" },
            new DateTimeAttributeMetadata { LogicalName = "pb_startdate" },
            new BooleanAttributeMetadata { LogicalName = "pb_active" },
            new PicklistAttributeMetadata { LogicalName = "pb_priority" });

    private static EntityMetadata SystemUser() =>
        Entity("systemuser", "systemusers", OwnershipTypes.BusinessOwned,
            new StringAttributeMetadata { LogicalName = "fullname", MaxLength = 200 });

    private static IEdmEntityType Type(IEdmModel model, string name) =>
        (IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM." + name);


    private static CustomApiRegistry NoCustomApis() => Registry([]);

    private static CustomApiRegistry Registry(IReadOnlyList<CustomApiDefinition> apis) => new(_ => Task.FromResult(apis));

    private static CustomApiDefinition Api(string name, int binding = 0, string? bound = null, bool isFunction = false, bool isPrivate = false,
        CustomApiField[]? requests = null, CustomApiField[]? responses = null) =>
        new(Guid.NewGuid(), name, binding, bound, isFunction, isPrivate, 0, null, null, requests ?? [], responses ?? []);

    private sealed class Warnings : ILogger<ODataEdmModelBuilder>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }

    private static IEdmModel BuildWith(params CustomApiDefinition[] apis) => BuildWith(new Warnings(), apis);

    private static IEdmModel BuildWith(Warnings warnings, params CustomApiDefinition[] apis)
    {
        var registry = Registry(apis);
        registry.AllAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), registry, warnings).Build();
    }

    private static void ShouldValidate(IEdmModel model) =>
        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));

    private static IEdmOperation Operation(IEdmModel model, string name) =>
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM." + name).Single();

    [Fact]
    public void Custom_api_operations_are_declared()
    {
        var model = BuildWith(
            Api("pb_Ping", requests:
            [
                new("Text", CustomApiType.String, null, false), new("Count", CustomApiType.Integer, null, true),
                new("When", CustomApiType.DateTime, null, true), new("Amount", CustomApiType.Money, null, false),
                new("Tags", CustomApiType.StringArray, null, false), new("Ref", CustomApiType.EntityReference, "pb_project", true),
            ], responses: [new("Ok", CustomApiType.Boolean, null, false), new("Id", CustomApiType.Guid, null, false)]),
            Api("pb_Describe", binding: 1, bound: "pb_project", isFunction: true, responses: [new("Label", CustomApiType.String, null, false)]),
            Api("pb_Sweep", binding: 2, bound: "pb_project"));

        var ping = (IEdmAction)Operation(model, "pb_Ping");
        ping.IsBound.Should().BeFalse();
        model.EntityContainer.OperationImports().OfType<IEdmActionImport>().Should().Contain(i => i.Name == "pb_Ping");
        IEdmTypeReference Param(string name) => ping.FindParameter(name).Type;
        Param("Text").Definition.Should().Be(EdmCoreModel.Instance.GetString(false).Definition);
        Param("Text").IsNullable.Should().BeFalse();
        Param("Count").Definition.Should().Be(EdmCoreModel.Instance.GetInt32(true).Definition);
        Param("Count").IsNullable.Should().BeTrue();
        Param("When").Definition.Should().Be(EdmCoreModel.Instance.GetDateTimeOffset(true).Definition);
        Param("Amount").Definition.Should().Be(EdmCoreModel.Instance.GetDecimal(false).Definition);
        Param("Tags").IsCollection().Should().BeTrue();
        Param("Tags").AsCollection().ElementType().Definition.Should().Be(EdmCoreModel.Instance.GetString(false).Definition);
        Param("Ref").Definition.Should().BeSameAs(Type(model, "pb_project"));
        Param("Ref").IsNullable.Should().BeTrue();
        ping.GetReturn().Type.Definition.Should().BeSameAs(model.FindDeclaredType("Microsoft.Dynamics.CRM.pb_PingResponse"));
        ((IEdmComplexType)ping.GetReturn().Type.Definition).StructuralProperties().Select(p => p.Name).Should().BeEquivalentTo("Ok", "Id");

        var describe = (IEdmFunction)Operation(model, "pb_Describe");
        describe.IsBound.Should().BeTrue();
        describe.Parameters.First().Name.Should().Be("entity");
        describe.Parameters.First().Type.Definition.Should().BeSameAs(Type(model, "pb_project"));
        model.EntityContainer.OperationImports().Should().NotContain(i => i.Name == "pb_Describe");

        var sweep = (IEdmAction)Operation(model, "pb_Sweep");
        sweep.IsBound.Should().BeTrue();
        sweep.Parameters.First().Name.Should().Be("entityset");
        sweep.Parameters.First().Type.IsCollection().Should().BeTrue();
        sweep.Parameters.First().Type.AsCollection().ElementType().Definition.Should().BeSameAs(Type(model, "pb_project"));
        sweep.GetReturn().Should().BeNull();
        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Custom_api_entity_responses_are_the_entity_or_a_collection_of_crmbaseentity()
    {
        var model = BuildWith(
            Api("pb_One", isFunction: true, responses: [new("Row", CustomApiType.Entity, "pb_project", false)]),
            Api("pb_Many", isFunction: true, responses: [new("Rows", CustomApiType.EntityCollection, "pb_project", false)]));

        Operation(model, "pb_One").GetReturn().Type.Definition.Should().BeSameAs(Type(model, "pb_project"));
        var many = Operation(model, "pb_Many").GetReturn().Type;
        many.IsCollection().Should().BeTrue();
        many.AsCollection().ElementType().Definition.Should().BeSameAs(Type(model, "crmbaseentity"));
        model.FindDeclaredType("Microsoft.Dynamics.CRM.pb_OneResponse").Should().BeNull();
        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Custom_api_bound_to_a_missing_table_is_left_out_with_a_warning()
    {
        var warnings = new Warnings();
        var model = BuildWith(warnings, Api("pb_Gone", binding: 1, bound: "pb_deleted"), Api("pb_Here"));

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Gone").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Here").Should().ContainSingle();
        warnings.Messages.Should().ContainSingle().Which.Should().Contain("pb_Gone").And.Contain("pb_deleted").And.Contain("not in $metadata");
        ShouldValidate(model);
    }

    [Fact]
    public void Custom_api_with_an_unknown_table_in_a_parameter_falls_back_to_crmbaseentity_with_a_warning()
    {
        var warnings = new Warnings();
        var model = BuildWith(warnings, Api("pb_Ref", requests: [new("Ref", CustomApiType.EntityReference, "pb_deleted", true)]));

        Operation(model, "pb_Ref").FindParameter("Ref").Type.Definition.Should().BeSameAs(Type(model, "crmbaseentity"));
        warnings.Messages.Should().ContainSingle().Which.Should().Contain("pb_Ref").And.Contain("pb_deleted").And.Contain("crmbaseentity");
    }

    [Fact]
    public void Custom_api_named_like_an_entity_type_or_another_response_is_left_out_with_a_warning_and_the_model_stays_valid()
    {
        var warnings = new Warnings();
        var model = BuildWith(warnings,
            Api("pb_project"),
            Api("pb_A", responses: [new("X", CustomApiType.String, null, false), new("Y", CustomApiType.String, null, false)]),
            Api("pb_AResponse"),
            Api("systemuser", isFunction: true, responses: [new("X", CustomApiType.String, null, false)]));

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_project").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_AResponse").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.systemuser").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_A").Should().ContainSingle();
        warnings.Messages.Should().HaveCount(3);
        warnings.Messages.Should().Contain(m => m.Contains("pb_project")).And.Contain(m => m.Contains("pb_AResponse")).And.Contain(m => m.Contains("systemuser"));
        ShouldValidate(model);
    }

    [Fact]
    public async Task Custom_api_whose_response_type_name_is_taken_by_an_entity_type_is_left_out()
    {
        var warnings = new Warnings();
        var registry = Registry([Api("pb_Task", responses: [new("X", CustomApiType.String, null, false), new("Y", CustomApiType.String, null, false)])]);
        await registry.AllAsync(CancellationToken.None);
        var model = new ODataEdmModelBuilder(new FakeStore(Project("pb_TaskResponse", "pb_taskresponses"), SystemUser()), registry, warnings).Build();

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Task").Should().BeEmpty();
        warnings.Messages.Should().ContainSingle().Which.Should().Contain("pb_Task").And.Contain("pb_TaskResponse");
        ShouldValidate(model);
    }

    [Fact]
    public void Global_custom_api_named_like_an_entity_set_is_left_out_with_a_warning_and_the_model_stays_valid()
    {
        var warnings = new Warnings();
        var model = BuildWith(warnings,
            Api("pb_projects"),
            Api("systemusers", isFunction: true, responses: [new("X", CustomApiType.String, null, false)]),
            Api("pb_Projects"),
            Api("pb_bound", binding: 2, bound: "pb_project"));

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_projects").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.systemusers").Should().BeEmpty();
        model.EntityContainer.FindEntitySet("pb_projects").Should().NotBeNull();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Projects").Should().ContainSingle("container names are case-sensitive");
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_bound").Should().ContainSingle();
        warnings.Messages.Should().HaveCount(2);
        warnings.Messages.Should().Contain(m => m.Contains("'pb_projects'") && m.Contains("entity set"))
            .And.Contain(m => m.Contains("'systemusers'") && m.Contains("entity set"));
        ShouldValidate(model);
    }

    [Theory]
    [InlineData(1, "entity")]
    [InlineData(1, "Entity")]
    [InlineData(2, "entityset")]
    public void Custom_api_with_a_parameter_named_like_its_binding_parameter_is_left_out_with_a_warning(int binding, string parameter)
    {
        var warnings = new Warnings();
        var model = BuildWith(warnings, Api("pb_Clash", binding: binding, bound: "pb_project", requests: [new(parameter, CustomApiType.String, null, true)]));

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Clash").Should().BeEmpty();
        warnings.Messages.Should().ContainSingle().Which.Should().Contain("pb_Clash").And.Contain(parameter);
        ShouldValidate(model);
    }

    [Fact]
    public void Custom_api_single_entity_return_is_nullable_because_an_unset_property_answers_204()
    {
        var model = BuildWith(Api("pb_One", isFunction: true, responses: [new("Row", CustomApiType.Entity, "pb_project", false)]));

        Operation(model, "pb_One").GetReturn().Type.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Custom_api_edm_types_are_the_names_of_the_type_table()
    {
        var model = BuildWith(
            Api("pb_Types", requests: Enum.GetValues<CustomApiType>()
                .SelectMany(t => new[] { new CustomApiField(t + "Plain", t, null, false), new CustomApiField(t + "Table", t, "pb_project", false) }).ToArray()));

        var operation = Operation(model, "pb_Types");
        foreach (var type in Enum.GetValues<CustomApiType>())
            foreach (var (suffix, table) in new[] { ("Plain", (string?)null), ("Table", "pb_project") })
            {
                var declared = operation.FindParameter(type + suffix).Type.FullName().Replace("Microsoft.Dynamics.CRM.", "mscrm.");
                declared.Should().Be(CustomApiTypes.EdmTypeName(type, table), $"{type} with table {table ?? "none"}");
            }
    }

    [Fact]
    public void Custom_api_mixed_response_and_unbound_function_are_declared_and_round_trip_through_csdl()
    {
        var model = BuildWith(
            Api("pb_Mixed", isFunction: true, responses:
            [
                new("Count", CustomApiType.Integer, null, false), new("Row", CustomApiType.Entity, "pb_project", false),
                new("Rows", CustomApiType.EntityCollection, null, false), new("Owner", CustomApiType.EntityReference, null, false),
            ]));

        var function = (IEdmFunction)Operation(model, "pb_Mixed");
        function.IsComposable.Should().BeFalse();
        model.EntityContainer.OperationImports().OfType<IEdmFunctionImport>().Should().ContainSingle(i => i.Name == "pb_Mixed");
        var response = (IEdmComplexType)function.GetReturn().Type.Definition;
        response.StructuralProperties().Select(p => p.Name).Should().Equal("Count");
        var navigations = response.NavigationProperties().ToDictionary(n => n.Name);
        navigations["Row"].ToEntityType().Should().BeSameAs(Type(model, "pb_project"));
        navigations["Rows"].Type.IsCollection().Should().BeTrue();
        navigations["Rows"].ToEntityType().Should().BeSameAs(Type(model, "crmbaseentity"));
        navigations["Owner"].ToEntityType().Should().BeSameAs(Type(model, "crmbaseentity"));
        ShouldValidate(model);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream)) CsdlWriter.TryWriteCsdl(model, writer, CsdlTarget.OData, out _).Should().BeTrue();
        stream.Position = 0;
        using var reader = XmlReader.Create(stream);
        CsdlReader.TryParse(reader, out var parsed, out var errors).Should().BeTrue(string.Join("; ", errors?.Select(e => e.ErrorMessage) ?? []));
        parsed.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Mixed").Should().ContainSingle();
    }

    [Fact]
    public void Private_apis_and_empty_functions_are_not_declared()
    {
        var model = BuildWith(
            Api("pb_Secret", isPrivate: true, responses: [new("A", CustomApiType.String, null, false), new("B", CustomApiType.String, null, false)]),
            Api("pb_Nothing", isFunction: true),
            Api("pb_Shown"));

        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Secret").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Nothing").Should().BeEmpty();
        model.FindDeclaredType("Microsoft.Dynamics.CRM.pb_SecretResponse").Should().BeNull();
        model.EntityContainer.OperationImports().Select(i => i.Name).Should().NotContain(["pb_Secret", "pb_Nothing"]);
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Shown").Should().ContainSingle();
    }

    [Fact]
    public async Task Registry_current_is_empty_before_the_first_load_and_never_loads()
    {
        var loads = 0;
        var registry = new CustomApiRegistry(_ => { loads++; return Task.FromResult<IReadOnlyList<CustomApiDefinition>>([Api("pb_A")]); });

        registry.Current.Apis.Should().BeEmpty();
        registry.Current.Generation.Should().Be(CustomApiRegistry.NotLoaded);
        loads.Should().Be(0);

        await registry.AllAsync(CancellationToken.None);
        registry.Current.Apis.Select(a => a.UniqueName).Should().Equal("pb_A");
        var loaded = registry.Current.Generation;

        registry.Invalidate();
        registry.Current.Generation.Should().Be(loaded, "Current is the last loaded snapshot, one invalidation behind");
        await registry.AllAsync(CancellationToken.None);
        registry.Current.Generation.Should().Be(loaded + 1);
    }

    [Fact]
    public async Task Model_is_rebuilt_when_apis_change()
    {
        IReadOnlyList<CustomApiDefinition> apis = [];
        var registry = new CustomApiRegistry(_ => Task.FromResult(apis));
        var builder = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), registry);

        await registry.AllAsync(CancellationToken.None);
        var first = builder.Build();
        builder.Build().Should().BeSameAs(first);
        first.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Late").Should().BeEmpty();

        apis = [Api("pb_Late")];
        registry.Invalidate();
        builder.Build().Should().BeSameAs(first, "the new generation is not loaded yet; $metadata loads it before building");
        await registry.AllAsync(CancellationToken.None);
        var second = builder.Build();
        second.Should().NotBeSameAs(first);
        second.FindDeclaredOperations("Microsoft.Dynamics.CRM.pb_Late").Should().ContainSingle();
    }

    private sealed class Actions(params ActionDefinition[] actions) : IActivatedActions
    {
        public IReadOnlyList<ActionDefinition> Current { get; set; } = actions;
        public Task<IReadOnlyList<ActionDefinition>> GetAsync(CancellationToken ct) => Task.FromResult(Current);
    }

    private static ActionDefinition XamlAction(string message, string? bound = null, CustomApiField[]? inputs = null, CustomApiField[]? outputs = null) =>
        new(Guid.NewGuid(), message, message, bound, inputs ?? [], outputs ?? []);

    /// <summary>
    /// Issue #343: the activated XAML actions are declared as custom APIs are (bound to one record of their table, or global with an
    /// import), after the custom APIs, which keep a name both would have; one that cannot be declared is left out with a warning
    /// naming it as an action. The model is rebuilt when the list of activated actions changes.
    /// </summary>
    [Fact]
    public async Task Activated_xaml_actions_are_declared_after_the_custom_apis()
    {
        var registry = Registry([Api("pb_Both", responses: [new("FromApi", CustomApiType.String, null, false)])]);
        await registry.AllAsync(CancellationToken.None);
        var actions = new Actions(
            XamlAction("new_Recalc", "pb_project", [new("Increment", CustomApiType.Decimal, null, false)], [new("Total", CustomApiType.Decimal, null, false)]),
            XamlAction("new_Echo", inputs: [new("InText", CustomApiType.String, null, false), new("Customer", CustomApiType.EntityReference, "systemuser", true)]),
            XamlAction("pb_Both"),
            XamlAction("new_Lost", "pb_gone"));
        var warnings = new Warnings();
        var builder = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), registry, warnings, actions);

        var model = builder.Build();
        ShouldValidate(model);
        var recalc = (IEdmAction)Operation(model, "new_Recalc");
        recalc.IsBound.Should().BeTrue();
        recalc.Parameters.Select(p => p.Name).Should().Equal("entity", "Increment");
        recalc.Parameters.First().Type.Definition.Should().BeSameAs(Type(model, "pb_project"));
        ((IEdmComplexType)recalc.GetReturn().Type.Definition).StructuralProperties().Select(p => p.Name).Should().Equal("Total");
        var echo = (IEdmAction)Operation(model, "new_Echo");
        echo.IsBound.Should().BeFalse();
        echo.FindParameter("Customer").Type.Definition.Should().BeSameAs(Type(model, "systemuser"));
        model.EntityContainer.OperationImports().Select(i => i.Name).Should().Contain("new_Echo").And.NotContain("new_Recalc");
        ((IEdmComplexType)Operation(model, "pb_Both").GetReturn().Type.Definition).StructuralProperties().Select(p => p.Name)
            .Should().Equal("FromApi");
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.new_Lost").Should().BeEmpty();
        warnings.Messages.Should().Contain(m => m.StartsWith("Action 'pb_Both'")).And.Contain(m => m.StartsWith("Action 'new_Lost'"));

        builder.Build().Should().BeSameAs(model);
        actions.Current = [XamlAction("new_Echo")];
        var rebuilt = builder.Build();
        rebuilt.Should().NotBeSameAs(model);
        rebuilt.FindDeclaredOperations("Microsoft.Dynamics.CRM.new_Recalc").Should().BeEmpty();
    }

    /// <summary>#131: LocalTimeFromUtcTime is an unbound function with Learn's parameters and response.</summary>
    [Fact]
    public void Build_declares_LocalTimeFromUtcTime()
    {
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), NoCustomApis()).Build();
        var function = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.LocalTimeFromUtcTime").Should().ContainSingle().Subject;
        function.IsFunction().Should().BeTrue();
        function.IsBound.Should().BeFalse();
        function.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(("TimeZoneCode", "Edm.Int32"), ("UtcTime", "Edm.DateTimeOffset"));
        function.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.LocalTimeFromUtcTimeResponse");
        model.EntityContainer.FindOperationImports("LocalTimeFromUtcTime").Should().ContainSingle();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.UtcTimeFromLocalTime").Should().BeEmpty();
    }

    [Fact]
    public void Build_maps_pb_project_to_entity_type_with_lookup_value_property()
    {
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), NoCustomApis()).Build();

        var project = Type(model, "pb_project");
        project.Should().NotBeNull();
        project.Key().Select(k => k.Name).Should().Equal("pb_projectid");

        var value = project.FindProperty("_pb_customerid_value");
        value.Should().NotBeNull();
        value.Type.Definition.Should().Be(EdmCoreModel.Instance.GetGuid(true).Definition);

        var nav = project.FindProperty("pb_customerid").Should().BeAssignableTo<IEdmNavigationProperty>().Subject;
        nav.ToEntityType().FullName().Should().Be("Microsoft.Dynamics.CRM.systemuser");
        nav.Type.IsCollection().Should().BeFalse();

        model.EntityContainer.Name.Should().Be("System");
        model.EntityContainer.FindEntitySet("pb_projects").Should().NotBeNull();
    }

    [Theory]
    [InlineData("pb_name", EdmPrimitiveTypeKind.String)]
    [InlineData("pb_budget", EdmPrimitiveTypeKind.Decimal)]
    [InlineData("pb_budget_base", EdmPrimitiveTypeKind.Decimal)]
    [InlineData("pb_count", EdmPrimitiveTypeKind.Int32)]
    [InlineData("pb_startdate", EdmPrimitiveTypeKind.DateTimeOffset)]
    [InlineData("pb_active", EdmPrimitiveTypeKind.Boolean)]
    [InlineData("pb_priority", EdmPrimitiveTypeKind.Int32)]
    [InlineData("statecode", EdmPrimitiveTypeKind.Int32)]
    [InlineData("statuscode", EdmPrimitiveTypeKind.Int32)]
    [InlineData("versionnumber", EdmPrimitiveTypeKind.Int64)]
    [InlineData("pb_projectid", EdmPrimitiveTypeKind.Guid)]
    [InlineData("_ownerid_value", EdmPrimitiveTypeKind.Guid)]
    [InlineData("_createdby_value", EdmPrimitiveTypeKind.Guid)]
    public void Build_maps_attribute_types(string property, EdmPrimitiveTypeKind kind)
    {
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), NoCustomApis()).Build();

        Type(model, "pb_project").FindProperty(property).Type.PrimitiveKind().Should().Be(kind);
    }

    [Fact]
    public void Build_produces_a_valid_model_and_binds_navigation_targets()
    {
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), NoCustomApis()).Build();

        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
        var set = model.EntityContainer.FindEntitySet("pb_projects");
        var nav = (IEdmNavigationProperty)Type(model, "pb_project").FindProperty("ownerid");
        set.FindNavigationTarget(nav).Name.Should().Be("systemusers");
        // owningteam targets "team", which does not exist: value property only, no navigation property.
        Type(model, "pb_project").FindProperty("_owningteam_value").Should().NotBeNull();
        Type(model, "pb_project").FindProperty("owningteam").Should().BeNull();
    }

    [Fact]
    public void Build_caches_the_model_until_the_version_stamp_changes()
    {
        var store = new FakeStore(Project(), SystemUser());
        var builder = new ODataEdmModelBuilder(store, NoCustomApis());

        var first = builder.Build();
        store.Entities.Add(Project("pb_task", "pb_tasks"));
        builder.Build().Should().BeSameAs(first);

        store.VersionStamp++;
        var rebuilt = builder.Build();
        rebuilt.Should().NotBeSameAs(first);
        rebuilt.FindDeclaredType("Microsoft.Dynamics.CRM.pb_task").Should().NotBeNull();
    }

    [Fact]
    public void Build_leaves_audit_changedata_out_of_the_entity_type()
    {
        var audit = Entity("audit", "audits", OwnershipTypes.None,
            new MemoAttributeMetadata { LogicalName = "changedata", MaxLength = 2000 },
            new MemoAttributeMetadata { LogicalName = "attributemask", MaxLength = 2000 });
        var other = Entity("pb_log", "pb_logs", OwnershipTypes.None, new MemoAttributeMetadata { LogicalName = "changedata", MaxLength = 2000 });

        var model = new ODataEdmModelBuilder(new FakeStore(audit, other), NoCustomApis()).Build();

        Type(model, "audit").FindProperty("changedata").Should().BeNull();
        Type(model, "audit").FindProperty("attributemask").Should().NotBeNull();
        Type(model, "pb_log").FindProperty("changedata").Should().NotBeNull();
        WebApiHiddenAttributes.IsHidden("audit", "changedata").Should().BeTrue();
        WebApiHiddenAttributes.IsHidden("audit", "attributemask").Should().BeFalse();
        WebApiHiddenAttributes.IsHidden("pb_log", "changedata").Should().BeFalse();
    }
    [Fact]
    public void Tracked_entity_set_has_change_tracking_annotation()
    {
        var tracked = Project();
        tracked.ChangeTrackingEnabled = true;
        var untracked = Project("pb_task", "pb_tasks");
        var model = new ODataEdmModelBuilder(new FakeStore(tracked, untracked, SystemUser()), NoCustomApis()).Build();

        IEdmVocabularyAnnotation? Annotation(string set) => model.FindVocabularyAnnotations(model.EntityContainer.FindEntitySet(set))
            .SingleOrDefault(a => a.Term.FullName() == "Org.OData.Capabilities.V1.ChangeTracking");

        var annotation = Annotation("pb_projects");
        annotation.Should().NotBeNull();
        var record = annotation!.Value.Should().BeAssignableTo<IEdmRecordExpression>().Subject;
        record.Properties.Single(p => p.Name == "Supported").Value.Should().BeAssignableTo<IEdmBooleanConstantExpression>()
            .Which.Value.Should().BeTrue();
        Annotation("pb_tasks").Should().BeNull("only tracked tables carry the annotation");
        Annotation("systemusers").Should().BeNull();
        ShouldValidate(model);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream)) CsdlWriter.TryWriteCsdl(model, writer, CsdlTarget.OData, out _).Should().BeTrue();
        var csdl = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        csdl.Should().Contain("Org.OData.Capabilities.V1.ChangeTracking").And.Contain("Property=\"Supported\" Bool=\"true\"");
    }

    [Fact]
    public void Metadata_declares_duplicate_operations()
    {
        var rule = Entity("duplicaterule", "duplicaterules", OwnershipTypes.UserOwned, new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 });
        var job = Entity("asyncoperation", "asyncoperations", OwnershipTypes.UserOwned, new IntegerAttributeMetadata { LogicalName = "operationtype" });
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser(), rule, job), NoCustomApis()).Build();
        ShouldValidate(model);

        var publish = (IEdmAction)Operation(model, "PublishDuplicateRule");
        publish.IsBound.Should().BeTrue();
        publish.Parameters.Should().ContainSingle().Which.Type.Definition.Should().BeSameAs(Type(model, "duplicaterule"));
        publish.GetReturn().Type.Definition.Should().BeSameAs(Type(model, "asyncoperation"));
        publish.GetReturn().Type.IsNullable.Should().BeFalse();
        model.EntityContainer.OperationImports().Should().NotContain(i => i.Name == "PublishDuplicateRule");

        var unpublish = (IEdmAction)Operation(model, "UnpublishDuplicateRule");
        unpublish.IsBound.Should().BeFalse();
        unpublish.GetReturn().Should().BeNull();
        unpublish.FindParameter("DuplicateRuleId").Type.Definition.Should().Be(EdmCoreModel.Instance.GetGuid(false).Definition);
        model.EntityContainer.OperationImports().OfType<IEdmActionImport>().Should().Contain(i => i.Name == "UnpublishDuplicateRule");

        var retrieve = (IEdmFunction)Operation(model, "RetrieveDuplicates");
        retrieve.IsBound.Should().BeFalse();
        retrieve.Parameters.Select(p => p.Name).Should().Equal("BusinessEntity", "MatchingEntityName", "PagingInfo");
        retrieve.FindParameter("BusinessEntity").Type.Definition.Should().BeSameAs(Type(model, "crmbaseentity"));
        retrieve.FindParameter("PagingInfo").Type.IsNullable.Should().BeTrue();
        retrieve.FindParameter("PagingInfo").Type.Definition.Should().BeSameAs(model.FindDeclaredType("Microsoft.Dynamics.CRM.PagingInfo"));
        retrieve.GetReturn().Type.IsCollection().Should().BeTrue();
        retrieve.GetReturn().Type.AsCollection().ElementType().Definition.Should().BeSameAs(Type(model, "crmbaseentity"));
        model.EntityContainer.OperationImports().OfType<IEdmFunctionImport>().Should().Contain(i => i.Name == "RetrieveDuplicates");

        var bulk = (IEdmAction)Operation(model, "BulkDetectDuplicates");
        bulk.Parameters.Select(p => p.Name).Should().Equal("Query", "JobName", "SendEmailNotification", "TemplateId", "ToRecipients",
            "CCRecipients", "RecurrencePattern", "RecurrenceStartTime");
        bulk.FindParameter("Query").Type.Definition.Should().BeSameAs(model.FindDeclaredType("Microsoft.Dynamics.CRM.QueryBase"));
        var response = (IEdmComplexType)bulk.GetReturn().Type.Definition;
        response.FullName().Should().Be("Microsoft.Dynamics.CRM.BulkDetectDuplicatesResponse");
        response.StructuralProperties().Select(p => p.Name).Should().Equal("JobId");
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.QueryExpression")).BaseComplexType()!.Name.Should().Be("QueryBase");
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.FetchExpression")).BaseComplexType()!.Name.Should().Be("QueryBase");
    }

    [Fact]
    public void Publish_is_left_out_without_the_duplicaterule_table()
    {
        var model = new ODataEdmModelBuilder(new FakeStore(Project(), SystemUser()), NoCustomApis()).Build();
        ShouldValidate(model);
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.PublishDuplicateRule").Should().BeEmpty();
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveDuplicates").Should().ContainSingle();
    }
}
