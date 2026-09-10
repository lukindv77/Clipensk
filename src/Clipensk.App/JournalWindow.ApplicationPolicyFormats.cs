using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnEditApplicationFormatsButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Content = ApplicationPolicyText("EditFormats");
        }
    }

    private async void OnEditApplicationFormatsClicked(object sender, RoutedEventArgs e)
    {
        if (_applicationPolicyEditInProgress ||
            ApplicationPoliciesList.SelectedItem is not ApplicationPolicyListItem selected)
        {
            return;
        }

        ProtectedStorageSessionLease? session = _protectedStorageSession;
        long generation = Volatile.Read(ref _applicationPoliciesGeneration);
        if (session is null || !IsCurrentApplicationPolicyOperation(session, generation))
        {
            return;
        }

        _applicationPolicyEditInProgress = true;
        EditApplicationPolicyButton.IsEnabled = false;
        try
        {
            ClipboardCapturePolicy? currentPolicy = await ReadApplicationPolicyAsync(
                session,
                selected.Summary.ApplicationId);
            if (!IsCurrentApplicationPolicyOperation(session, generation))
            {
                return;
            }

            var error = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
            };
            var content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 680,
            };
            content.Children.Add(new TextBlock
            {
                Text = ApplicationPolicyText("FormatsHelp"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(error);

            var editors = new List<ApplicationFormatPolicyEditor>();
            foreach ((string formatName, string label) in StandardPolicyFormats())
            {
                ClipboardFormatCapturePolicy stored = default;
                bool hasStored = currentPolicy is not null &&
                    currentPolicy.Formats.TryGetValue(formatName, out stored);

                var rule = new ComboBox
                {
                    Header = label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    DisplayMemberPath = nameof(ApplicationRuleOption.Label),
                    ItemsSource = ApplicationFormatRuleOptions(),
                    SelectedIndex = RuleIndex(hasStored ? stored.Capture : ClipboardCapturePolicyRule.Inherit),
                };
                AutomationProperties.SetName(rule, label + ": " + PolicyText("ChooseRule"));

                var limit = new ComboBox
                {
                    Header = PolicyText("LimitMode"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    DisplayMemberPath = nameof(ApplicationLimitOption.Label),
                    ItemsSource = new[]
                    {
                        new ApplicationLimitOption(ApplicationPolicyText("InheritLimit"), false),
                        new ApplicationLimitOption(ApplicationPolicyText("OverrideLimit"), true),
                    },
                    SelectedIndex = hasStored && stored.MaxBytes.HasValue ? 1 : 0,
                };
                AutomationProperties.SetName(limit, label + ": " + PolicyText("LimitMode"));

                var bytes = new TextBox
                {
                    Header = PolicyText("Bytes"),
                    PlaceholderText = PolicyText("BytesPlaceholder"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Text = hasStored && stored.MaxBytes.HasValue
                        ? stored.MaxBytes.Value.ToString(CultureInfo.InvariantCulture)
                        : string.Empty,
                    IsEnabled = hasStored && stored.MaxBytes.HasValue,
                };
                AutomationProperties.SetName(bytes, label + ": " + PolicyText("Bytes"));
                limit.SelectionChanged += (_, _) =>
                {
                    bytes.IsEnabled = (limit.SelectedItem as ApplicationLimitOption)?.OverrideMaxBytes == true;
                };

                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(rule);
                row.Children.Add(limit);
                row.Children.Add(bytes);
                content.Children.Add(row);
                editors.Add(new ApplicationFormatPolicyEditor(formatName, rule, limit, bytes));
            }

            var dialog = new ContentDialog
            {
                Title = selected.DisplayName,
                PrimaryButtonText = ApplicationPolicyText("Apply"),
                CloseButtonText = ApplicationPolicyText("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = content,
                },
            };

            ClipboardCapturePolicy? appliedPolicy = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!IsCurrentApplicationPolicyOperation(session, generation))
                    {
                        error.Message = ApplicationPolicyText("SessionChanged");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    ClipboardCapturePolicy requested;
                    try
                    {
                        ApplicationClipboardFormatSetup[] formats = editors
                            .Select(editor => new ApplicationClipboardFormatSetup(
                                editor.FormatName,
                                (editor.Rule.SelectedItem as ApplicationRuleOption)?.Rule,
                                (editor.Limit.SelectedItem as ApplicationLimitOption)?.OverrideMaxBytes == true,
                                editor.Bytes.Text))
                            .ToArray();
                        requested = ApplicationClipboardCapturePolicySetup.Create(
                            currentPolicy?.Capture ?? ClipboardCapturePolicyRule.Inherit,
                            formats,
                            currentPolicy?.Formats);
                    }
                    catch (ArgumentException)
                    {
                        error.Message = ApplicationPolicyText("ValidationFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    if (Application.Current is not App app ||
                        !await app.TryApplyApplicationCapturePolicyChangeAsync(
                            session,
                            selected.Summary.ApplicationId,
                            requested,
                            session.CancellationToken))
                    {
                        error.Message = ApplicationPolicyText("ApplyFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    appliedPolicy = requested;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            _applicationPolicyDialog = dialog;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                if (ReferenceEquals(_applicationPolicyDialog, dialog))
                {
                    _applicationPolicyDialog = null;
                }
            }

            if (result == ContentDialogResult.Primary &&
                appliedPolicy is not null &&
                IsCurrentApplicationPolicyOperation(session, generation))
            {
                SelectedApplicationPolicySummary.Text = BuildApplicationPolicySummary(appliedPolicy);
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Success;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("FormatSaved");
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (IsCurrentApplicationPolicyOperation(session, generation))
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Error;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("FormatPrepareFailed");
                ApplicationPoliciesInfo.IsOpen = true;
            }
        }
        finally
        {
            _applicationPolicyEditInProgress = false;
            if (session is not null &&
                IsCurrentApplicationPolicyOperation(session, generation) &&
                ApplicationPoliciesList.SelectedItem is ApplicationPolicyListItem)
            {
                EditApplicationPolicyButton.IsEnabled = true;
            }
        }
    }

    private ApplicationRuleOption[] ApplicationFormatRuleOptions() =>
    [
        new(ApplicationPolicyText("Inherit"), ClipboardCapturePolicyRule.Inherit),
        new(PolicyText("Allow"), ClipboardCapturePolicyRule.Allow),
        new(PolicyText("Deny"), ClipboardCapturePolicyRule.Deny),
    ];

    private static int RuleIndex(ClipboardCapturePolicyRule rule) => rule switch
    {
        ClipboardCapturePolicyRule.Allow => 1,
        ClipboardCapturePolicyRule.Deny => 2,
        _ => 0,
    };

    private sealed record ApplicationLimitOption(string Label, bool OverrideMaxBytes);

    private sealed record ApplicationFormatPolicyEditor(
        string FormatName,
        ComboBox Rule,
        ComboBox Limit,
        TextBox Bytes);
}
