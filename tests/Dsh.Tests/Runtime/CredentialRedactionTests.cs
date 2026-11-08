using Dsh.Runtime.Logging;

namespace Dsh.Tests.Runtime;

public class CredentialRedactionTests
{
    [Theory]
    [InlineData("API_KEY=", "credential-for-test\nmodel=public-value")]
    [InlineData("{\"clientSecret\":", "\"credential-for-test\\\"tail\",\"model\":\"public-value\"}")]
    [InlineData("password: ", "'credential-for-test''tail'\nmodel: public-value")]
    [InlineData("options:\n  apiKey: ", "|\n    credential-for-test\n    private-second-line\n  model: public-value\n")]
    public void NamedCredentials_AreRemoved_WithoutHidingUnrelatedValues(string prefix, string value)
    {
        // Every credential-shaped value in this test is synthetic.
        var result = Redaction.Apply($"{prefix}{value}");

        Assert.DoesNotContain("credential-for-test", result);
        Assert.DoesNotContain("private-second-line", result);
        Assert.Contains("public-value", result);
    }

    [Fact]
    public void NonCredentialKeySuffix_DoesNotHideOrdinaryContent()
    {
        const string text = "monkey: public-value\nclientSecretName: public-name";

        Assert.Equal(text, Redaction.Apply(text));
    }
}
