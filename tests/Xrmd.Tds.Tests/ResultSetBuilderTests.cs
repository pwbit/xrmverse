using FluentAssertions;
using Meziantou.Framework.Tds.Handler;
using Microsoft.Xrm.Sdk;
using Xrmd.Query.Model;
using Xrmd.Tds.Execution;
using Xrmd.Tds.Translation;

namespace Xrmd.Tds.Tests;

/// <summary>The shape of the result sets (plan 3.3, ruling T7): column types from the attribute metadata, values as SQL types.</summary>
public sealed class ResultSetBuilderTests
{
    private static readonly Dictionary<string, object?> NoParameters = [];

    private static TranslatedQuery Query(string sql) =>
        (TranslatedQuery)new SqlTranslator(TestMetadata.Instance).Translate(sql, NoParameters);

    private static TdsResultSet Build(TranslatedQuery query, params Entity[] rows) =>
        ResultSetBuilder.Build(query.Model, query.Columns, rows, TestMetadata.Instance);

    [Fact]
    public void Types_follow_attribute_metadata()
    {
        var query = Query("""
            SELECT name, description, numberofemployees, pb_visits, pb_rating, pb_score, revenue, pb_active, createdon, accountid,
                   primarycontactid, industrycode, statecode, statuscode
            FROM account
            """);
        var created = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var full = new Entity("account", accountId)
        {
            ["name"] = "acme",
            ["description"] = "a long text",
            ["numberofemployees"] = 12,
            ["pb_visits"] = 5_000_000_000L,
            ["pb_rating"] = 1.25m,
            ["pb_score"] = 2.5d,
            ["revenue"] = new Money(10.5m),
            ["pb_active"] = true,
            ["createdon"] = created,
            ["accountid"] = accountId,
            ["primarycontactid"] = new EntityReference("contact", contactId),
            ["industrycode"] = new OptionSetValue(3),
            ["statecode"] = new OptionSetValue(0),
            ["statuscode"] = new OptionSetValue(1),
        };
        var empty = new Entity("account");

        var set = Build(query, full, empty);

        set.Columns.Select(c => c.Name).Should().Equal("name", "description", "numberofemployees", "pb_visits", "pb_rating", "pb_score",
            "revenue", "pb_active", "createdon", "accountid", "primarycontactid", "industrycode", "statecode", "statuscode");
        set.Columns.Select(c => c.ColumnType).Should().Equal(
            TdsColumnType.NVarChar, TdsColumnType.NVarChar, TdsColumnType.Int32, TdsColumnType.Int64, TdsColumnType.Decimal,
            TdsColumnType.Double, TdsColumnType.Money, TdsColumnType.Boolean, TdsColumnType.DateTime2, TdsColumnType.Guid,
            TdsColumnType.Guid, TdsColumnType.Int32, TdsColumnType.Int32, TdsColumnType.Int32);
        set.Columns.Should().AllSatisfy(c => c.IsNullable.Should().BeTrue());
        set.Rows.Should().HaveCount(2);
        set.Rows[0].Should().Equal("acme", "a long text", 12, 5_000_000_000L, 1.25m, 2.5d, 10.5m, true, created, accountId, contactId, 3, 0, 1);
        ((DateTime)set.Rows[0][8]!).Kind.Should().Be(DateTimeKind.Utc);
        set.Rows[1].Should().HaveCount(14).And.AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Dates_without_a_kind_are_utc()
    {
        var query = Query("SELECT createdon FROM account");
        var value = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

        var row = Build(query, new Entity("account") { ["createdon"] = value }).Rows.Single();

        row[0].Should().Be(value);
        ((DateTime)row[0]!).Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Local_dates_are_converted_to_utc()
    {
        var query = Query("SELECT createdon FROM account");
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var row = Build(query, new Entity("account") { ["createdon"] = utc.ToLocalTime() }).Rows.Single();

        row[0].Should().Be(utc);
        ((DateTime)row[0]!).Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Lookup_name_and_choice_label_values()
    {
        var query = Query("SELECT primarycontactidname, statuscodename, industrycodename AS industry FROM account");
        var entity = new Entity("account")
        {
            ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()) { Name = "Ada Lovelace" },
            ["statuscode"] = new OptionSetValue(1),
            ["industrycode"] = new OptionSetValue(3),
        };
        entity.FormattedValues["statuscode"] = "Active";
        entity.FormattedValues["industrycode"] = "Consulting";

        var set = Build(query, entity, new Entity("account"));

        set.Columns.Select(c => (c.Name, c.ColumnType)).Should().Equal(
            ("primarycontactidname", TdsColumnType.NVarChar), ("statuscodename", TdsColumnType.NVarChar), ("industry", TdsColumnType.NVarChar));
        set.Rows[0].Should().Equal("Ada Lovelace", "Active", "Consulting");
        set.Rows[1].Should().HaveCount(3).And.AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Link_columns_are_read_from_aliased_values()
    {
        var query = Query("SELECT a.name, c.fullname, c.contactid FROM account a JOIN contact c ON a.primarycontactid = c.contactid");
        var contactId = Guid.NewGuid();
        var entity = new Entity("account")
        {
            ["name"] = "acme",
            ["c.fullname"] = new AliasedValue("contact", "fullname", "Ada Lovelace"),
            ["c.contactid"] = new AliasedValue("contact", "contactid", contactId),
        };

        var set = Build(query, entity);

        set.Columns.Select(c => (c.Name, c.ColumnType)).Should().Equal(
            ("name", TdsColumnType.NVarChar), ("fullname", TdsColumnType.NVarChar), ("contactid", TdsColumnType.Guid));
        set.Rows.Single().Should().Equal("acme", "Ada Lovelace", contactId);
    }

    [Fact]
    public void Aggregate_columns_take_the_type_of_their_source()
    {
        var query = Query("""
            SELECT ownerid, statecode, COUNT(*) AS c, COUNT(name) AS n, SUM(revenue) s, AVG(numberofemployees) a, MAX(createdon) m
            FROM account GROUP BY ownerid, statecode
            """);
        var ownerId = Guid.NewGuid();
        var last = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var entity = new Entity("account")
        {
            ["ownerid"] = new AliasedValue("account", "ownerid", new EntityReference("systemuser", ownerId)),
            ["statecode"] = new AliasedValue("account", "statecode", new OptionSetValue(1)),
            ["c"] = new AliasedValue("account", "accountid", 4),
            ["n"] = new AliasedValue("account", "name", 3),
            ["s"] = new AliasedValue("account", "revenue", new Money(12.5m)),
            ["a"] = new AliasedValue("account", "numberofemployees", 7.5m),
            ["m"] = new AliasedValue("account", "createdon", last),
        };

        var set = Build(query, entity);

        set.Columns.Select(c => (c.Name, c.ColumnType)).Should().Equal(
            ("ownerid", TdsColumnType.Guid), ("statecode", TdsColumnType.Int32), ("c", TdsColumnType.Int32), ("n", TdsColumnType.Int32),
            ("s", TdsColumnType.Money), ("a", TdsColumnType.Decimal), ("m", TdsColumnType.DateTime2));
        set.Rows.Single().Should().Equal(ownerId, 1, 4, 3, 12.5m, 7.5m, last);
    }

    /// <summary>
    /// SQL Server sends such a column without a name; the TDS server library refuses an empty name, so it gets the name ADO.NET and
    /// Power BI give an unnamed column: <c>Column</c> and its position.
    /// </summary>
    [Fact]
    public void An_aggregate_without_alias_is_named_after_its_position()
    {
        var query = Query("SELECT name, COUNT(*) FROM account GROUP BY name");

        var set = Build(query, new Entity("account")
        {
            ["name"] = new AliasedValue("account", "name", "acme"),
            ["expr"] = new AliasedValue("account", "accountid", 2),
        });

        set.Columns.Select(c => c.Name).Should().Equal("name", "Column2");
        set.Rows.Single().Should().Equal("acme", 2);
    }

    [Fact]
    public void No_rows_still_gives_the_columns()
    {
        var set = Build(Query("SELECT name, revenue FROM account"));

        set.Columns.Select(c => c.ColumnType).Should().Equal(TdsColumnType.NVarChar, TdsColumnType.Money);
        set.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Star_leaves_out_unreadable_columns_and_named_columns_stay()
    {
        var query = Query("SELECT accountnumber AS named, a.*, c.* FROM account a JOIN contact c ON a.primarycontactid = c.contactid");
        var unreadable = new Dictionary<string, IReadOnlySet<string>>
        {
            ["account"] = new HashSet<string> { "accountnumber", "revenue" },
            ["contact"] = new HashSet<string> { "emailaddress1" },
        };

        var columns = ResultSetBuilder.WithoutUnreadableStarColumns(query.Model, query.Columns, t => unreadable[t]);

        columns[0].Name.Should().Be("named");
        columns.Skip(1).Select(c => c.Name).Should().Contain(["name", "fullname"]).And.NotContain(["accountnumber", "revenue", "emailaddress1"]);
        columns.Should().HaveCount(query.Columns.Count - 3);
    }

    [Fact]
    public void Paged_join_is_ordered_by_every_link_primary_key_after_the_user_orders()
    {
        var query = Query("""
            SELECT a.name, c.fullname, u.fullname AS owner
            FROM account a JOIN contact c ON c.parentcustomerid = a.accountid JOIN systemuser u ON a.ownerid = u.systemuserid
            ORDER BY c.fullname DESC
            """);

        TdsQueryHandler.AddLinkKeyOrders(query.Model, TestMetadata.Instance);

        var contact = query.Model.Root.Links.Single(l => l.Alias == "c");
        var user = query.Model.Root.Links.Single(l => l.Alias == "u");
        contact.Orders.Should().Equal(new OrderNode("fullname", true), new OrderNode("contactid", false));
        user.Orders.Should().Equal(new OrderNode("systemuserid", false));
    }

    [Fact]
    public void Distinct_and_link_free_queries_get_no_extra_order()
    {
        var distinct = Query("SELECT DISTINCT c.fullname FROM account a JOIN contact c ON c.parentcustomerid = a.accountid");
        var single = Query("SELECT name FROM account");

        TdsQueryHandler.AddLinkKeyOrders(distinct.Model, TestMetadata.Instance);
        TdsQueryHandler.AddLinkKeyOrders(single.Model, TestMetadata.Instance);

        distinct.Model.Root.Links.Single().Orders.Should().BeEmpty();
        single.Model.Root.Orders.Should().BeEmpty();
    }
}
