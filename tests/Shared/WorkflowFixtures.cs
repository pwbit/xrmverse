using Xrmd.Workflows;

namespace Xrmd.TestSupport;

/// <summary>
/// The hand-written classic workflow XAML fixtures of plan 2.14 (<c>tests/fixtures/workflows</c>, Task 1) and definition rows built
/// on them, for tests that create processes through <see cref="WorkflowStore.SaveAsync"/>, as the solution import does (plan 2.14's W3;
/// requests author them since plan 2.21, A16).
/// </summary>
public static class WorkflowFixtures
{
    /// <summary>The XAML of the fixture <c>{name}.xaml</c>, copied next to the test assembly.</summary>
    public static string Xaml(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", name + ".xaml"));

    /// <summary>
    /// A Draft definition (type 1) of <paramref name="fixture"/>'s XAML on <paramref name="primaryEntity"/> with a new id: by default a
    /// background workflow (category 0, mode 0) triggered on create.
    /// </summary>
    public static WorkflowRow Definition(string fixture, string name, string primaryEntity = "account", int category = 0, int mode = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = category,
        Mode = mode,
        Type = 1,
        Scope = 4,
        PrimaryEntity = primaryEntity,
        Xaml = Xaml(fixture),
        TriggerOnCreate = category == 0,
        CreateStage = category == 0 && mode == 1 ? 40 : null,
    };
}
