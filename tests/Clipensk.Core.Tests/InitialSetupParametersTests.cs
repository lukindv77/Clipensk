using Clipensk.Core.Input;
using Clipensk.Core.Settings;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class InitialSetupParametersTests
{
    private static readonly HotKeyGesture CtrlShiftV = new(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x56);

    [Fact]
    public void ValidChoices_AreAppliedAndCompleteTheSetup()
    {
        var parameters = new InitialSetupParameters(CtrlShiftV, true, true, 10, 14);
        var settings = new ApplicationSettings { DataRootPath = @"D:\Clipensk", PasswordHint = "hint" };

        ApplicationSettings applied = parameters.ApplyTo(settings);

        Assert.Null(parameters.FindProblem());
        Assert.Equal(CtrlShiftV, applied.JournalHotKey);
        Assert.True(applied.AutostartEnabled);
        Assert.True(applied.AutoLockEnabled);
        Assert.Equal(10, applied.AutoLockAfterMinutes);
        Assert.Equal(14, applied.DefaultJournalPeriodDays);
        Assert.True(applied.InitialSetupCompleted);
        Assert.Equal(@"D:\Clipensk", applied.DataRootPath);
        Assert.Equal("hint", applied.PasswordHint);
    }

    [Fact]
    public void AutoLockOffWithoutMinutes_AndNoJournalPeriod_AreValid()
    {
        Assert.Null(new InitialSetupParameters(CtrlShiftV, false, false, null, null).FindProblem());
    }

    [Fact]
    public void MissingHotKeyOrModifier_IsRejected()
    {
        Assert.Equal("Setup.Parameters.HotKeyRequired", new InitialSetupParameters(null, false, false, null, 30).FindProblem());
        Assert.Equal(
            "Setup.Parameters.HotKeyRequired",
            new InitialSetupParameters(new HotKeyGesture(HotKeyModifiers.None, 0x56), false, false, null, 30).FindProblem());
        Assert.Throws<InvalidOperationException>(() =>
            new InitialSetupParameters(null, false, false, null, 30).ApplyTo(new ApplicationSettings()));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, 0)]
    [InlineData(false, -1)]
    public void AutoLockWithoutPositiveMinutes_IsRejected(bool enabled, int? minutes)
    {
        Assert.Equal("Settings.Lock.Invalid", new InitialSetupParameters(CtrlShiftV, false, enabled, minutes, 30).FindProblem());
    }

    [Fact]
    public void NonPositiveJournalPeriod_IsRejected()
    {
        Assert.Equal("Settings.JournalPeriod.Invalid", new InitialSetupParameters(CtrlShiftV, false, false, null, 0).FindProblem());
    }
}
