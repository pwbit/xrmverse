using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;

namespace Xrmd.Query.Sdk;

/// <summary>Conversions between the model query enums and the SDK's. Names and numeric values are identical (pinned by QueryEnumParityTests).</summary>
public static class SdkQueryEnums
{
    public static FilterOperator ToModel(this ConditionOperator value) => Convert<ConditionOperator, FilterOperator>(value);
    public static FilterLogic ToModel(this LogicalOperator value) => Convert<LogicalOperator, FilterLogic>(value);
    public static JoinKind ToModel(this JoinOperator value) => Convert<JoinOperator, JoinKind>(value);

    public static ConditionOperator ToSdk(this FilterOperator value) => Convert<FilterOperator, ConditionOperator>(value);
    public static LogicalOperator ToSdk(this FilterLogic value) => Convert<FilterLogic, LogicalOperator>(value);
    public static JoinOperator ToSdk(this JoinKind value) => Convert<JoinKind, JoinOperator>(value);

    private static TTo Convert<TFrom, TTo>(TFrom value) where TFrom : struct, Enum where TTo : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a defined {typeof(TFrom).Name}.");
        var number = System.Convert.ToInt32(value);
        var result = (TTo)Enum.ToObject(typeof(TTo), number);
        if (!Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(value), value, $"No {typeof(TTo).Name} has value {number}.");
        return result;
    }
}
