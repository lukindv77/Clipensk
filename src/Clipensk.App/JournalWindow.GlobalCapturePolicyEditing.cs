using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private bool _policyEditInProgress;

    private async void OnEditGlobalPolicyClicked(object sender, RoutedEventArgs e)
    {
        ProtectedStorageSessionLease? session = _policyViewSession;
        long generation = Volatile.Read(ref _policyUiGeneration);
        if (_policyEditInProgress ||
            session is null ||
            _policyViewState != PolicyViewState.Configured ||
            !IsCurrentPolicyOperation(session, generation))
        {
            return;
        }

        _policyEditInProgress = true;
        EditGlobalPolicyButton.IsEnabled = false;
        try
        {
            ClipboardCapturePolicy? currentPolicy = await Task.Run(
                async () => await new SqliteGlobalClipboardCapturePolicyRepository(session)
                    .ReadAsync(session.CancellationToken),
                session.CancellationToken);
            if (currentPolicy is null || !IsCurrentPolicyOperation(session, generation))
            {
                ShowPolicyEditError("Не удалось перечитать текущие правила перед изменением.");
                return;
            }

            PolicyEditDialogControls controls = BuildPolicyEditDialogControls(currentPolicy);
            var dialog = new ContentDialog
            {
                Title = "Изменить правила сбора",
                PrimaryButtonText = "Применить изменения",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = controls.Content,
            };

            ClipboardCapturePolicy? appliedPolicy = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!IsCurrentPolicyOperation(session, generation))
                    {
                        controls.Error.IsOpen = true;
                        controls.Error.Message = "Защищённая сессия изменилась. Закройте диалог и повторите после разблокировки.";
                        args.Cancel = true;
                        return;
                    }

                    ClipboardCapturePolicy requestedPolicy;
                    try
                    {
                        requestedPolicy = CreateEditedGlobalPolicy(currentPolicy, controls);
                    }
                    catch (ArgumentException)
                    {
                        controls.Error.IsOpen = true;
                        controls.Error.Message = PolicyText("ValidationFailed");
                        args.Cancel = true;
                        return;
                    }
                    catch (InvalidDataException)
                    {
                        controls.Error.IsOpen = true;
                        controls.Error.Message = "Сохранённые правила имеют неподдерживаемое состояние. Перечитайте их перед изменением.";
                        args.Cancel = true;
                        return;
                    }

                    if (Application.Current is not App app ||
                        !await app.TryApplyGlobalCapturePolicyChangeAsync(
                            session,
                            requestedPolicy,
                            session.CancellationToken))
                    {
                        controls.Error.IsOpen = true;
                        controls.Error.Message =
                            "Изменение не завершено. Если durable-операция уже началась, сбор оставлен безопасно приостановленным и будет продолжен при следующей разблокировке.";
                        args.Cancel = true;
                        return;
                    }

                    appliedPolicy = requestedPolicy;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary &&
                appliedPolicy is not null &&
                IsCurrentPolicyOperation(session, generation))
            {
                await LoadGlobalCapturePolicyAsync(reload: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Lock/close owns cancellation. The protected view will be cleared by lifecycle code.
        }
        catch
        {
            if (IsCurrentPolicyOperation(session, generation))
            {
                ShowPolicyEditError("Не удалось подготовить изменение правил. Перечитайте текущие правила и повторите попытку.");
            }
        }
        finally
        {
            _policyEditInProgress = false;
            if (!_policyWindowClosed && _policyViewState == PolicyViewState.Configured)
            {
                EditGlobalPolicyButton.IsEnabled = true;
            }
        }
    }

    private PolicyEditDialogControls BuildPolicyEditDialogControls(
        ClipboardCapturePolicy currentPolicy)
    {
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(new TextBlock
        {
            Text = "Изменяются базовое правило и стандартные форматы. Пользовательские бинарные форматы и их file-extension mappings сохраняются без изменений.",
            TextWrapping = TextWrapping.Wrap,
        });

        var error = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
        };
        root.Children.Add(error);

        ComboBox globalRule = CreatePolicyRuleEditor(PolicyText("BaseRule"));
        SelectRule(globalRule, currentPolicy.Capture);
        root.Children.Add(globalRule);

        root.Children.Add(new TextBlock
        {
            Text = PolicyText("Formats"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });

        var editors = new List<PolicyFormatEditor>();
        foreach ((string name, string label) in StandardPolicyFormats())
        {
            ComboBox rule = CreatePolicyRuleEditor(label);
            ComboBox limit = CreatePolicyLimitEditor(label);
            TextBox bytes = CreatePolicyBytesEditor(label);
            var editor = new PolicyFormatEditor(name, rule, limit, bytes);
            rule.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);
            limit.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);

            if (currentPolicy.Formats.TryGetValue(name, out ClipboardFormatCapturePolicy? currentFormat))
            {
                PopulateStandardPolicyEditor(editor, currentFormat);
            }

            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(rule);
            row.Children.Add(limit);
            row.Children.Add(bytes);
            root.Children.Add(row);
            editors.Add(editor);
        }

        var scroll = new ScrollViewer
        {
            Content = root,
            MaxHeight = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        return new PolicyEditDialogControls(scroll, error, globalRule, editors);
    }

    private ClipboardCapturePolicy CreateEditedGlobalPolicy(
        ClipboardCapturePolicy currentPolicy,
        PolicyEditDialogControls controls)
    {
        var standardSetups = controls.StandardEditors
            .Select(editor => new GlobalClipboardFormatSetup(
                editor.FormatName,
                (editor.Rule.SelectedItem as PolicyRuleOption)?.Rule,
                (editor.Limit.SelectedItem as PolicyLimitOption)?.Enabled,
                editor.Bytes.Text))
            .ToArray();

        ClipboardCapturePolicy editedStandardPolicy = GlobalClipboardCapturePolicySetup.Create(
            (controls.GlobalRule.SelectedItem as PolicyRuleOption)?.Rule,
            standardSetups);

        HashSet<string> standardNames = StandardPolicyFormats()
            .Select(format => format.Name)
            .ToHashSet(StringComparer.Ordinal);
        var combinedFormats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);

        foreach ((string name, ClipboardFormatCapturePolicy format) in currentPolicy.Formats)
        {
            if (!standardNames.Contains(name))
            {
                combinedFormats.Add(name, format);
            }
        }

        foreach ((string name, ClipboardFormatCapturePolicy format) in editedStandardPolicy.Formats)
        {
            combinedFormats.Add(name, format);
        }

        return new ClipboardCapturePolicy(editedStandardPolicy.Capture, combinedFormats);
    }

    private static void PopulateStandardPolicyEditor(
        PolicyFormatEditor editor,
        ClipboardFormatCapturePolicy format)
    {
        SelectRule(editor.Rule, format.Capture);
        if (format.Capture == ClipboardCapturePolicyRule.Allow)
        {
            editor.Limit.SelectedIndex = format.MaxBytes.HasValue ? 0 : 1;
            editor.Bytes.Text = format.MaxBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        else
        {
            editor.Limit.SelectedIndex = -1;
            editor.Bytes.Text = string.Empty;
        }

        UpdateLimitAvailability(editor);
    }

    private static void SelectRule(ComboBox editor, ClipboardCapturePolicyRule rule)
    {
        editor.SelectedIndex = rule switch
        {
            ClipboardCapturePolicyRule.Allow => 0,
            ClipboardCapturePolicyRule.Deny => 1,
            _ => throw new InvalidDataException("Stored global policy contains a non-explicit rule."),
        };
    }

    private void ShowPolicyEditError(string message)
    {
        GlobalPolicyInfo.Severity = InfoBarSeverity.Error;
        GlobalPolicyInfo.Message = message;
        GlobalPolicyInfo.IsOpen = true;
    }

    private sealed record PolicyEditDialogControls(
        ScrollViewer Content,
        InfoBar Error,
        ComboBox GlobalRule,
        IReadOnlyList<PolicyFormatEditor> StandardEditors);
}
