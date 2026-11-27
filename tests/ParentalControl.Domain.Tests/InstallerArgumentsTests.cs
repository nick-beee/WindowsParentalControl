using ParentalControl.Domain.Updates;

namespace ParentalControl.Domain.Tests;

public class InstallerArgumentsTests
{
    private static readonly DateTime Start = new(2026, 11, 27, 9, 5, 3);

    [Fact]
    public void SilentUpdateLogsToTheLogsFolder() =>
        Assert.Equal(@"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""C:\ProgramData\ParentalControl\logs\setup-20261127-090503.log""",
            InstallerArguments.Build(true, @"C:\ProgramData\ParentalControl\logs", Start));

    [Fact]
    public void NormalSetupAlsoLogs() =>
        Assert.Equal(@"/LOG=""C:\ProgramData\ParentalControl\logs\setup-20261127-090503.log""",
            InstallerArguments.Build(false, @"C:\ProgramData\ParentalControl\logs\", Start));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\a\"b")]
    public void NoLogWithoutAUsableFolder(string? folder)
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", InstallerArguments.Build(true, folder, Start));
        Assert.Equal("", InstallerArguments.Build(false, folder, Start));
    }
}
