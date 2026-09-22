using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ApplicationHistoryPurgeStateCodecTests
{
    private const string Root = "10000000-0000-0000-0000-000000000001";
    private const string Child = "20000000-0000-0000-0000-000000000002";
    private static readonly string Fingerprint = new('A', 64);

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        ApplicationHistoryPurgeState state = ApplicationHistoryPurgeStateCodec.CreateStarted(
            ApplicationHistoryPurgeReason.Merge,
            Root,
            Child,
            [Child, Root],
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Allow, ["Text", "HTML Format"]),
            rootPolicyFingerprint: null) with
        {
            Archive = ApplicationHistoryPurgeState.Completed,
        };

        ApplicationHistoryPurgeState parsed =
            ApplicationHistoryPurgeStateCodec.Parse(ApplicationHistoryPurgeStateCodec.Serialize(state));

        Assert.Equal(ApplicationHistoryPurgeReason.Merge, parsed.Reason);
        Assert.Equal(Root, parsed.RootApplicationId);
        Assert.Equal(Child, parsed.ChildApplicationId);
        Assert.Equal([Root, Child], parsed.SourceApplicationIds);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, parsed.EffectiveCapture);
        Assert.Equal(["HTML Format", "Text"], parsed.AllowedFormats);
        Assert.Null(parsed.RootPolicyFingerprint);
        Assert.Equal(ApplicationHistoryPurgeState.Completed, parsed.Archive);
        Assert.Equal(ApplicationHistoryPurgeState.Pending, parsed.Catalog);
        Assert.True(parsed.Rule.Retains("Text"));
        Assert.False(parsed.Rule.Retains("text"));
    }

    [Theory]
    [InlineData("""{"version":2,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":null,"archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["20000000-0000-0000-0000-000000000002"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Inherit","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":["Text","HTML Format"]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"completed","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"Merge","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":null,"archive":"pending","catalog":"pending","trash":"pending"}""")]
    [InlineData("""{"version":1,"reason":"FirstAssignment","rootApplicationId":"10000000-0000-0000-0000-000000000001","childApplicationId":null,"sourceApplicationIds":["10000000-0000-0000-0000-000000000001"],"effectivePolicy":{"capture":"Allow","allowedFormats":[]},"rootPolicyFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","archive":"pending","catalog":"pending","trash":"pending","extra":1}""")]
    [InlineData("not json")]
    public void Parse_RejectsMalformedOrInconsistentState(string json)
    {
        Assert.Throws<InvalidDataException>(() => ApplicationHistoryPurgeStateCodec.Parse(json));
    }

    [Fact]
    public void CreateStarted_RejectsAFirstAssignmentWithoutARecordedPolicy()
    {
        Assert.Throws<InvalidDataException>(() => ApplicationHistoryPurgeStateCodec.CreateStarted(
            ApplicationHistoryPurgeReason.FirstAssignment,
            Root,
            childApplicationId: null,
            [Root],
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Deny, []),
            rootPolicyFingerprint: null));

        ApplicationHistoryPurgeState valid = ApplicationHistoryPurgeStateCodec.CreateStarted(
            ApplicationHistoryPurgeReason.FirstAssignment,
            Root,
            childApplicationId: null,
            [Root],
            new ClipboardHistoryPurgeRule(ClipboardCapturePolicyRule.Deny, []),
            Fingerprint);
        Assert.False(valid.IsFullyCompleted);
    }
}
