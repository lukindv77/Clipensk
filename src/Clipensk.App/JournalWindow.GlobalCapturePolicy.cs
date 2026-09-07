using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private enum PolicyViewState { Locked, Loading, Unconfigured, Saving, Configured, Failed }
    private PolicyViewState _policyViewState = PolicyViewState.Locked;
    private ProtectedStorageSessionLease? _policyViewSession;
    private long _policyUiGeneration;
    private bool _policyWindowClosed;
    private readonly List<PolicyFormatEditor> _policyFormatEditors = new();
    private readonly List<CustomPolicyFormatEditor> _customPolicyFormatEditors = new();
    private StackPanel? _customPolicyFormatRowsHost;

    private void InitializeGlobalPolicyUi()
    {
        GlobalPolicyTitle.Text = PolicyText("Title");
        GlobalPolicyBody.Text = PolicyText("Body");
        GlobalPolicyRule.Header = PolicyText("BaseRule");
        GlobalPolicyRule.PlaceholderText = PolicyText("ChooseRule");
        GlobalPolicyRule.ItemsSource = RuleOptions();
        GlobalPolicyRule.DisplayMemberPath = nameof(PolicyRuleOption.Label);
        GlobalPolicyFormatsTitle.Text = PolicyText("Formats");
        GlobalPolicyLimitsHelp.Text = PolicyText("LimitsHelp");
        GlobalPolicyExternalHelp.Text = PolicyText("ExternalHelp");
        GlobalPolicySetupHelp.Text = PolicyText("SetupHelp");
        SaveGlobalPolicyButton.Content = PolicyText("Save");
        ReloadGlobalPolicyButton.Content = PolicyText("Reload");
        OpenGlobalPolicyButton.Content = PolicyText("Open");
        SetGlobalPolicyState(PolicyViewState.Locked);
    }

    private string PolicyText(string key) => _localization.GetString("CapturePolicy." + key);

    private PolicyRuleOption[] RuleOptions() =>
    [
        new(PolicyText("Allow"), ClipboardCapturePolicyRule.Allow),
        new(PolicyText("Deny"), ClipboardCapturePolicyRule.Deny),
    ];

    private (string Name, string Label)[] StandardPolicyFormats() =>
    [
        (StandardDataFormats.Text, PolicyText("Format.Text")),
        (StandardDataFormats.Html, PolicyText("Format.Html")),
        (StandardDataFormats.Rtf, PolicyText("Format.Rtf")),
        (StandardDataFormats.Bitmap, PolicyText("Format.Bitmap")),
        (StandardDataFormats.WebLink, PolicyText("Format.WebLink")),
        (StandardDataFormats.ApplicationLink, PolicyText("Format.ApplicationLink")),
        (StandardDataFormats.StorageItems, PolicyText("Format.StorageItems")),
    ];

    private void BuildGlobalPolicyEditors()
    {
        GlobalPolicyRule.SelectedIndex = -1;
        _policyFormatEditors.Clear();
        _customPolicyFormatEditors.Clear();
        _customPolicyFormatRowsHost = null;
        GlobalPolicyFormatRows.Children.Clear();
        foreach ((string name, string label) in StandardPolicyFormats())
        {
            var rule = CreatePolicyRuleEditor(label);
            var limit = CreatePolicyLimitEditor(label);
            var bytes = CreatePolicyBytesEditor(label);
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(rule);
            row.Children.Add(limit);
            row.Children.Add(bytes);
            var editor = new PolicyFormatEditor(name, rule, limit, bytes);
            rule.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);
            limit.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);
            _policyFormatEditors.Add(editor);
            GlobalPolicyFormatRows.Children.Add(row);
        }

        GlobalPolicyFormatRows.Children.Add(new TextBlock
        {
            Text = PolicyText("CustomFormats"),
            TextWrapping = TextWrapping.Wrap,
        });
        GlobalPolicyFormatRows.Children.Add(new TextBlock
        {
            Text = PolicyText("CustomFormatsHelp"),
            TextWrapping = TextWrapping.Wrap,
        });
        _customPolicyFormatRowsHost = new StackPanel { Spacing = 16 };
        GlobalPolicyFormatRows.Children.Add(_customPolicyFormatRowsHost);
        var addCustomFormatButton = new Button
        {
            Content = PolicyText("AddCustomFormat"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addCustomFormatButton.Click += (_, _) => AddCustomPolicyFormatEditor();
        GlobalPolicyFormatRows.Children.Add(addCustomFormatButton);
    }

    private ComboBox CreatePolicyRuleEditor(string label)
    {
        var rule = new ComboBox
        {
            Header = label,
            PlaceholderText = PolicyText("ChooseRule"),
            ItemsSource = RuleOptions(),
            DisplayMemberPath = nameof(PolicyRuleOption.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = -1,
        };
        AutomationProperties.SetName(rule, label + ": " + PolicyText("ChooseRule"));
        return rule;
    }

    private ComboBox CreatePolicyLimitEditor(string label)
    {
        var limit = new ComboBox
        {
            Header = PolicyText("LimitMode"),
            PlaceholderText = PolicyText("ChooseLimit"),
            ItemsSource = new[]
            {
                new PolicyLimitOption(PolicyText("Limited"), true),
                new PolicyLimitOption(PolicyText("Unlimited"), false),
            },
            DisplayMemberPath = nameof(PolicyLimitOption.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = -1,
            IsEnabled = false,
        };
        AutomationProperties.SetName(limit, label + ": " + PolicyText("LimitMode"));
        return limit;
    }

    private TextBox CreatePolicyBytesEditor(string label)
    {
        var bytes = new TextBox
        {
            Header = PolicyText("Bytes"),
            PlaceholderText = PolicyText("BytesPlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        AutomationProperties.SetName(bytes, label + ": " + PolicyText("Bytes"));
        return bytes;
    }

    private void AddCustomPolicyFormatEditor()
    {
        StackPanel? host = _customPolicyFormatRowsHost;
        if (host is null || _policyViewState != PolicyViewState.Unconfigured)
        {
            return;
        }

        var formatName = new TextBox
        {
            Header = PolicyText("CustomFormatName"),
            PlaceholderText = PolicyText("CustomFormatNamePlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var extension = new TextBox
        {
            Header = PolicyText("CustomFileExtension"),
            PlaceholderText = PolicyText("CustomFileExtensionPlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        var rule = CreatePolicyRuleEditor(PolicyText("CustomRule"));
        var limit = CreatePolicyLimitEditor(PolicyText("CustomRule"));
        var bytes = CreatePolicyBytesEditor(PolicyText("CustomRule"));
        var remove = new Button
        {
            Content = PolicyText("RemoveCustomFormat"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(formatName);
        row.Children.Add(extension);
        row.Children.Add(rule);
        row.Children.Add(limit);
        row.Children.Add(bytes);
        row.Children.Add(remove);

        var editor = new CustomPolicyFormatEditor(formatName, extension, rule, limit, bytes, row);
        rule.SelectionChanged += (_, _) => UpdateCustomEditorAvailability(editor);
        limit.SelectionChanged += (_, _) => UpdateCustomEditorAvailability(editor);
        remove.Click += (_, _) =>
        {
            if (_policyViewState != PolicyViewState.Unconfigured)
            {
                return;
            }
            _customPolicyFormatEditors.Remove(editor);
            host.Children.Remove(row);
        };
        _customPolicyFormatEditors.Add(editor);
        host.Children.Add(row);
    }

    private static void UpdateLimitAvailability(PolicyFormatEditor editor)
    {
        bool allowed = (editor.Rule.SelectedItem as PolicyRuleOption)?.Rule == ClipboardCapturePolicyRule.Allow;
        editor.Limit.IsEnabled = allowed;
        editor.Bytes.IsEnabled = allowed && (editor.Limit.SelectedItem as PolicyLimitOption)?.Enabled == true;
    }

    private static void UpdateCustomEditorAvailability(CustomPolicyFormatEditor editor)
    {
        bool allowed = (editor.Rule.SelectedItem as PolicyRuleOption)?.Rule == ClipboardCapturePolicyRule.Allow;
        editor.FileExtension.IsEnabled = allowed;
        editor.Limit.IsEnabled = allowed;
        editor.Bytes.IsEnabled = allowed && (editor.Limit.SelectedItem as PolicyLimitOption)?.Enabled == true;
    }

    private async Task LoadGlobalCapturePolicyAsync(bool reload = false)
    {
        if (_policyWindowClosed || !TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? session) || session is null)
        {
            return;
        }
        if (ReferenceEquals(_policyViewSession, session) &&
            (_policyViewState is PolicyViewState.Loading or PolicyViewState.Saving ||
             (!reload && (_policyViewState is PolicyViewState.Unconfigured or PolicyViewState.Configured))))
        {
            return;
        }

        long generation = Interlocked.Increment(ref _policyUiGeneration);
        ClearGlobalPolicyContents();
        _policyViewSession = session;
        SetGlobalPolicyState(PolicyViewState.Loading);
        try
        {
            HashSet<string> standardNames = StandardPolicyFormats()
                .Select(format => format.Name)
                .ToHashSet(StringComparer.Ordinal);
            var result = await Task.Run(async () =>
            {
                var repository = new SqliteGlobalClipboardCapturePolicyRepository(session);
                ClipboardCapturePolicy? policy = await repository.ReadAsync(session.CancellationToken);
                var extensions = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (policy is not null)
                {
                    var customRepository = new SqliteCustomBinaryFormatConfigurationRepository(session);
                    foreach ((string name, ClipboardFormatCapturePolicy format) in policy.Formats)
                    {
                        session.CancellationToken.ThrowIfCancellationRequested();
                        if (!standardNames.Contains(name) && format.Capture == ClipboardCapturePolicyRule.Allow)
                        {
                            extensions[name] = await customRepository.ReadFileExtensionAsync(
                                name,
                                session.CancellationToken);
                        }
                    }
                }
                return (Policy: policy, Extensions: extensions);
            }, session.CancellationToken);
            if (!IsCurrentPolicyOperation(session, generation)) return;
            if (result.Policy is null)
            {
                BuildGlobalPolicyEditors();
                SetGlobalPolicyState(PolicyViewState.Unconfigured);
            }
            else
            {
                DisplayStoredGlobalPolicy(result.Policy, result.Extensions);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentPolicyOperation(session, generation)) SetGlobalPolicyState(PolicyViewState.Failed);
        }
        catch (Exception)
        {
            if (IsCurrentPolicyOperation(session, generation)) SetGlobalPolicyState(PolicyViewState.Failed);
        }
    }

    private async void OnSaveGlobalPolicyClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _policyViewSession;
        long generation = Volatile.Read(ref _policyUiGeneration);
        if (session is null || _policyViewState != PolicyViewState.Unconfigured ||
            !IsCurrentPolicyOperation(session, generation)) return;

        ClipboardCapturePolicy policy;
        List<InitialCustomBinaryFormatConfiguration> customConfigurations;
        try
        {
            var formatSetups = _policyFormatEditors.Select(editor => new GlobalClipboardFormatSetup(
                editor.FormatName,
                (editor.Rule.SelectedItem as PolicyRuleOption)?.Rule,
                (editor.Limit.SelectedItem as PolicyLimitOption)?.Enabled,
                editor.Bytes.Text)).ToList();
            customConfigurations = new List<InitialCustomBinaryFormatConfiguration>(_customPolicyFormatEditors.Count);
            foreach (CustomPolicyFormatEditor editor in _customPolicyFormatEditors)
            {
                string formatName = editor.FormatName.Text;
                if (!ClipboardCaptureFormatGuard.IsCaptureAllowed(formatName))
                {
                    throw new ArgumentException("The custom clipboard format is prohibited from capture.");
                }

                ClipboardCapturePolicyRule? rule = (editor.Rule.SelectedItem as PolicyRuleOption)?.Rule;
                formatSetups.Add(new GlobalClipboardFormatSetup(
                    formatName,
                    rule,
                    (editor.Limit.SelectedItem as PolicyLimitOption)?.Enabled,
                    editor.Bytes.Text));
                if (rule == ClipboardCapturePolicyRule.Allow)
                {
                    string extension = ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension(
                        editor.FileExtension.Text);
                    customConfigurations.Add(new InitialCustomBinaryFormatConfiguration(formatName, extension));
                }
            }

            policy = GlobalClipboardCapturePolicySetup.Create(
                (GlobalPolicyRule.SelectedItem as PolicyRuleOption)?.Rule,
                formatSetups);
        }
        catch (ArgumentException)
        {
            GlobalPolicyInfo.Severity = InfoBarSeverity.Error;
            GlobalPolicyInfo.Message = PolicyText("ValidationFailed");
            return;
        }

        SetGlobalPolicyState(PolicyViewState.Saving);
        try
        {
            var service = new SqliteInitialClipboardCaptureConfigurationService(session);
            await Task.Run(
                async () => await service.InitializeAsync(
                    policy,
                    customConfigurations,
                    session.CancellationToken),
                session.CancellationToken);
            if (IsCurrentPolicyOperation(session, generation))
            {
                var storedExtensions = customConfigurations.ToDictionary(
                    configuration => configuration.FormatName,
                    configuration => (string?)configuration.FileExtension,
                    StringComparer.Ordinal);
                DisplayStoredGlobalPolicy(policy, storedExtensions);
                NotifyGlobalCapturePolicyInitialized();
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentPolicyOperation(session, generation)) SetGlobalPolicyState(PolicyViewState.Failed);
        }
        catch (Exception)
        {
            if (IsCurrentPolicyOperation(session, generation))
            {
                SetGlobalPolicyState(PolicyViewState.Failed);
                GlobalPolicyInfo.Message = PolicyText("SaveFailed");
            }
        }
    }

    private bool IsCurrentPolicyOperation(ProtectedStorageSessionLease session, long generation) =>
        !_policyWindowClosed && generation == Volatile.Read(ref _policyUiGeneration) &&
        ReferenceEquals(session, _policyViewSession) && ReferenceEquals(session, _protectedStorageSession) &&
        session.IsActive && _lifecycle.CanAccessProtectedData;

    private void DisplayStoredGlobalPolicy(
        ClipboardCapturePolicy policy,
        IReadOnlyDictionary<string, string?>? customExtensions = null)
    {
        ClearGlobalPolicyContents();
        var labels = StandardPolicyFormats().ToDictionary(format => format.Name, format => format.Label, StringComparer.Ordinal);
        var lines = new List<string>
        {
            PolicyText("BaseRule") + ": " + PolicyText(policy.Capture == ClipboardCapturePolicyRule.Allow ? "Allow" : "Deny"),
        };
        foreach ((string name, ClipboardFormatCapturePolicy format) in policy.Formats.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            bool standard = labels.TryGetValue(name, out string? friendly);
            string label = standard ? friendly! : name;
            string limit = format.MaxBytes.HasValue
                ? format.MaxBytes.Value.ToString(CultureInfo.InvariantCulture) + " " + PolicyText("ByteUnit")
                : PolicyText("Unlimited");
            string line = label + ": " + PolicyText(format.Capture == ClipboardCapturePolicyRule.Allow ? "Allow" : "Deny") + "; " + limit;
            if (!standard && format.Capture == ClipboardCapturePolicyRule.Allow)
            {
                string? extension = null;
                customExtensions?.TryGetValue(name, out extension);
                line += "; " + PolicyText("CustomFileExtension") + ": " +
                    (extension ?? PolicyText("CustomExtensionMissing"));
            }
            lines.Add(line);
        }
        GlobalPolicySummary.Text = string.Join(Environment.NewLine, lines);
        SetGlobalPolicyState(PolicyViewState.Configured);
    }

    private void SetGlobalPolicyState(PolicyViewState state)
    {
        _policyViewState = state;
        bool busy = state is PolicyViewState.Loading or PolicyViewState.Saving;
        GlobalPolicyProgress.IsActive = busy;
        GlobalPolicyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GlobalPolicyEditor.Visibility = state is PolicyViewState.Unconfigured or PolicyViewState.Saving ? Visibility.Visible : Visibility.Collapsed;
        GlobalPolicyEditor.IsEnabled = state == PolicyViewState.Unconfigured;
        SaveGlobalPolicyButton.IsEnabled = state == PolicyViewState.Unconfigured;
        ReloadGlobalPolicyButton.IsEnabled = !busy && state != PolicyViewState.Locked;
        GlobalPolicySummary.Visibility = state == PolicyViewState.Configured ? Visibility.Visible : Visibility.Collapsed;
        GlobalPolicyInfo.Severity = state == PolicyViewState.Failed ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        GlobalPolicyInfo.Message = PolicyText("State." + state);
        JournalPolicyStatus.Text = PolicyText("State." + state);
    }

    private void ClearGlobalPolicyContents()
    {
        GlobalPolicyRule.SelectedIndex = -1;
        foreach (PolicyFormatEditor editor in _policyFormatEditors)
        {
            editor.Rule.SelectedIndex = -1;
            editor.Limit.SelectedIndex = -1;
            editor.Bytes.Text = string.Empty;
        }
        foreach (CustomPolicyFormatEditor editor in _customPolicyFormatEditors)
        {
            editor.Rule.SelectedIndex = -1;
            editor.Limit.SelectedIndex = -1;
            editor.Bytes.Text = string.Empty;
            editor.FormatName.Text = string.Empty;
            editor.FileExtension.Text = string.Empty;
        }
        _policyFormatEditors.Clear();
        _customPolicyFormatEditors.Clear();
        _customPolicyFormatRowsHost = null;
        GlobalPolicyFormatRows.Children.Clear();
        GlobalPolicySummary.Text = string.Empty;
    }

    private void ClearGlobalPolicyUi()
    {
        _policyViewSession = null;
        ClearGlobalPolicyContents();
        SetGlobalPolicyState(PolicyViewState.Locked);
    }

    private void OnGlobalPolicyProtectedAccessChanged(bool allowed)
    {
        if (allowed) return;
        long generation = Interlocked.Increment(ref _policyUiGeneration);
        void ClearRevokedView()
        {
            if (_policyWindowClosed || generation != Volatile.Read(ref _policyUiGeneration)) return;
            ClearGlobalPolicyUi();
            GlobalPolicyPanel.Visibility = Visibility.Collapsed;
            RefreshLifecycleUi();
        }
        if (DispatcherQueue.HasThreadAccess) ClearRevokedView();
        else DispatcherQueue.TryEnqueue(ClearRevokedView);
    }

    private void OnOpenGlobalPolicyClicked(object sender, RoutedEventArgs e)
    {
        if (!_lifecycle.CanAccessProtectedData) return;
        ShellNavigation.SelectedItem = ApplicationsItem;
        ShowPage("applications");
    }

    private async void OnReloadGlobalPolicyClicked(object sender, RoutedEventArgs e) =>
        await LoadGlobalCapturePolicyAsync(reload: true);

    private sealed record PolicyRuleOption(string Label, ClipboardCapturePolicyRule Rule);
    private sealed record PolicyLimitOption(string Label, bool Enabled);
    private sealed record PolicyFormatEditor(string FormatName, ComboBox Rule, ComboBox Limit, TextBox Bytes);
    private sealed record CustomPolicyFormatEditor(
        TextBox FormatName,
        TextBox FileExtension,
        ComboBox Rule,
        ComboBox Limit,
        TextBox Bytes,
        StackPanel Row);
}
