using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Tds.Translation;

namespace Xrmd.Tds.Tests;

public sealed class SqlTranslatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    private static TranslatedBatch Translate(string sql, IReadOnlyDictionary<string, object?>? parameters = null) =>
        new SqlTranslator(TestMetadata.Instance).Translate(sql, parameters ?? NoParameters);

    private static TranslatedQuery Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null) =>
        Translate(sql, parameters).Should().BeOfType<TranslatedQuery>().Subject;

    private static TdsSqlException Error(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var act = () => Translate(sql, parameters);
        return act.Should().Throw<TdsSqlException>().Which;
    }

    [Fact]
    public void Select_columns_top_where_order()
    {
        var q = Query("SELECT TOP 5 a.name AS n, a.accountnumber FROM dbo.account a WHERE a.name LIKE 'A%' AND a.statecode = 0 ORDER BY a.name DESC");

        q.Model.Root.EntityName.Should().Be("account");
        q.Model.Root.AllColumns.Should().BeFalse();
        q.Model.Root.Columns.Select(c => c.Attribute).Should().Equal("name", "accountnumber");
        q.Model.Root.Filter.Operator.Should().Be(LogicalOperator.And);
        q.Model.Root.Filter.Filters.Should().BeEmpty();
        q.Model.Root.Filter.Conditions.Should().HaveCount(2);
        q.Model.Root.Filter.Conditions[0].Should().BeEquivalentTo(new ConditionNode(null, "name", ConditionOperator.Like, ["A%"]));
        q.Model.Root.Filter.Conditions[1].Should().BeEquivalentTo(new ConditionNode(null, "statecode", ConditionOperator.Equal, [0]));
        q.Model.Root.Orders.Should().Equal(new OrderNode("name", true));
        q.Model.OrderByStoredValues.Should().BeTrue(); // SQL ORDER BY semantics: a lookup or a choice by its stored value
        q.Model.Top.Should().Be(5);
        q.Model.Aggregate.Should().BeFalse();
        q.Columns.Select(c => c.Name).Should().Equal("n", "accountnumber");
        q.Columns.Should().AllSatisfy(c => c.Value.Should().Be(OutputValue.Raw));
        q.Columns.Select(c => c.Attribute).Should().Equal("name", "accountnumber");
    }

    [Theory]
    [InlineData("SELECT [Name] FROM [DBO].[ACCOUNT]")]
    [InlineData("SELECT name FROM account")]
    [InlineData("SELECT NAME FROM Account")]
    [InlineData("SELECT \"name\" FROM [account]")]
    [InlineData("SELECT ACCOUNT.Name FROM dbo.Account")]
    public void Brackets_case_and_schema_resolve(string sql)
    {
        var q = Query(sql);

        q.Model.Root.EntityName.Should().Be("account");
        q.Model.Root.Columns.Should().Equal(new ColumnNode("name"));
        q.Columns.Should().ContainSingle().Which.Attribute.Should().Be("name");
    }

    [Theory]
    [InlineData("SELECT a.name, c.fullname FROM account a INNER JOIN contact c ON a.primarycontactid = c.contactid", JoinOperator.Inner)]
    [InlineData("SELECT a.name, c.fullname FROM account a JOIN contact c ON c.contactid = a.primarycontactid", JoinOperator.Inner)]
    [InlineData("SELECT a.name, c.fullname FROM account a LEFT JOIN contact c ON a.primarycontactid = c.contactid", JoinOperator.LeftOuter)]
    [InlineData("SELECT a.name, c.fullname FROM account a LEFT OUTER JOIN contact c ON c.contactid = a.primarycontactid", JoinOperator.LeftOuter)]
    public void Join_on_lookup_becomes_link(string sql, JoinOperator join)
    {
        var q = Query(sql);

        var link = q.Model.Root.Links.Should().ContainSingle().Subject;
        link.EntityName.Should().Be("contact");
        link.From.Should().Be("contactid");
        link.To.Should().Be("primarycontactid");
        link.JoinType.Should().Be(join);
        link.Alias.Should().Be("c");
        link.Columns.Should().Equal(new ColumnNode("fullname"));
        q.Model.Root.Columns.Should().Equal(new ColumnNode("name"));
        q.Columns.Select(c => (c.Name, c.EntityAlias, c.Attribute)).Should().Equal(("name", "a", "name"), ("fullname", "c", "fullname"));
    }

    [Fact]
    public void Join_from_the_referenced_side_and_conditions_on_the_link()
    {
        var q = Query("SELECT c.fullname FROM contact c JOIN account a ON a.primarycontactid = c.contactid WHERE a.name = 'Acme' ORDER BY c.fullname");

        var link = q.Model.Root.Links.Should().ContainSingle().Subject;
        link.EntityName.Should().Be("account");
        link.From.Should().Be("primarycontactid");
        link.To.Should().Be("contactid");
        q.Model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode("a", "name", ConditionOperator.Equal, ["Acme"]));
        q.Model.Root.Orders.Should().Equal(new OrderNode("fullname", false));
    }

    [Theory]
    [InlineData("SELECT a.name FROM account a JOIN contact c ON a.name = c.fullname")]
    [InlineData("SELECT a.name FROM account a JOIN contact c ON a.primarycontactid = c.contactid AND c.fullname = 'x'")]
    [InlineData("SELECT a.name FROM account a RIGHT JOIN contact c ON a.primarycontactid = c.contactid")]
    [InlineData("SELECT a.name FROM account a, contact c")]
    [InlineData("SELECT a.name FROM account a CROSS APPLY contact c")]
    public void Joins_other_than_lookup_to_key_are_refused(string sql) =>
        Error(sql).Number.Should().Be(40000);

    [Fact]
    public void Lookup_name_and_choice_label_columns()
    {
        var q = Query("SELECT primarycontactidname, statecodename FROM account");

        q.Model.Root.Columns.Select(c => c.Attribute).Should().Equal("primarycontactid", "statecode");
        q.Columns.Select(c => (c.Name, c.Attribute, c.Value)).Should().Equal(
            ("primarycontactidname", "primarycontactid", OutputValue.Name),
            ("statecodename", "statecode", OutputValue.Label));
    }

    [Fact]
    public void A_column_and_its_name_share_one_model_column()
    {
        var q = Query("SELECT ownerid, owneridname, industrycodename FROM account");

        q.Model.Root.Columns.Select(c => c.Attribute).Should().Equal("ownerid", "industrycode");
        q.Columns.Select(c => c.Value).Should().Equal(OutputValue.Raw, OutputValue.Name, OutputValue.Label);
    }

    [Fact]
    public void Group_by_with_aggregates()
    {
        var q = Query("SELECT ownerid, COUNT(*) AS c, SUM(revenue) s FROM account GROUP BY ownerid");

        q.Model.Aggregate.Should().BeTrue();
        var columns = q.Model.Root.Columns;
        columns.Should().HaveCount(3);
        columns[0].Should().Be(new ColumnNode("ownerid", "ownerid", GroupBy: true));
        columns[1].Should().Be(new ColumnNode("accountid", "c", XrmAggregate.Count));
        columns[2].Should().Be(new ColumnNode("revenue", "s", XrmAggregate.Sum));
        q.Columns.Select(c => (c.Name, c.Attribute, c.Value)).Should().Equal(
            ("ownerid", "ownerid", OutputValue.Aggregate), ("c", "c", OutputValue.Aggregate), ("s", "s", OutputValue.Aggregate));
    }

    [Fact]
    public void Count_distinct_and_order_by_alias()
    {
        var q = Query("SELECT COUNT(DISTINCT name) AS n, MAX(revenue) AS m FROM account ORDER BY n DESC");

        q.Model.Aggregate.Should().BeTrue();
        q.Model.Root.Columns.Should().Equal(
            new ColumnNode("name", "n", XrmAggregate.CountColumn, Distinct: true),
            new ColumnNode("revenue", "m", XrmAggregate.Max));
        q.Model.Root.Orders.Should().Equal(new OrderNode("n", true, "n"));
    }

    [Theory]
    [InlineData("SELECT name, COUNT(*) AS c FROM account")]
    [InlineData("SELECT ownerid, COUNT(*) AS c FROM account GROUP BY ownerid HAVING COUNT(*) > 1")]
    [InlineData("SELECT SUM(DISTINCT revenue) AS s FROM account")]
    public void Aggregate_queries_outside_the_model_are_refused(string sql) =>
        Error(sql).Number.Should().Be(40000);

    [Fact]
    public void Parameters_bind()
    {
        var q = Query("SELECT name FROM account WHERE name = @n", new Dictionary<string, object?> { ["@n"] = "Acme" });

        q.Model.Root.Filter.Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConditionNode(null, "name", ConditionOperator.Equal, ["Acme"]));

        // Names without '@' and in another case bind too (SQL Server variable names ignore case).
        Query("SELECT name FROM account WHERE name = @N", new Dictionary<string, object?> { ["n"] = "Acme" })
            .Model.Root.Filter.Conditions.Single().Values.Should().Equal("Acme");

        var e = Error("SELECT name FROM account WHERE name = @n");
        e.Number.Should().Be(137);
        e.Message.Should().Contain("@n");
    }

    [Fact]
    public void Where_operators_map_and_not_is_pushed_down()
    {
        var q = Query("""
            SELECT name FROM account
            WHERE (name NOT LIKE 'Z%' OR accountnumber IS NULL)
              AND numberofemployees BETWEEN 10 AND -5
              AND industrycode IN (1, 2)
              AND NOT (statecode <> 1 OR numberofemployees >= 3)
              AND 7 < numberofemployees
            """);

        var f = q.Model.Root.Filter;
        f.Operator.Should().Be(LogicalOperator.And);
        f.Conditions.Should().BeEquivalentTo(new[]
        {
            new ConditionNode(null, "numberofemployees", ConditionOperator.Between, [10, -5]),
            new ConditionNode(null, "industrycode", ConditionOperator.In, [1, 2]),
            new ConditionNode(null, "statecode", ConditionOperator.Equal, [1]),
            new ConditionNode(null, "numberofemployees", ConditionOperator.LessThan, [3]),
            new ConditionNode(null, "numberofemployees", ConditionOperator.GreaterThan, [7]),
        }, o => o.WithStrictOrdering());
        var or = f.Filters.Should().ContainSingle().Subject;
        or.Operator.Should().Be(LogicalOperator.Or);
        or.Conditions.Should().BeEquivalentTo(new[]
        {
            new ConditionNode(null, "name", ConditionOperator.NotLike, ["Z%"]),
            new ConditionNode(null, "accountnumber", ConditionOperator.Null, []),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Distinct_and_star_on_a_link()
    {
        var q = Query("SELECT DISTINCT c.* FROM account a JOIN contact c ON a.primarycontactid = c.contactid");

        q.Model.Distinct.Should().BeTrue();
        q.Model.Root.Columns.Should().BeEmpty();
        q.Model.Root.Links.Single().Columns.Select(c => c.Attribute).Should().Contain(["contactid", "fullname", "emailaddress1"]);
        q.Columns.Should().AllSatisfy(c => c.EntityAlias.Should().Be("c"));
    }

    [Theory]
    [InlineData("DELETE FROM account", "DELETE")]
    [InlineData("UPDATE account SET name = 'x'", "UPDATE")]
    [InlineData("SELECT 1; SELECT 2", "multiple statements")]
    [InlineData("SELECT name FROM account; DROP TABLE account", "multiple statements")]
    [InlineData("EXEC sp_who", "EXEC")]
    [InlineData("SELECT * FROM account WHERE accountid IN (SELECT accountid FROM contact)", "subqueries")]
    [InlineData("SELECT * FROM account WHERE EXISTS (SELECT contactid FROM contact)", "subqueries")]
    [InlineData("WITH x AS (SELECT 1 a) SELECT a FROM x", "common table expressions")]
    [InlineData("SELECT name FROM account UNION SELECT fullname FROM contact", "UNION")]
    [InlineData("SELECT UPPER(name) FROM account", "UPPER")]
    [InlineData("SELECT name FROM account WHERE UPPER(name) = 'X'", "UPPER")]
    [InlineData("SELECT name FROM account CROSS JOIN contact", "CROSS JOIN")]
    [InlineData("SELECT name FROM account ORDER BY name OFFSET 5 ROWS", "OFFSET")]
    [InlineData("SELECT name INTO #t FROM account", "INTO")]
    [InlineData("SELECT name FROM account WITH (NOLOCK)", "table hints")]
    [InlineData("SELECT name FROM account FOR JSON PATH", "FOR")]
    [InlineData("SELECT name + 'x' FROM account", "expressions")]
    [InlineData("SELECT name FROM account WHERE name = accountnumber", "column")]
    [InlineData("SELECT TOP 10 PERCENT name FROM account", "PERCENT")]
    [InlineData("SET @x = 1", "SET")]
    [InlineData("SELECT * FROM sys.objects", "sys.objects")]
    [InlineData("SELECT @@SERVERNAME", "@@SERVERNAME")]
    [InlineData("SELECT SERVERPROPERTY('MachineName')", "MachineName")]
    [InlineData("SELECT name FROM account WHERE name = 'a' COLLATE Latin1_General_BIN", "COLLATE")]
    public void Unsupported_are_refused(string sql, string construct)
    {
        var e = Error(sql);

        e.Number.Should().Be(40000);
        e.Message.Should().StartWith("The SQL endpoint does not support ").And.Contain(construct);
    }

    [Fact]
    public void Syntax_errors_are_102() => Error("SELEC name FROM account").Number.Should().Be(102);

    [Theory]
    [InlineData("SELECT name FROM nosuchtable", 208, "nosuchtable")]
    [InlineData("SELECT name FROM dbo.account a JOIN other.contact c ON a.primarycontactid = c.contactid", 208, "other.contact")]
    [InlineData("SELECT nosuchcolumn FROM account", 207, "nosuchcolumn")]
    [InlineData("SELECT name FROM account WHERE nosuchcolumn = 1", 207, "nosuchcolumn")]
    [InlineData("SELECT x.name FROM account a", 207, "x.name")]
    [InlineData("SELECT fullnamename FROM contact", 207, "fullnamename")]
    public void Unknown_names(string sql, int number, string name)
    {
        var e = Error(sql);

        e.Number.Should().Be(number);
        e.Message.Should().Contain(name);
    }

    [Theory]
    [InlineData("SELECT pb_document FROM account")]
    [InlineData("SELECT pb_regions FROM account")]
    [InlineData("SELECT pb_logo FROM account")]
    [InlineData("SELECT name FROM account WHERE pb_regions = 1")]
    public void Unsupported_types_are_invalid_columns(string sql) => Error(sql).Number.Should().Be(207);

    [Fact]
    public void Star_expands_to_supported_columns()
    {
        var q = Query("SELECT * FROM account");

        q.Model.Root.AllColumns.Should().BeFalse();
        var attributes = q.Model.Root.Columns.Select(c => c.Attribute).ToList();
        attributes.Should().Contain(["accountid", "name", "accountnumber", "primarycontactid", "revenue", "statecode", "ownerid"]);
        attributes.Should().NotContain(["pb_document", "pb_logo", "pb_regions"]);
        attributes.Should().NotContain(a => a.EndsWith("name") && a != "name");
        attributes[0].Should().Be("accountid");
        q.Columns.Should().AllSatisfy(c => c.Value.Should().Be(OutputValue.Raw));
        q.Columns.Select(c => c.Name).Should().Equal(attributes);
    }

    [Fact]
    public void Star_columns_are_marked_and_named_columns_are_not()
    {
        var q = Query("SELECT name AS n, * FROM account");

        q.Columns[0].FromStar.Should().BeFalse();
        q.Columns.Skip(1).Should().AllSatisfy(c => c.FromStar.Should().BeTrue());
    }

    public static TheoryData<string, SystemQueryKind?, string?> SystemQueryCases => new()
    {
        { "SELECT @@VERSION", SystemQueryKind.Version, null },
        { "select serverproperty('Edition')", SystemQueryKind.ServerProperty, "Edition" },
        { "SELECT SERVERPROPERTY('ProductVersion') AS v", SystemQueryKind.ServerProperty, "ProductVersion" },
        { "SELECT DB_NAME()", SystemQueryKind.DatabaseName, null },
        { "SELECT @@SPID", SystemQueryKind.Spid, null },
        { "SET NOCOUNT ON", null, null },
        { "SET NOCOUNT ON; SET ANSI_NULLS ON; SET TRANSACTION ISOLATION LEVEL READ COMMITTED", null, null },
        { "SELECT * FROM INFORMATION_SCHEMA.TABLES", SystemQueryKind.Tables, null },
        { "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'account'", SystemQueryKind.Columns, "account" },
        { "SELECT * FROM information_schema.columns", SystemQueryKind.Columns, null },
    };

    [Theory]
    [MemberData(nameof(SystemQueryCases))]
    public void System_queries(string sql, SystemQueryKind? kind, string? argument)
    {
        var batch = Translate(sql);

        if (kind is null)
        {
            batch.Should().BeOfType<NoOp>();
            return;
        }
        var system = batch.Should().BeOfType<SystemQuery>().Subject;
        system.Kind.Should().Be(kind);
        system.Argument.Should().Be(argument);
    }

    [Fact]
    public void System_query_columns_are_named()
    {
        Translate("SELECT SERVERPROPERTY('Edition') AS e").Should().BeOfType<SystemQuery>().Which.Columns.Should().Equal("e");
        Translate("SELECT @@VERSION").Should().BeOfType<SystemQuery>().Which.Columns.Should().Equal("");
        Translate("SELECT table_name, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES").Should().BeOfType<SystemQuery>()
            .Which.Columns.Should().Equal("TABLE_NAME", "TABLE_TYPE");
        Error("SELECT NOPE FROM INFORMATION_SCHEMA.TABLES").Number.Should().Be(207);
    }

    [Theory]
    [InlineData("0", typeof(int))]
    [InlineData("2147483647", typeof(int))]
    [InlineData("2147483648", typeof(long))]
    [InlineData("9223372036854775808", typeof(decimal))]
    [InlineData("-5", typeof(int))]
    [InlineData("-2147483648", typeof(long))]
    [InlineData("1.5", typeof(decimal))]
    [InlineData("-1.5", typeof(decimal))]
    [InlineData("1e3", typeof(double))]
    [InlineData("-1e3", typeof(double))]
    public void Literals_keep_their_type(string literal, Type type) =>
        Query($"SELECT name FROM account WHERE numberofemployees = {literal}")
            .Model.Root.Filter.Conditions.Single().Values.Single().Should().BeOfType(type);

    [Fact]
    public void In_and_between_values_keep_their_type()
    {
        var conditions = Query("SELECT name FROM account WHERE industrycode IN (1, 2) AND numberofemployees BETWEEN 1 AND 3000000000")
            .Model.Root.Filter.Conditions;

        conditions[0].Values.Should().AllSatisfy(v => v.Should().BeOfType<int>());
        conditions[1].Values[0].Should().BeOfType<int>();
        conditions[1].Values[1].Should().BeOfType<long>();
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE numberofemployees = 99999999999999999999999999999999")]
    [InlineData("SELECT name FROM account WHERE numberofemployees = 99999999999999999999999999999999.5")]
    [InlineData("SELECT name FROM account WHERE numberofemployees = 1e400")]
    public void Out_of_range_numbers_are_refused(string sql)
    {
        var e = Error(sql);

        e.Number.Should().Be(40000);
        e.Message.Should().Contain("out of range");
    }

    [Fact]
    public void Translated_models_compile()
    {
        var compiler = new SqlQueryCompiler(TestMetadata.Instance, Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance);
        var context = new QueryContext(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));
        string[] sqls =
        [
            "SELECT name FROM account WHERE statecode = 0",
            "SELECT name FROM account WHERE numberofemployees BETWEEN 1 AND 2",
            "SELECT name FROM account WHERE industrycode IN (1, 2)",
            "SELECT name FROM account WHERE NOT (numberofemployees >= -3) AND revenue > 1.5 AND statuscode <> 2",
            "SELECT TOP (@t) a.name, c.fullname FROM account a LEFT JOIN contact c ON a.primarycontactid = c.contactid WHERE c.fullname LIKE 'A%' ORDER BY a.name",
            "SELECT ownerid, COUNT(*) AS c, SUM(revenue) s FROM account GROUP BY ownerid ORDER BY c DESC",
        ];

        foreach (var sql in sqls)
        {
            var q = Query(sql, new Dictionary<string, object?> { ["@t"] = 10 });
            var compile = () => compiler.Compile(q.Model, context);
            compile.Should().NotThrow(sql);
        }
    }

    [Fact]
    public void Distinct_aggregate_is_translated_and_compiles()
    {
        var q = Query("SELECT DISTINCT COUNT(*) AS c FROM account");

        q.Model.Distinct.Should().BeTrue();
        q.Model.Aggregate.Should().BeTrue();
        var context = new QueryContext(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));
        new SqlQueryCompiler(TestMetadata.Instance, Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Compile(q.Model, context).Sql.Should().Contain("SELECT DISTINCT");
    }

    [Theory]
    [InlineData((short)7)]
    [InlineData((byte)7)]
    [InlineData(7L)]
    [InlineData(7)]
    public void Top_takes_integral_parameters(object value) =>
        Query("SELECT TOP (@t) name FROM account", new Dictionary<string, object?> { ["@t"] = value }).Model.Top.Should().Be(7);

    [Theory]
    [InlineData("7")]
    [InlineData(7.0)]
    [InlineData(-1)]
    [InlineData(3000000000L)]
    public void Top_refuses_other_parameters(object value) =>
        Error("SELECT TOP (@t) name FROM account", new Dictionary<string, object?> { ["@t"] = value }).Number.Should().Be(40000);

    [Fact]
    public void Top_refuses_a_null_parameter()
    {
        var e = Error("SELECT TOP (@t) name FROM account", new Dictionary<string, object?> { ["@t"] = DBNull.Value });

        e.Number.Should().Be(40000);
        e.Message.Should().Contain("TOP").And.Contain("@t");
    }

    public static TheoryData<object> NonScalarParameters => new()
    {
        new byte[] { 1, 2 },
        new[] { 1, 2 },
        new System.Data.DataTable(),
        new object(),
        double.NaN,
        float.PositiveInfinity,
    };

    [Theory]
    [MemberData(nameof(NonScalarParameters))]
    public void Non_scalar_parameters_are_refused(object value)
    {
        var e = Error("SELECT name FROM account WHERE name = @n", new Dictionary<string, object?> { ["@n"] = value });

        e.Number.Should().Be(40000);
        e.Message.Should().Contain("@n");
    }

    [Fact]
    public void Parameter_names_that_collide_are_refused()
    {
        var e = Error("SELECT name FROM account WHERE name = @n", new Dictionary<string, object?> { ["@n"] = "a", ["N"] = "b" });

        e.Number.Should().Be(40000);
        e.Message.Should().Contain("@n");
    }

    [Theory]
    [InlineData("SET FMTONLY ON")]
    [InlineData("SET FMTONLY ON; SELECT name FROM account")]
    [InlineData("SET FMTONLY OFF; SET FMTONLY ON; SELECT name FROM account")]
    public void Fmtonly_on_is_refused(string sql)
    {
        var e = Error(sql);

        e.Number.Should().Be(40000);
        e.Message.Should().Contain("SET FMTONLY ON");
    }

    [Fact]
    public void Fmtonly_off_alone_is_ignored()
    {
        Translate("SET FMTONLY OFF").Should().BeOfType<NoOp>();
    }

    [Fact]
    public void Fmtonly_off_before_a_query_is_ignored()
    {
        Query("SET FMTONLY OFF; SELECT name FROM account").Model.Root.EntityName.Should().Be("account");
    }

    [Theory]
    [InlineData("SELECT pb_document_name FROM account")]
    [InlineData("SELECT pb_logoid FROM account")]
    [InlineData("SELECT pb_logo_timestamp FROM account")]
    [InlineData("SELECT pb_logo_url FROM account")]
    [InlineData("SELECT name FROM account WHERE pb_logoid IS NULL")]
    [InlineData("SELECT name FROM account ORDER BY pb_document_name")]
    public void File_and_image_companions_are_invalid_columns(string sql) => Error(sql).Number.Should().Be(207);

    [Fact]
    public void Star_leaves_out_file_and_image_companions() =>
        Query("SELECT * FROM account").Model.Root.Columns.Select(c => c.Attribute)
            .Should().NotContain(["pb_document_name", "pb_logoid", "pb_logo_timestamp", "pb_logo_url"]);

    [Fact]
    public void Set_before_a_query_is_ignored() =>
        Query("SET NOCOUNT ON; SELECT name FROM account").Model.Root.EntityName.Should().Be("account");
}
