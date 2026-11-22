using System.Data;
using System.Globalization;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Query.Model;

namespace Xrmd.Query.Sql;

/// <summary>
/// Compiles a <see cref="JsonPathConditionNode"/> (plan #78, ruling J3). <see cref="ISqlDialect.JsonMember"/> finds the member
/// the key names in the object or array at the parent path, with its JSON type (0 null, 1 string, 2 number, 3 bool), so a
/// comparison is type-strict; <see cref="ISqlDialect.IsJson"/> keeps a column that is not valid JSON from raising an error
/// (plan 2.7b, Task 5: both engines).
/// </summary>
internal static class JsonPathConditions
{
    public static string Compile(string column, JsonPathConditionNode node, QueryParameters p, ISqlDialect dialect)
    {
        var path = p.Add(DbType.String, node.ParentPath);
        var key = p.Add(DbType.String, node.Key);
        var members = dialect.JsonMember(column, path, key, node.KeyIsIndex);

        switch (node.Test)
        {
            case JsonPathTest.Defined:
                return $"({dialect.IsJson(column)} AND EXISTS ({members}))";
            case JsonPathTest.NotDefined:
                // Undefined includes a null column and a stored value that is not JSON.
                return $"({column} IS NULL OR {dialect.IsJson(column, valid: false)} OR NOT EXISTS ({members}))";
            case JsonPathTest.NotEqual:
                return $"({dialect.IsJson(column)} AND EXISTS ({members} AND NOT ({Typed(node, JsonPathTest.Equal, p, dialect)})))";
            default:
                return $"({dialect.IsJson(column)} AND EXISTS ({members} AND {Typed(node, node.Test, p, dialect)}))";
        }
    }

    /// <summary>The type test and value comparison of one member, for the comparison <paramref name="test"/>.</summary>
    private static string Typed(JsonPathConditionNode node, JsonPathTest test, QueryParameters p, ISqlDialect dialect)
    {
        var op = test switch
        {
            JsonPathTest.Equal => "=",
            JsonPathTest.Less => "<",
            JsonPathTest.LessEqual => "<=",
            JsonPathTest.Greater => ">",
            JsonPathTest.GreaterEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(node), test, null),
        };
        var type = $"j.{dialect.Quote("type")}";
        var value = $"j.{dialect.Quote("value")}";
        switch (node.Value)
        {
            case string s:
                if (test == JsonPathTest.Equal)
                {
                    var v = p.Add(DbType.String, s);
                    return $"{type} = 1 AND {dialect.BinaryEquals(value, v)}";
                }
                return $"{type} = 1 AND {dialect.BinaryCollate(value)} {op} {p.Add(DbType.String, s)}";
            case bool b:
                RequireEquality(test, "bool");
                return $"{type} = 3 AND {value} = '{(b ? "true" : "false")}'";
            case null:
                RequireEquality(test, "null");
                return $"{type} = 0";
            default:
                var number = ToDouble(node.Value);
                // The member's text is a JSON number: null past the largest double on both engines (TryCastText).
                return $"{type} = 2 AND {dialect.TryCastText(value, "float")} {op} {p.Add(DbType.Double, number)}";
        }
    }

    private static void RequireEquality(JsonPathTest test, string type)
    {
        if (test != JsonPathTest.Equal)
            throw new XrmdValidationException($"A range comparison with a {type} value is not supported on a JSON path.");
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float or int or long or short or byte or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw new XrmdValidationException($"A JSON path condition cannot compare with a value of type {value.GetType().Name}."),
    };
}
