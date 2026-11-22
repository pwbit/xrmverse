namespace Xrmd.Pipeline.Tests;

/// <summary>The bytes of the net462 <c>Xrmd.TestPlugins.dll</c> copied next to the test assembly.</summary>
public static class TestPluginAssembly
{
    /// <summary>The raw assembly bytes.</summary>
    public static byte[] Bytes { get; } = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll"));

    /// <summary>The raw bytes of the early-bound <c>Xrmd.TestPlugins.EarlyBound.dll</c> (<c>[assembly: ProxyTypesAssembly]</c>).</summary>
    public static byte[] EarlyBoundBytes { get; } =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.EarlyBound.dll"));

    /// <summary>The raw bytes of <c>Xrmd.TestWorkflowActivities.dll</c> (custom workflow activities, plan 2.14 W17).</summary>
    public static byte[] WorkflowActivityBytes { get; } =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestWorkflowActivities.dll"));

    /// <summary>
    /// <paramref name="bytes"/> with another module version id: a different content with the same assembly identity, as a
    /// rebuild of the same project gives (the strong-name signature is not checked by .NET).
    /// </summary>
    public static byte[] Rebuilt(byte[] bytes)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToByteArray();
        var copy = (byte[])bytes.Clone();
        var at = copy.AsSpan().IndexOf(mvid);
        if (at < 0) throw new InvalidOperationException("MVID not found.");
        Guid.NewGuid().ToByteArray().CopyTo(copy, at);
        return copy;
    }
}
