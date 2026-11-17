using System.Text;
using FluentAssertions;
using ReStore.Core.src.cli;

namespace ReStore.Tests;

public class ConsoleStreamsTests
{
    [Theory]
    [InlineData(65001, true)]  // UTF-8
    [InlineData(1200, true)]   // UTF-16 LE
    [InlineData(1201, true)]   // UTF-16 BE
    [InlineData(12000, true)]  // UTF-32 LE
    [InlineData(12001, true)]  // UTF-32 BE
    [InlineData(28591, false)] // ISO-8859-1 (legacy OEM/ANSI code pages such as 437/850/1252 behave the same: they are not built in on every runtime)
    [InlineData(20127, false)] // US-ASCII
    public void IsUnicode_RecognisesTheEncodingsThatCanHoldEveryCharacter(int codePage, bool expected)
    {
        Encoding encoding = codePage switch
        {
            65001 => new UTF8Encoding(false),
            1200 => new UnicodeEncoding(false, false),
            1201 => new UnicodeEncoding(true, false),
            12000 => new UTF32Encoding(false, false),
            12001 => new UTF32Encoding(true, false),
            20127 => Encoding.ASCII,
            _ => Encoding.Latin1
        };

        ConsoleStreams.IsUnicode(encoding).Should().Be(expected);
    }

    [Fact]
    public void UseUtf8ForRedirectedStreams_LeavesTheStreamsAloneOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // On Windows it would replace the test host's own streams.
        }

        var before = (Console.Out, Console.Error, Console.In);

        ConsoleStreams.UseUtf8ForRedirectedStreams();

        (Console.Out, Console.Error, Console.In).Should().Be(before);
    }
}
