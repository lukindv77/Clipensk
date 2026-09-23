using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Xunit;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class ApplicationGroupModelTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly ClipboardCapturePolicy Global = new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
        });

    [Theory]
    [InlineData("  Браузеры  ", "Браузеры", "БРАУЗЕРЫ")]
    [InlineData("Office", "Office", "OFFICE")]
    public void Name_IsTrimmedAndKeyedWithoutCase(string text, string value, string key)
    {
        ApplicationGroupName name = ApplicationGroupName.Create(text);

        Assert.Equal(value, name.Value);
        Assert.Equal(key, name.Key);
        Assert.Equal(ApplicationGroupName.Create("браузеры").Key, ApplicationGroupName.Create("БРАУЗЕРЫ").Key);
    }

    [Theory]
    [InlineData(null, ApplicationGroupNameError.Empty)]
    [InlineData("   ", ApplicationGroupNameError.Empty)]
    [InlineData("a\tb", ApplicationGroupNameError.ControlCharacter)]
    [InlineData("line\nbreak", ApplicationGroupNameError.ControlCharacter)]
    public void Name_RejectsEmptyAndControlCharacters(string? text, ApplicationGroupNameError expected)
    {
        Assert.False(ApplicationGroupName.TryCreate(text, out ApplicationGroupName? name, out ApplicationGroupNameError error));
        Assert.Null(name);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Name_RejectsMoreThanTheMaximumLength()
    {
        Assert.True(ApplicationGroupName.TryCreate(new string('a', ApplicationGroupName.MaxLength), out _, out _));
        Assert.False(ApplicationGroupName.TryCreate(
            new string('a', ApplicationGroupName.MaxLength + 1),
            out _,
            out ApplicationGroupNameError error));
        Assert.Equal(ApplicationGroupNameError.TooLong, error);
    }

    [Fact]
    public void UniqueNameFromApplication_AddsASuffixCleansAndFitsTheLimit()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "CHROME.EXE", "CHROME.EXE (2)" };

        Assert.Equal("chrome.exe (3)", ApplicationGroupName.CreateUniqueFromApplicationName("chrome.exe", taken.Contains).Value);
        Assert.Equal("a b", ApplicationGroupName.CreateUniqueFromApplicationName(" a\u0001 b ", taken.Contains).Value);

        string longName = new('x', 150);
        var longTaken = new HashSet<string>(StringComparer.Ordinal) { new('X', ApplicationGroupName.MaxLength) };
        ApplicationGroupName fitted = ApplicationGroupName.CreateUniqueFromApplicationName(longName, longTaken.Contains);
        Assert.Equal(ApplicationGroupName.MaxLength, fitted.Value.Length);
        Assert.EndsWith(" (2)", fitted.Value, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ApplicationGroupName.CreateUniqueFromApplicationName("\u0001", taken.Contains));
    }

    [Fact]
    public void Group_RequiresAStandaloneExplicitPolicyAndUtcTime()
    {
        var inherit = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Inherit);
        var inheritFormat = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Inherit),
            });

        Assert.Throws<ArgumentException>(() => Group("A", inherit));
        Assert.Throws<ArgumentException>(() => Group("A", inheritFormat));
        Assert.Throws<ArgumentException>(() => new ApplicationGroup(
            ApplicationGroupId.New(),
            ApplicationGroupName.Create("A"),
            Global,
            new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.FromHours(3))));
        Assert.Same(Global, Group("A", Global).Policy);
    }

    [Fact]
    public void Directory_ResolvesGroupsMembersAndEffectivePolicy()
    {
        var groupPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        ApplicationGroup office = Group("Office", groupPolicy);
        ApplicationGroup browsers = Group("Browsers", Global);
        var first = new ApplicationId(new Guid("20000000-0000-0000-0000-000000000002"));
        var second = new ApplicationId(new Guid("10000000-0000-0000-0000-000000000001"));
        var unassigned = ApplicationId.New();

        var directory = new ApplicationGroupDirectory(
            [office, browsers],
            [Assign(first, office), Assign(second, office)]);

        Assert.Equal([browsers, office], directory.Groups);
        Assert.Same(office, directory.GroupOf(first));
        Assert.Null(directory.GroupOf(unassigned));
        Assert.True(directory.IsInDefaultGroup(unassigned));
        Assert.Equal([second, first], directory.MembersOf(office.GroupId));
        Assert.Empty(directory.MembersOf(browsers.GroupId));
        Assert.Same(groupPolicy, directory.EffectivePolicy(first, Global));
        Assert.Same(Global, directory.EffectivePolicy(unassigned, Global));
        Assert.True(directory.IsNameTaken(ApplicationGroupName.Create("OFFICE")));
        Assert.False(directory.IsNameTaken(ApplicationGroupName.Create("office"), office.GroupId));
        Assert.Throws<ArgumentException>(() => directory.MembersOf(ApplicationGroupId.New()));
    }

    [Fact]
    public void Directory_FailsClosedOnInconsistentSnapshots()
    {
        ApplicationGroup office = Group("Office", Global);
        var application = ApplicationId.New();

        Assert.Throws<InvalidDataException>(() => new ApplicationGroupDirectory([office, office], []));
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupDirectory([office, Group("OFFICE", Global)], []));
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupDirectory([], [Assign(application, office)]));
        Assert.Throws<InvalidDataException>(() => new ApplicationGroupDirectory(
            [office, Group("Other", Global)],
            [Assign(application, office), Assign(application, office)]));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", null, "app.exe")]
    [InlineData(null, "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]
    public void DisplayName_PrefersTheExecutableFileNameThenTheAumid(string? path, string? aumid, string expected)
    {
        var summary = new ApplicationIdentitySummary(
            ApplicationId.New(),
            Created,
            aumid is null ? [] : [aumid],
            path is null ? [] : [path]);

        Assert.Equal(expected, ApplicationDisplayName.From(summary));
    }

    [Fact]
    public void DisplayName_FallsBackToTheApplicationId()
    {
        var id = ApplicationId.New();

        Assert.Equal(id.ToString(), ApplicationDisplayName.From(new ApplicationIdentitySummary(id, Created, [], [])));
    }

    private static ApplicationGroup Group(string name, ClipboardCapturePolicy policy) =>
        new(ApplicationGroupId.New(), ApplicationGroupName.Create(name), policy, Created);

    private static ApplicationGroupAssignment Assign(ApplicationId application, ApplicationGroup group) =>
        new(application, group.GroupId, Created);
}
