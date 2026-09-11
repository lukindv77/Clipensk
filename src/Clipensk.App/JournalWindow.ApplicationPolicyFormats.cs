using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
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
            IReadOnlyList<ApplicationDiscoveredFormat> discoveredFormats =
                await ReadApplicationDiscoveredFormatsAsync(
                    session,
                    selected.Summary.ApplicationId);
            if (!IsCurrentApplicationPolicyOperation(session, generation))
            {
                return;
            }

            (string FormatName, string Label)[] standardFormats = StandardPolicyFormats().ToArray();
            var standardNames = standardFormats
                .Select(static format => format.FormatName)
                .ToHashSet(StringComparer.Ordinal);
            ApplicationDiscoveredFormat[] customDiscoveredFormats = discoveredFormats
                .Where(format => !standardNames.Contains(format.FormatName))
                .OrderBy(static format => format.FormatName, StringComparer.Ordinal)
                .ToArray();
            IReadOnlyDictionary<string, string?> existingMappings =
                await ReadApplicationCustomBinaryMappingsAsync(session, customDiscoveredFormats);
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
            foreach ((string formatName, string label) in standardFormats)
            {
                ClipboardFormatCapturePolicy stored = default;
                bool hasStored = currentPolicy is not null &&
                    currentPolicy.Formats.TryGetValue(formatName, out stored);

                var rule = CreateApplicationFormatRuleEditor(
                    label,
                    hasStored ? stored.Capture : ClipboardCapturePolicyRule.Inherit);
                var limit = CreateApplicationFormatLimitEditor(
                    label,
                    hasStored && stored.MaxBytes.HasValue);
                var bytes = CreateApplicationFormatBytesEditor(
                    label,
                    hasStored ? stored.MaxBytes : null);
                limit.SelectionChanged += (_, _) =>
                {
                    bytes.IsEnabled = (limit.SelectedItem as ApplicationLimitOption)?.OverrideMaxBytes == true;
                };

                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(rule);
                row.Children.Add(limit);
                row.Children.Add(bytes);
                content.Children.Add(row);
                editors.Add(new ApplicationFormatPolicyEditor(formatName, rule, limit, bytes, null));
            }

            if (customDiscoveredFormats.Length > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = PolicyText("CustomFormats"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            foreach (ApplicationDiscoveredFormat discovered in customDiscoveredFormats)
            {
                string formatName = discovered.FormatName;
                ClipboardFormatCapturePolicy stored = default;
                bool hasStored = currentPolicy is not null &&
                    currentPolicy.Formats.TryGetValue(formatName, out stored);
                existingMappings.TryGetValue(formatName, out string? existingExtension);

                var rule = CreateApplicationFormatRuleEditor(
                    formatName,
                    hasStored ? stored.Capture : ClipboardCapturePolicyRule.Inherit);
                var limit = CreateApplicationFormatLimitEditor(
                    formatName,
                    hasStored && stored.MaxBytes.HasValue);
                var bytes = CreateApplicationFormatBytesEditor(
                    formatName,
                    hasStored ? stored.MaxBytes : null);
                var extension = new TextBox
                {
                    Header = PolicyText("CustomFileExtension"),
                    PlaceholderText = PolicyText("CustomFileExtensionPlaceholder"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Text = existingExtension ?? string.Empty,
                    IsReadOnly = existingExtension is not null,
                    IsEnabled = hasStored && stored.Capture == ClipboardCapturePolicyRule.Allow,
                };
                AutomationProperties.SetName(
                    extension,
                    formatName + ": " + PolicyText("CustomFileExtension"));

                limit.SelectionChanged += (_, _) =>
                {
                    bytes.IsEnabled = (limit.SelectedItem as ApplicationLimitOption)?.OverrideMaxBytes == true;
                };
                rule.SelectionChanged += (_, _) =>
                {
                    extension.IsEnabled =
                        (rule.SelectedItem as ApplicationRuleOption)?.Rule == ClipboardCapturePolicyRule.Allow;
                };

                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(rule);
                row.Children.Add(limit);
                row.Children.Add(bytes);
                row.Children.Add(extension);
                content.Children.Add(row);
                editors.Add(new ApplicationFormatPolicyEditor(formatName, rule, limit, bytes, extension));
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
                    ApplicationCustomBinaryFormatConfiguration[] customMappings;
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

                        customMappings = editors
                            .Where(static editor => editor.Extension is not null)
                            .Where(editor =>
                                (editor.Rule.SelectedItem as ApplicationRuleOption)?.Rule ==
                                ClipboardCapturePolicyRule.Allow)
                            .Select(editor =>
                            {
                                if (!ClipboardCaptureFormatGuard.IsCaptureAllowed(editor.FormatName))
                                {
                                    throw new ArgumentException("This clipboard format cannot be captured.");
                                }

                                string normalizedExtension = ExternalPayloadAddressFactory
                                    .NormalizeCustomBinaryExtension(editor.Extension!.Text);
                                return new ApplicationCustomBinaryFormatConfiguration(
                                    editor.FormatName,
                                    normalizedExtension);
                            })
                            .ToArray();
                    }
                    catch (ArgumentException)
                    {
                        error.Message = ApplicationPolicyText("ValidationFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    if (Application.Current is not App app)
                    {
                        error.Message = ApplicationPolicyText("ApplyFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    bool applied = customDiscoveredFormats.Length == 0
                        ? await app.TryApplyApplicationCapturePolicyChangeAsync(
                            session,
                            selected.Summary.ApplicationId,
                            requested,
                            session.CancellationToken)
                        : await app.TryApplyApplicationCapturePolicyAndCustomMappingsChangeAsync(
                            session,
                            selected.Summary.ApplicationId,
                            requested,
                            customMappings,
                            session.CancellationToken);
                    if (!applied)
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

    private async Task<IReadOnlyDictionary<string, string?>> ReadApplicationCustomBinaryMappingsAsync(
        ProtectedStorageSessionLease session,
        IReadOnlyList<ApplicationDiscoveredFormat> formats)
    {
        return await Task.Run(
            async () =>
            {
                var repository = new SqliteCustomBinaryFormatConfigurationRepository(session);
                var mappings = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (ApplicationDiscoveredFormat format in formats)
                {
                    mappings.Add(
                        format.FormatName,
                        await repository.ReadFileExtensionAsync(
                                format.FormatName,
                                session.CancellationToken)
                            .ConfigureAwait(false));
                }

                return (IReadOnlyDictionary<string, string?>)mappings;
            },
            session.CancellationToken);
    }

    private ComboBox CreateApplicationFormatRuleEditor(
        string label,
        ClipboardCapturePolicyRule storedRule)
    {
        var rule = new ComboBox
        {
            Header = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(ApplicationRuleOption.Label),
            ItemsSource = ApplicationFormatRuleOptions(),
            SelectedIndex = RuleIndex(storedRule),
        };
        AutomationProperties.SetName(rule, label + ": " + PolicyText("ChooseRule"));
        return rule;
    }

    private ComboBox CreateApplicationFormatLimitEditor(string label, bool overrideMaxBytes)
    {
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
            SelectedIndex = overrideMaxBytes ? 1 : 0,
        };
        AutomationProperties.SetName(limit, label + ": " + PolicyText("LimitMode"));
        return limit;
    }

    private TextBox CreateApplicationFormatBytesEditor(string label, long? maxBytes)
    {
        var bytes = new TextBox
        {
            Header = PolicyText("Bytes"),
            PlaceholderText = PolicyText("BytesPlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Text = maxBytes.HasValue
                ? maxBytes.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            IsEnabled = maxBytes.HasValue,
        };
        AutomationProperties.SetName(bytes, label + ": " + PolicyText("Bytes"));
        return bytes;
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
        TextBox Bytes,
        TextBox? Extension);
}
