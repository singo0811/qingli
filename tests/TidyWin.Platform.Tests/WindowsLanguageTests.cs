using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

public class WindowsLanguageTests
{
    [Theory]
    [InlineData(0x0804, "zh")] // Chinese (Simplified)
    [InlineData(0x0404, "zh")] // Chinese (Traditional)
    [InlineData(0x0C04, "zh")] // Chinese (Hong Kong)
    [InlineData(0x0409, "en")]
    [InlineData(0x0809, "en")]
    [InlineData(0x0407, "other")] // German
    [InlineData(0x0411, "other")] // Japanese
    public void Language_ids_map_to_a_code(int id, string expected)
        => Assert.Equal(expected, WindowsLanguage.PrimaryToCode((ushort)id));

    [Fact]
    public void The_real_call_returns_something_on_windows()
        => Assert.NotNull(WindowsLanguage.TwoLetterCode());
}
