using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ApplicationHistoryPurgeStateCodecTests
{
    private const string Application = "10000000-0000-0000-0000-000000000001";
    private const string Group = "20000000-0000-0000-0000-000000000002";
    private static readonly string Fingerprint = new('A', 64);

    private const string ValidJson =
        """{"version":2,"applicationId":"10000000-0000-0000-0000-000000000001","groupId":"20000000-0000-0000-0000-000000000002","createdGroup":true,"effectivePolicy":{"capture":"Allow","allowedFormats":["Text"]},"groupPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""";

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        ApplicationHistoryPurgeState state = ApplicationHistoryPurgeStateCodec.CreateStarted(
            Application,
            Group,
            createdGroup: true,
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Allow, ["Text", "HTML Format"]),
            Fingerprint) with
        {
            Archive = ApplicationHistoryPurgeState.Completed,
        };

        ApplicationHistoryPurgeState parsed =
            ApplicationHistoryPurgeStateCodec.Parse(ApplicationHistoryPurgeStateCodec.Serialize(state));

        Assert.Equal(Application, parsed.ApplicationId);
        Assert.Equal(Group, parsed.GroupId);
        Assert.True(parsed.CreatedGroup);
        Assert.Equal([Application], parsed.SourceApplicationIds);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, parsed.EffectiveCapture);
        Assert.Equal(["HTML Format", "Text"], parsed.AllowedFormats);
        Assert.Equal(Fingerprint, parsed.GroupPolicyFingerprint);
        Assert.Equal(ApplicationHistoryPurgeState.Completed, parsed.Archive);
        Assert.Equal(ApplicationHistoryPurgeState.Pending, parsed.Catalog);
        Assert.True(parsed.Rule.Retains("Text"));
        Assert.False(parsed.Rule.Retains("text"));
    }

    [Fact]
    public void Parse_AcceptsTheCanonicalShape()
    {
        ApplicationHistoryPurgeState parsed = ApplicationHistoryPurgeStateCodec.Parse(ValidJson);

        Assert.False(parsed.IsFullyCompleted);
        Assert.Equal(ValidJson, ApplicationHistoryPurgeStateCodec.Serialize(parsed));
    }

    [Theory]
    [InlineData("\"version\":2", "\"version\":1")]
    [InlineData("\"version\":2", "\"version\":3")]
    [InlineData("\"createdGroup\":true", "\"createdGroup\":1")]
    [InlineData("10000000-0000-0000-0000-000000000001", "10000000-0000-0000-0000-00000000000A")]
    [InlineData("20000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000000")]
    [InlineData("\"capture\":\"Allow\"", "\"capture\":\"Inherit\"")]
    [InlineData("[\"Text\"]", "[\"Text\",\"HTML Format\"]")]
    [InlineData("[\"Text\"]", "[\"Text\",\"Text\"]")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("\"catalog\":\"pending\"", "\"catalog\":\"completed\"")]
    [InlineData("\"trash\":\"pending\"", "\"trash\":\"done\"")]
    [InlineData("\"trash\":\"pending\"}", "\"trash\":\"pending\",\"extra\":1}")]
    [InlineData("\"groupId\":\"20000000-0000-0000-0000-000000000002\",", "")]
    public void Parse_RejectsMalformedOrInconsistentState(string original, string replacement)
    {
        string json = ValidJson.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(ValidJson, json);

        Assert.Throws<InvalidDataException>(() => ApplicationHistoryPurgeStateCodec.Parse(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void Parse_RejectsNonObjects(string json)
    {
        Assert.Throws<InvalidDataException>(() => ApplicationHistoryPurgeStateCodec.Parse(json));
    }

    [Fact]
    public void Parse_RejectsTheVersionOneRootBasedState()
    {
        const string VersionOne =
            """{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""";

        Assert.Throws<InvalidDataException>(() => ApplicationHistoryPurgeStateCodec.Parse(VersionOne));
    }
}
