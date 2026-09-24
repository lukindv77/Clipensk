using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.App;

/// <summary>
/// Choosing an application's group and editing a group's settings, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> v2: a move always shows a preview of what the target
/// group's rules would delete and runs only after explicit confirmation; editing a group's settings
/// only affects future capture of all its applications.
/// </summary>
public sealed partial class JournalWindow
{
    private const int GroupRecordsPreviewLimit = 50;

    private async void OnChangeApplicationGroupClicked(object sender, RoutedEventArgs e)
    {
        if (ApplicationPoliciesList.SelectedItem is not ApplicationPolicyListItem selected)
        {
            return;
        }

        await RunApplicationPageGroupChangeAsync(
            (session, isCurrent) => RunApplicationGroupMoveDialogAsync(
                session,
                selected.Summary.ApplicationId,
                selected.ApplicationName,
                isCurrent),
            ApplicationPoliciesInfo);
    }

    private async void OnEditApplicationGroupSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (ApplicationPoliciesList.SelectedItem is not ApplicationPolicyListItem { Group: { } group })
        {
            return;
        }

        await RunApplicationGroupSettingsAsync(group.GroupId, ApplicationPoliciesInfo);
    }

    private Task RunApplicationGroupSettingsAsync(ApplicationGroupId groupId, InfoBar resultBar) =>
        RunApplicationPageGroupChangeAsync(
            (session, isCurrent) => RunGroupSettingsDialogAsync(session, groupId, isCurrent),
            resultBar);

    /// <summary>
    /// Runs one group change started from the Applications page: at most one at a time, with the
    /// page's buttons disabled, then rereads the page and reports the result in
    /// <paramref name="resultBar"/>.
    /// </summary>
    private async Task RunApplicationPageGroupChangeAsync(
        Func<ProtectedStorageSessionLease, Func<bool>, Task<GroupChangeResult?>> change,
        InfoBar resultBar)
    {
        if (_applicationPolicyEditInProgress)
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
        SetApplicationGroupButtons(null);
        SetApplicationGroupManagementButtons(null);
        GroupChangeResult? result = null;
        try
        {
            result = await change(session, () => IsCurrentApplicationPolicyOperation(session, generation));
        }
        finally
        {
            _applicationPolicyEditInProgress = false;
            await FinishApplicationGroupChangeAsync(session, generation, result, resultBar);
        }
    }

    /// <summary>
    /// Shows the choose/move dialog for one application: a new group or an existing one, then the
    /// preview of what the target group's rules would delete, then the confirmed move. Returns what
    /// to report, or <see langword="null"/> when the user cancelled. <paramref name="isCurrent"/>
    /// tells whether the protected session the dialog was opened for is still the active one.
    /// </summary>
    private async Task<GroupChangeResult?> RunApplicationGroupMoveDialogAsync(
        ProtectedStorageSessionLease session,
        ApplicationId applicationId,
        string applicationName,
        Func<bool> isCurrent)
    {
        string? resultMessage = null;
        InfoBarSeverity resultSeverity = InfoBarSeverity.Success;
        try
        {
            GroupDialogData data = await ReadGroupDialogDataAsync(session, _ => [applicationId]);
            if (!isCurrent())
            {
                return null;
            }

            ApplicationGroup? sourceGroup = data.Groups.GroupOf(applicationId);
            GroupOption[] targets = data.Groups.Groups
                .Where(group => sourceGroup is null || group.GroupId != sourceGroup.GroupId)
                .Select(group => new GroupOption(group.Name.Value, group))
                .ToArray();

            var chooseError = CreateGroupErrorBar();
            var mode = new RadioButtons();
            mode.Items.Add(ApplicationGroupText("CreateNew"));
            mode.Items.Add(ApplicationGroupText("MoveExisting"));
            mode.SelectedIndex = 0;

            var name = new TextBox
            {
                Header = ApplicationGroupText("NameHeader"),
                PlaceholderText = ApplicationGroupText("NamePlaceholder"),
                MaxLength = ApplicationGroupName.MaxLength,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var useApplicationName = new Button { Content = ApplicationGroupText("UseApplicationName") };
            useApplicationName.Click += (_, _) =>
            {
                name.Text = applicationName;
                name.Focus(FocusState.Programmatic);
            };
            GroupPolicyEditor editor = BuildGroupPolicyEditor(data.Global, data.DiscoveredFormats, data.Mappings);
            var newPanel = new StackPanel { Spacing = 12 };
            newPanel.Children.Add(name);
            newPanel.Children.Add(useApplicationName);
            newPanel.Children.Add(new TextBlock
            {
                Text = ApplicationGroupText("NewGroupPolicyHelp"),
                TextWrapping = TextWrapping.Wrap,
            });
            newPanel.Children.Add(editor.Root);

            var existing = new ComboBox
            {
                Header = ApplicationGroupText("ExistingGroupHeader"),
                ItemsSource = targets,
                DisplayMemberPath = nameof(GroupOption.Label),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var existingSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
            existing.SelectionChanged += (_, _) =>
            {
                existingSummary.Text = existing.SelectedItem is GroupOption option
                    ? BuildGroupRulesSummary(option.Group.Policy)
                    : string.Empty;
            };
            var existingPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
            if (targets.Length == 0)
            {
                existingPanel.Children.Add(new TextBlock
                {
                    Text = ApplicationGroupText("NoOtherGroups"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                existingPanel.Children.Add(existing);
                existingPanel.Children.Add(existingSummary);
            }

            mode.SelectionChanged += (_, _) =>
            {
                newPanel.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                existingPanel.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            };

            var chooseContent = new StackPanel { Spacing = 12, MaxWidth = 680 };
            chooseContent.Children.Add(new TextBlock
            {
                Text = ApplicationGroupText("MoveHelp"),
                TextWrapping = TextWrapping.Wrap,
            });
            chooseContent.Children.Add(chooseError);
            chooseContent.Children.Add(mode);
            chooseContent.Children.Add(newPanel);
            chooseContent.Children.Add(existingPanel);

            var scroll = new ScrollViewer
            {
                Content = chooseContent,
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var dialog = new ContentDialog
            {
                Title = FillText(ApplicationGroupText("MoveTitle"), applicationName),
                PrimaryButtonText = ApplicationGroupText("Next"),
                CloseButtonText = ApplicationGroupText("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = scroll,
            };

            ApplicationGroupMoveRequest? request = null;
            ApplicationGroupMovePreview? preview = null;
            MoveConfirmationControls? confirmation = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                InfoBar currentError = confirmation?.Error ?? chooseError;
                try
                {
                    if (!isCurrent())
                    {
                        ShowGroupError(currentError, ApplicationGroupText("SessionChanged"));
                        args.Cancel = true;
                        return;
                    }

                    if (request is null || preview is null)
                    {
                        ApplicationGroupMoveRequest? built = BuildMoveRequest(
                            applicationId,
                            mode,
                            name,
                            editor,
                            existing,
                            data.Groups,
                            chooseError);
                        ApplicationGroupMovePreview? computed = built is null
                            ? null
                            : await TryPreviewMoveAsync(session, built, chooseError);
                        if (built is not null && computed is not null &&
                            isCurrent())
                        {
                            request = built;
                            preview = computed;
                            confirmation = ShowMoveConfirmation(session, dialog, scroll, computed, sourceGroup, notice: null);
                        }

                        args.Cancel = true;
                        return;
                    }

                    bool deleteSourceGroup = false;
                    if (preview.SourceGroupBecomesEmpty)
                    {
                        if (confirmation?.SourceGroupFate is not { SelectedIndex: >= 0 } fate)
                        {
                            ShowGroupError(
                                currentError,
                                FillText(
                                    ApplicationGroupText("SourceGroupChoiceRequired"),
                                    sourceGroup?.Name.Value ?? string.Empty));
                            args.Cancel = true;
                            return;
                        }
                        deleteSourceGroup = fate.SelectedIndex == 1;
                    }

                    if (Application.Current is not App app)
                    {
                        ShowGroupError(currentError, ApplicationGroupText("MoveFailed"));
                        args.Cancel = true;
                        return;
                    }

                    ApplicationGroupMoveOutcome outcome = await app.TryMoveApplicationToGroupAsync(
                        session,
                        request with { DeleteEmptiedSourceGroup = deleteSourceGroup },
                        preview,
                        session.CancellationToken);
                    switch (outcome.Kind)
                    {
                        case ApplicationGroupMoveOutcomeKind.Completed:
                            resultMessage = FillText(
                                ApplicationGroupText("Moved"),
                                preview.TargetGroupName.Value,
                                outcome.Result!.TotalSummary.DeletedRepresentations,
                                outcome.Result.TotalSummary.DeletedRecords);
                            resultSeverity = InfoBarSeverity.Success;
                            return;
                        case ApplicationGroupMoveOutcomeKind.Incomplete:
                            resultMessage = FillText(
                                ApplicationGroupText("MovedIncomplete"),
                                preview.TargetGroupName.Value);
                            resultSeverity = InfoBarSeverity.Warning;
                            return;
                        case ApplicationGroupMoveOutcomeKind.PreviewOutdated:
                            ApplicationGroupMovePreview? refreshed = await TryPreviewMoveAsync(session, request, currentError);
                            if (refreshed is not null && isCurrent())
                            {
                                preview = refreshed;
                                confirmation = ShowMoveConfirmation(
                                    session,
                                    dialog,
                                    scroll,
                                    refreshed,
                                    sourceGroup,
                                    ApplicationGroupText("PreviewOutdated"));
                            }
                            args.Cancel = true;
                            return;
                        case ApplicationGroupMoveOutcomeKind.NameTaken:
                            ShowGroupError(currentError, ApplicationGroupText("NameTaken"));
                            args.Cancel = true;
                            return;
                        default:
                            ShowGroupError(currentError, ApplicationGroupText("MoveFailed"));
                            args.Cancel = true;
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    args.Cancel = true;
                }
                catch
                {
                    ShowGroupError(currentError, ApplicationGroupText("PrepareFailed"));
                    args.Cancel = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await ShowApplicationGroupDialogAsync(dialog);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            resultMessage = ApplicationGroupText("PrepareFailed");
            resultSeverity = InfoBarSeverity.Error;
        }

        return resultMessage is null ? null : new GroupChangeResult(resultMessage, resultSeverity);
    }

    /// <summary>
    /// Edits the standalone rules of one user group for all its members; saved history is kept.
    /// Returns what to report, or <see langword="null"/> when the user cancelled.
    /// </summary>
    private async Task<GroupChangeResult?> RunGroupSettingsDialogAsync(
        ProtectedStorageSessionLease session,
        ApplicationGroupId groupId,
        Func<bool> isCurrent)
    {
        string? resultMessage = null;
        InfoBarSeverity resultSeverity = InfoBarSeverity.Success;
        try
        {
            GroupDialogData data = await ReadGroupDialogDataAsync(
                session,
                groups => groups.FindGroup(groupId) is null ? [] : groups.MembersOf(groupId));
            if (!isCurrent())
            {
                return null;
            }

            if (data.Groups.FindGroup(groupId) is not { } group)
            {
                return new GroupChangeResult(ApplicationGroupText("GroupGone"), InfoBarSeverity.Warning);
            }

            var error = CreateGroupErrorBar();
            GroupPolicyEditor editor = BuildGroupPolicyEditor(group.Policy, data.DiscoveredFormats, data.Mappings);
            var content = new StackPanel { Spacing = 12, MaxWidth = 680 };
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Message = FillText(
                    ApplicationGroupText("SettingsScope"),
                    data.Groups.MembersOf(group.GroupId).Count),
            });
            content.Children.Add(error);
            content.Children.Add(editor.Root);

            var dialog = new ContentDialog
            {
                Title = FillText(ApplicationGroupText("SettingsTitle"), group.Name.Value),
                PrimaryButtonText = ApplicationGroupText("Apply"),
                CloseButtonText = ApplicationGroupText("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 620,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!isCurrent())
                    {
                        ShowGroupError(error, ApplicationGroupText("SessionChanged"));
                        args.Cancel = true;
                        return;
                    }

                    ClipboardCapturePolicy policy;
                    ApplicationCustomBinaryFormatConfiguration[] mappings;
                    try
                    {
                        (policy, mappings) = ReadGroupPolicyEditor(editor);
                    }
                    catch (ArgumentException)
                    {
                        ShowGroupError(error, ApplicationGroupText("ValidationFailed"));
                        args.Cancel = true;
                        return;
                    }

                    if (Application.Current is not App app ||
                        !await app.TryApplyGroupCapturePolicyChangeAsync(
                            session,
                            group.GroupId,
                            policy,
                            mappings,
                            session.CancellationToken))
                    {
                        ShowGroupError(error, ApplicationGroupText("SettingsFailed"));
                        args.Cancel = true;
                        return;
                    }

                    resultMessage = FillText(ApplicationGroupText("SettingsSaved"), group.Name.Value);
                    resultSeverity = InfoBarSeverity.Success;
                }
                catch (OperationCanceledException)
                {
                    args.Cancel = true;
                }
                catch
                {
                    ShowGroupError(error, ApplicationGroupText("SettingsFailed"));
                    args.Cancel = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await ShowApplicationGroupDialogAsync(dialog);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            resultMessage = ApplicationGroupText("PrepareFailed");
            resultSeverity = InfoBarSeverity.Error;
        }

        return resultMessage is null ? null : new GroupChangeResult(resultMessage, resultSeverity);
    }

    private async Task<ContentDialogResult> ShowApplicationGroupDialogAsync(ContentDialog dialog)
    {
        _applicationPolicyDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_applicationPolicyDialog, dialog))
            {
                _applicationPolicyDialog = null;
            }
        }
    }

    /// <summary>Rereads the page so groups and membership shown match storage, then reports the result.</summary>
    private async Task FinishApplicationGroupChangeAsync(
        ProtectedStorageSessionLease session,
        long generation,
        GroupChangeResult? result,
        InfoBar resultBar)
    {
        if (!IsCurrentApplicationPolicyOperation(session, generation))
        {
            return;
        }

        await LoadApplicationPoliciesAsync();
        if (result is not null && ReferenceEquals(session, _protectedStorageSession))
        {
            resultBar.Severity = result.Severity;
            resultBar.Message = result.Message;
            resultBar.IsOpen = true;
        }
    }

    private ApplicationGroupMoveRequest? BuildMoveRequest(
        ApplicationId applicationId,
        RadioButtons mode,
        TextBox name,
        GroupPolicyEditor editor,
        ComboBox existing,
        ApplicationGroupDirectory groups,
        InfoBar error)
    {
        if (mode.SelectedIndex == 1)
        {
            if (existing.SelectedItem is not GroupOption option)
            {
                ShowGroupError(error, ApplicationGroupText("GroupRequired"));
                return null;
            }

            return new ApplicationGroupMoveRequest(
                applicationId,
                new ExistingApplicationGroupTarget(option.Group.GroupId));
        }

        if (!ApplicationGroupName.TryCreate(name.Text, out ApplicationGroupName? groupName, out ApplicationGroupNameError nameError))
        {
            ShowGroupError(
                error,
                ApplicationGroupText(nameError == ApplicationGroupNameError.Empty ? "NameRequired" : "NameInvalid"));
            return null;
        }
        if (groups.IsNameTaken(groupName!))
        {
            ShowGroupError(error, ApplicationGroupText("NameTaken"));
            return null;
        }

        try
        {
            (ClipboardCapturePolicy policy, ApplicationCustomBinaryFormatConfiguration[] mappings) =
                ReadGroupPolicyEditor(editor);
            return new ApplicationGroupMoveRequest(
                applicationId,
                new NewApplicationGroupTarget(groupName!, policy, mappings));
        }
        catch (ArgumentException)
        {
            ShowGroupError(error, ApplicationGroupText("ValidationFailed"));
            return null;
        }
    }

    private async Task<ApplicationGroupMovePreview?> TryPreviewMoveAsync(
        ProtectedStorageSessionLease session,
        ApplicationGroupMoveRequest request,
        InfoBar error)
    {
        try
        {
            return await Task.Run(
                async () => await new ProtectedApplicationHistoryPurgePreviewService(session)
                    .PreviewMoveAsync(request, session.CancellationToken)
                    .ConfigureAwait(false),
                session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApplicationGroupNameTakenException)
        {
            ShowGroupError(error, ApplicationGroupText("NameTaken"));
        }
        catch (PendingPolicyMaintenanceException)
        {
            ShowGroupError(error, ApplicationGroupText("MaintenancePending"));
        }
        catch
        {
            ShowGroupError(error, ApplicationGroupText("PreviewFailed"));
        }

        return null;
    }

    /// <summary>
    /// Replaces the dialog content with the confirmation step: the totals the move would delete,
    /// per-format record lists on request and, when the move empties a user group, the keep/delete
    /// choice.
    /// </summary>
    private MoveConfirmationControls ShowMoveConfirmation(
        ProtectedStorageSessionLease session,
        ContentDialog dialog,
        ScrollViewer scroll,
        ApplicationGroupMovePreview preview,
        ApplicationGroup? sourceGroup,
        string? notice)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 680 };
        if (notice is not null)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Message = notice,
            });
        }

        var error = CreateGroupErrorBar();
        content.Children.Add(error);
        string targetName = preview.TargetGroupName.Value;
        if (preview.IsEmpty)
        {
            content.Children.Add(CreateWrappedText(FillText(ApplicationGroupText("PurgeNothing"), targetName)));
        }
        else
        {
            ClipboardHistoryPurgeSummary total = preview.TotalSummary;
            content.Children.Add(CreateWrappedText(FillText(
                ApplicationGroupText("PurgeSummary"),
                targetName,
                total.DeletedRepresentations,
                total.DeletedExternalReferences,
                total.DeletedRecords,
                total.TrimmedRecords)));
            content.Children.Add(CreateWrappedText(FillText(
                ApplicationGroupText("PurgeLocations"),
                preview.CurrentSummary.DeletedRepresentations,
                preview.ArchiveDatabaseCount,
                preview.ArchiveSummary.DeletedRepresentations)));
            content.Children.Add(CreateWrappedText(ApplicationGroupText("PurgeFiles")));

            var records = new StackPanel { Spacing = 4 };
            foreach ((string formatName, int count) in total.DeletedRepresentationsByFormat
                         .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new TextBlock
                {
                    Text = FillText(ApplicationGroupText("PurgeFormat"), formatName, count),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var show = new HyperlinkButton { Content = ApplicationGroupText("ShowRecords") };
                show.Click += async (_, _) => await ShowRecordsLosingAsync(session, preview, formatName, count, records);
                row.Children.Add(show);
                content.Children.Add(row);
            }
            content.Children.Add(records);
        }

        RadioButtons? fate = null;
        if (preview.SourceGroupBecomesEmpty && sourceGroup is not null)
        {
            fate = new RadioButtons
            {
                Header = FillText(ApplicationGroupText("SourceGroupEmpties"), sourceGroup.Name.Value),
            };
            fate.Items.Add(ApplicationGroupText("KeepSourceGroup"));
            fate.Items.Add(ApplicationGroupText("DeleteSourceGroup"));
            fate.SelectedIndex = -1;
            content.Children.Add(fate);
        }

        scroll.Content = content;
        dialog.PrimaryButtonText = ApplicationGroupText(preview.IsEmpty ? "ConfirmMove" : "ConfirmPurgeAndMove");
        return new MoveConfirmationControls(error, fate);
    }

    /// <summary>Lists the application's records that would lose <paramref name="formatName"/>.</summary>
    private async Task ShowRecordsLosingAsync(
        ProtectedStorageSessionLease session,
        ApplicationGroupMovePreview preview,
        string formatName,
        int count,
        StackPanel host)
    {
        host.Children.Clear();
        host.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        try
        {
            ClipboardHistoryFilter filter = preview.RecordsLosing(formatName);
            IReadOnlyList<UnifiedClipboardHistoryEntry> entries = await Task.Run(
                async () => await new ProtectedUnifiedClipboardHistoryRepository(session)
                    .ReadAsync(
                        new JournalDateRange(DateOnly.MinValue, DateOnly.MaxValue),
                        GroupRecordsPreviewLimit,
                        filter: filter,
                        cancellationToken: session.CancellationToken)
                    .ConfigureAwait(false),
                session.CancellationToken);

            host.Children.Clear();
            foreach (UnifiedClipboardHistoryEntry unified in entries)
            {
                ClipboardHistoryEntry entry = unified.Entry;
                host.Children.Add(CreateWrappedText(
                    entry.EventTime.Timestamp.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture) +
                    " — " +
                    BuildJournalPreview(entry)));
            }
            host.Children.Add(CreateWrappedText(FillText(
                ApplicationGroupText("RecordsShown"),
                entries.Count,
                count)));
        }
        catch (OperationCanceledException)
        {
            host.Children.Clear();
        }
        catch
        {
            host.Children.Clear();
            host.Children.Add(CreateWrappedText(ApplicationGroupText("RecordsFailed")));
        }
    }

    /// <summary>
    /// Builds an editor for a group's standalone policy from <paramref name="basePolicy"/>: the base
    /// rule and the standard formats as in the global policy editor, then every custom format the
    /// policy lists or the group's applications were seen using, with its external file extension.
    /// </summary>
    private GroupPolicyEditor BuildGroupPolicyEditor(
        ClipboardCapturePolicy basePolicy,
        IReadOnlyCollection<string> discoveredFormats,
        IReadOnlyDictionary<string, string?> mappings)
    {
        var root = new StackPanel { Spacing = 16 };
        ComboBox baseRule = CreatePolicyRuleEditor(PolicyText("BaseRule"));
        SelectRule(baseRule, basePolicy.Capture);
        root.Children.Add(baseRule);
        root.Children.Add(new TextBlock
        {
            Text = PolicyText("Formats"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });

        HashSet<string> standardNames = StandardPolicyFormats()
            .Select(static format => format.Name)
            .ToHashSet(StringComparer.Ordinal);
        var standard = new List<PolicyFormatEditor>();
        foreach ((string formatName, string label) in StandardPolicyFormats())
        {
            PolicyFormatEditor editor = CreateGroupFormatEditor(formatName, label, basePolicy, root);
            standard.Add(editor);
        }

        string[] customNames = basePolicy.Formats.Keys
            .Concat(discoveredFormats)
            .Where(formatName => !standardNames.Contains(formatName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var custom = new List<GroupCustomFormatEditor>();
        if (customNames.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = PolicyText("CustomFormats"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (string formatName in customNames)
        {
            mappings.TryGetValue(formatName, out string? mapped);
            var extension = new TextBox
            {
                Header = PolicyText("CustomFileExtension"),
                PlaceholderText = PolicyText("CustomFileExtensionPlaceholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Text = mapped ?? string.Empty,
                IsReadOnly = mapped is not null,
            };
            PolicyFormatEditor format = CreateGroupFormatEditor(formatName, formatName, basePolicy, root, extension);
            void UpdateExtension() =>
                extension.IsEnabled =
                    (format.Rule.SelectedItem as PolicyRuleOption)?.Rule == ClipboardCapturePolicyRule.Allow;
            format.Rule.SelectionChanged += (_, _) => UpdateExtension();
            UpdateExtension();
            custom.Add(new GroupCustomFormatEditor(format, extension));
        }

        return new GroupPolicyEditor(root, baseRule, standard, custom);
    }

    private PolicyFormatEditor CreateGroupFormatEditor(
        string formatName,
        string label,
        ClipboardCapturePolicy basePolicy,
        StackPanel host,
        UIElement? extension = null)
    {
        ComboBox rule = CreatePolicyFormatRuleEditor(label);
        ComboBox limit = CreatePolicyLimitEditor(label);
        TextBox bytes = CreatePolicyBytesEditor(label);
        var editor = new PolicyFormatEditor(formatName, rule, limit, bytes);
        rule.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);
        limit.SelectionChanged += (_, _) => UpdateLimitAvailability(editor);
        if (basePolicy.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy current))
        {
            PopulateStandardPolicyEditor(editor, current);
        }

        host.Children.Add(extension is null
            ? CreatePolicyFormatCard(label, rule, limit, bytes)
            : CreatePolicyFormatCard(label, rule, limit, bytes, extension));
        return editor;
    }

    /// <summary>
    /// Reads a standalone group policy from the editor. A custom format left without a rule stays
    /// unlisted, which capture treats as not allowed; an allowed custom format needs a valid external
    /// file extension. Invalid choices throw <see cref="ArgumentException"/>.
    /// </summary>
    private static (ClipboardCapturePolicy Policy, ApplicationCustomBinaryFormatConfiguration[] Mappings) ReadGroupPolicyEditor(
        GroupPolicyEditor editor)
    {
        var setups = editor.Standard
            .Select(static format => ReadFormatSetup(format))
            .ToList();
        var mappings = new List<ApplicationCustomBinaryFormatConfiguration>();
        foreach (GroupCustomFormatEditor custom in editor.Custom)
        {
            ClipboardCapturePolicyRule? rule = (custom.Format.Rule.SelectedItem as PolicyRuleOption)?.Rule;
            if (rule is null)
            {
                continue;
            }

            setups.Add(ReadFormatSetup(custom.Format));
            if (rule == ClipboardCapturePolicyRule.Allow)
            {
                if (!ClipboardCaptureFormatGuard.IsCaptureAllowed(custom.Format.FormatName))
                {
                    throw new ArgumentException("This clipboard format cannot be captured.");
                }

                mappings.Add(new ApplicationCustomBinaryFormatConfiguration(
                    custom.Format.FormatName,
                    ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension(custom.Extension.Text)));
            }
        }

        ClipboardCapturePolicy policy = GlobalClipboardCapturePolicySetup.Create(
            (editor.BaseRule.SelectedItem as PolicyRuleOption)?.Rule,
            setups);
        return (policy, mappings.ToArray());
    }

    private static GlobalClipboardFormatSetup ReadFormatSetup(PolicyFormatEditor format) =>
        new(
            format.FormatName,
            (format.Rule.SelectedItem as PolicyRuleOption)?.Rule,
            (format.Limit.SelectedItem as PolicyLimitOption)?.Enabled,
            format.Bytes.Text);

    /// <summary>
    /// Reads, in one pass off the UI thread, the global policy, the groups, the formats the chosen
    /// applications were seen using and the external file extensions already bound to custom formats.
    /// </summary>
    private async Task<GroupDialogData> ReadGroupDialogDataAsync(
        ProtectedStorageSessionLease session,
        Func<ApplicationGroupDirectory, IReadOnlyList<ApplicationId>> applicationsInScope)
    {
        HashSet<string> standardNames = StandardPolicyFormats()
            .Select(static format => format.Name)
            .ToHashSet(StringComparer.Ordinal);
        return await Task.Run(
            async () =>
            {
                CancellationToken token = session.CancellationToken;
                ClipboardCapturePolicy global = await new SqliteGlobalClipboardCapturePolicyRepository(session)
                        .ReadAsync(token)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException("Global capture policy is not configured.");
                ApplicationGroupDirectory groups = await new SqliteApplicationGroupRepository(session)
                    .ReadAsync(token)
                    .ConfigureAwait(false);

                var discovered = new SortedSet<string>(StringComparer.Ordinal);
                var formats = new SqliteApplicationDiscoveredFormatRepository(session);
                foreach (ApplicationId application in applicationsInScope(groups))
                {
                    foreach (ApplicationDiscoveredFormat format in await formats
                                 .ListAsync(application, token)
                                 .ConfigureAwait(false))
                    {
                        discovered.Add(format.FormatName);
                    }
                }

                IEnumerable<string> policyFormats = global.Formats.Keys
                    .Concat(groups.Groups.SelectMany(static group => group.Policy.Formats.Keys));
                var mappings = new Dictionary<string, string?>(StringComparer.Ordinal);
                var repository = new SqliteCustomBinaryFormatConfigurationRepository(session);
                foreach (string formatName in discovered.Concat(policyFormats).Distinct(StringComparer.Ordinal))
                {
                    if (!standardNames.Contains(formatName))
                    {
                        mappings[formatName] = await repository
                            .ReadFileExtensionAsync(formatName, token)
                            .ConfigureAwait(false);
                    }
                }

                return new GroupDialogData(global, groups, discovered.ToArray(), mappings);
            },
            session.CancellationToken);
    }

    private static InfoBar CreateGroupErrorBar() => new()
    {
        IsOpen = false,
        IsClosable = false,
        Severity = InfoBarSeverity.Error,
    };

    private static void ShowGroupError(InfoBar error, string message)
    {
        error.Message = message;
        error.IsOpen = true;
    }

    private static TextBlock CreateWrappedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    };

    private sealed record GroupChangeResult(string Message, InfoBarSeverity Severity);

    private sealed record GroupOption(string Label, ApplicationGroup Group);

    private sealed record GroupDialogData(
        ClipboardCapturePolicy Global,
        ApplicationGroupDirectory Groups,
        IReadOnlyList<string> DiscoveredFormats,
        IReadOnlyDictionary<string, string?> Mappings);

    private sealed record GroupPolicyEditor(
        StackPanel Root,
        ComboBox BaseRule,
        IReadOnlyList<PolicyFormatEditor> Standard,
        IReadOnlyList<GroupCustomFormatEditor> Custom);

    private sealed record GroupCustomFormatEditor(PolicyFormatEditor Format, TextBox Extension);

    private sealed record MoveConfirmationControls(InfoBar Error, RadioButtons? SourceGroupFate);
}
