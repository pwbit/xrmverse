using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Xrmd.Core.Errors;
using Xrmd.Solution;

namespace Xrmd.Solution.Tests;

/// <summary>
/// <see cref="SolutionImporter.ReadRequest"/>: the path ImportSolution, ImportSolutionAsync and ExecuteAsync(ImportSolution)
/// share on SOAP and the Web API.
/// </summary>
public class ImportRequestTests
{
    [Fact]
    public void A_file_over_95_MB_is_ImportFileTooLargeToUpload()
    {
        var request = new ImportSolutionRequest { CustomizationFile = new byte[SolutionImporter.MaxCustomizationFileBytes + 1] };

        var act = () => SolutionImporter.ReadRequest(request);

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(unchecked((int)0x80040375));
        fault.Message.Should().Be("The import file is too large to upload.");
        fault.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void A_file_of_exactly_95_MB_is_read()
    {
        var file = new byte[SolutionImporter.MaxCustomizationFileBytes];

        SolutionImporter.ReadRequest(new ImportSolutionRequest { CustomizationFile = file }).File.Should().BeSameAs(file);
    }
}
