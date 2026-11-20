using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class LogonIdentityTests
{
    [Theory]
    [InlineData("alice", "CONTOSO", null, "alice", "CONTOSO")]
    [InlineData("bob", "CONTOSO", null, "bob", "CONTOSO")]
    [InlineData(" bob ", "PC", null, "bob", "PC")]
    [InlineData(@".\bob", "CONTOSO", null, "bob", ".")]
    [InlineData(@"PC\bob", "CONTOSO", null, "bob", "PC")]
    [InlineData(@"OTHER\bob", "CONTOSO", null, "bob", "OTHER")]
    [InlineData(@"MicrosoftAccount\alice@example.com", "CONTOSO", null, "alice@example.com", "MicrosoftAccount")]
    [InlineData("alice@example.org", "CONTOSO", null, "alice@example.org", null)]
    [InlineData("alice@example.com", "PC", "MicrosoftAccount", "alice@example.com", "MicrosoftAccount")]
    public void NamesHaveOneConsistentDomainRule(
        string configured, string defaultDomain, string? upnDomain, string user, string? domain)
    {
        Assert.Equal(new LogonIdentity(user, domain), LogonIdentity.ParseConfigured(configured, defaultDomain, upnDomain));
    }
}
