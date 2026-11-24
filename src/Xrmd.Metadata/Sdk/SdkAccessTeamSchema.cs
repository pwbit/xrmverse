using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="AccessTeamSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class AccessTeamSchema
{
    /// <summary>The SDK twin of <see cref="AllowsAccessTeams(TableOwnership?)"/>.</summary>
    public static bool AllowsAccessTeams(OwnershipTypes? ownership) => AllowsAccessTeams((TableOwnership?)(int?)ownership);
}
