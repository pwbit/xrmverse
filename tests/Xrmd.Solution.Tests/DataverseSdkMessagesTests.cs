using FluentAssertions;

namespace Xrmd.Solution.Tests;

public class DataverseSdkMessagesTests
{
    [Theory]
    [InlineData("9ebdbb1b-ea3e-db11-86a7-000a3a5473e8", "Create")]
    [InlineData("20bebb1b-ea3e-db11-86a7-000a3a5473e8", "Update")]
    [InlineData("a1bdbb1b-ea3e-db11-86a7-000a3a5473e8", "Delete")]
    [InlineData("f2bdbb1b-ea3e-db11-86a7-000a3a5473e8", "Retrieve")]
    [InlineData("03bebb1b-ea3e-db11-86a7-000a3a5473e8", "RetrieveMultiple")]
    [InlineData("16e7c888-3b24-448b-b7da-ebfe9b681762", "Associate")]
    public void The_ids_of_a_real_export_map_to_their_message_and_back(string id, string message)
    {
        DataverseSdkMessages.NameOf(Guid.Parse(id)).Should().Be(message);
        DataverseSdkMessages.IdOf(message).Should().Be(Guid.Parse(id));
    }
}
