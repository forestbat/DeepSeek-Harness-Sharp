using Dsh.Core;

namespace Dsh.Tests;

public sealed class BuiltInPersonaTests
{
    [Fact]
    public void BuiltInPersona_IsRewrittenForDsh()
    {
        Assert.Contains("You are DSharp", BuiltInPersona.Text);
        Assert.DoesNotContain("Kilo", BuiltInPersona.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Kilo-Org", BuiltInPersona.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", BuiltInPersona.Text, StringComparison.Ordinal);
    }
}
