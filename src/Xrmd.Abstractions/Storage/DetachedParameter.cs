using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Xrmd.Abstractions.Storage;

/// <summary>
/// A parameter built before its command exists (a compiled query, a row's column values): it only carries the name, type
/// and value. <see cref="DbCommandExtensions.AddParameters"/> copies it onto a command as the provider's own parameter; it is
/// never added to a command as it is (plan 2.7a, ruling P3).
/// </summary>
public sealed class DetachedParameter : DbParameter
{
    private object? _value = DBNull.Value;

    /// <summary>A parameter <paramref name="name"/> of <paramref name="type"/>; a null <paramref name="value"/> is <see cref="DBNull.Value"/>.</summary>
    public DetachedParameter(string name, DbType type, object? value, int size = 0, byte precision = 0, byte scale = 0)
    {
        ParameterName = name;
        DbType = type;
        Value = value;
        Size = size;
        Precision = precision;
        Scale = scale;
    }

    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = "";

    public override int Size { get; set; }

    public override byte Precision { get; set; }

    public override byte Scale { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = "";

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value
    {
        get => _value;
        set => _value = value ?? DBNull.Value;
    }

    public override void ResetDbType() => DbType = DbType.String;
}
