using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private void OnEnableDiscoveredFormatButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Content = ApplicationPolicyText("EnableDiscoveredFormat");
        }
    }

    private async void OnEnableDiscoveredFormatClicked(object sender, RoutedEventArgs e)
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
            if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                !ReferenceEquals(ApplicationPoliciesList.SelectedItem, selected))
            {
                return;
            }

            string[] standardFormatNames = StandardPolicyFormats()
                .Select(static format => format.Name)
                .ToArray();
            HashSet<string> standardFormatSet = standardFormatNames.ToHashSet(StringComparer.Ordinal);
            string[] eligibleFormatNames = discoveredFormats
                .Select(static format => format.FormatName)
                .Where(name =>
                    !standardFormatSet.Contains(name) &&
                    ClipboardCaptureFormatGuard.IsCaptureAllowed(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

            if (eligibleFormatNames.Length == 0)
            {
                ApplicationPoliciesInfo.Severity = InfoBarSeverity.Informational;
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("NoEligibleDiscoveredFormats");
                ApplicationPoliciesInfo.IsOpen = true;
                return;
            }

            DiscoveredCustomFormatCandidate[] candidates = await Task.Run(
                async () =>
                {
                    var repository = new SqliteCustomBinaryFormatConfigurationRepository(session);
                    var result = new List<DiscoveredCustomFormatCandidate>(eligibleFormatNames.Length);
                    foreach (string formatName in eligibleFormatNames)
                    {
                        session.CancellationToken.ThrowIfCancellationRequested();
                        string? extension = await repository.ReadFileExtensionAsync(
                                formatName,
                                session.CancellationToken)
                            .ConfigureAwait(false);
                        result.Add(new DiscoveredCustomFormatCandidate(formatName, extension));
                    }
                    return result.ToArray();
                },
                session.CancellationToken);
            if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                !ReferenceEquals(ApplicationPoliciesList.SelectedItem, selected))
            {
                return;
            }

            var error = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
            };
            var format = new ComboBox
            {
                Header = ApplicationPolicyText("DiscoveredFormatName"),
                PlaceholderText = ApplicationPolicyText("ChooseDiscoveredFormat"),
                ItemsSource = candidates,
                DisplayMemberPath = nameof(DiscoveredCustomFormatCandidate.FormatName),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = -1,
            };
            var extension = new TextBox
            {
                Header = PolicyText("CustomFileExtension"),
                PlaceholderText = PolicyText("CustomFileExtensionPlaceholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = false,
            };
            var mappingInfo = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            var maxBytes = new TextBox
            {
                Header = ApplicationPolicyText("CustomMaxBytes"),
                PlaceholderText = PolicyText("BytesPlaceholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            format.SelectionChanged += (_, _) =>
            {
                if (format.SelectedItem is not DiscoveredCustomFormatCandidate candidate)
                {
                    extension.Text = string.Empty;
                    extension.IsEnabled = false;
                    mappingInfo.Text = string.Empty;
                    mappingInfo.Visibility = Visibility.Collapsed;
                    return;
                }

                extension.Text = candidate.ExistingFileExtension ?? string.Empty;
                extension.IsEnabled = candidate.ExistingFileExtension is null;
                if (candidate.ExistingFileExtension is not null)
                {
                    mappingInfo.Text = ApplicationPolicyText("ExistingMapping")
                        .Replace("{0}", candidate.ExistingFileExtension, StringComparison.Ordinal);
                    mappingInfo.Visibility = Visibility.Visible;
                }
                else
                {
                    mappingInfo.Text = string.Empty;
                    mappingInfo.Visibility = Visibility.Collapsed;
                }
            };

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = ApplicationPolicyText("EnableDiscoveredFormatHelp"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(error);
            content.Children.Add(format);
            content.Children.Add(extension);
            content.Children.Add(mappingInfo);
            content.Children.Add(maxBytes);

            var dialog = new ContentDialog
            {
                Title = selected.DisplayName,
                PrimaryButtonText = ApplicationPolicyText("EnableDiscoveredFormatApply"),
                CloseButtonText = ApplicationPolicyText("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ShellNavigation.XamlRoot,
                Content = content,
            };

            ClipboardCapturePolicy? appliedPolicy = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!IsCurrentApplicationPolicyOperation(session, generation) ||
                        !ReferenceEquals(ApplicationPoliciesList.SelectedItem, selected))
                    {
                        error.Message = ApplicationPolicyText("SessionChanged");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    ApplicationDiscoveredCustomFormatEnableRequest request;
                    try
                    {
                        if (format.SelectedItem is not DiscoveredCustomFormatCandidate candidate)
                        {
                            throw new ArgumentException("A discovered custom format must be selected.");
                        }

                        string canonicalExtension = candidate.ExistingFileExtension ??
                            ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension(extension.Text);
                        request = ApplicationDiscoveredCustomFormatEnableSetup.Create(
                            currentPolicy?.Capture ?? ClipboardCapturePolicyRule.Inherit,
                            discoveredFormats.Select(static item => item.FormatName).ToArray(),
                            standardFormatNames,
                            candidate.FormatName,
                            canonicalExtension,
                            maxBytes.Text,
                            currentPolicy?.Formats);
                    }
                    catch (ArgumentException)
                    {
                        error.Message = ApplicationPolicyText("DiscoveredFormatValidationFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    var mapping = new ApplicationCustomBinaryFormatConfiguration(
                        request.FormatName,
                        request.FileExtension);
                    if (Application.Current is not App app ||
                        !await app.TryApplyApplicationCapturePolicyChangeAsync(
                            session,
                            selected.Summary.ApplicationId,
                            request.Policy,
                            [mapping],
                            session.CancellationToken))
                    {
                        error.Message = ApplicationPolicyText("ApplyFailed");
                        error.IsOpen = true;
                        args.Cancel = true;
                        return;
                    }

                    appliedPolicy = request.Policy;
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
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("DiscoveredFormatSaved");
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
                ApplicationPoliciesInfo.Message = ApplicationPolicyText("DiscoveredFormatPrepareFailed");
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

    private sealed record DiscoveredCustomFormatCandidate(
        string FormatName,
        string? ExistingFileExtension);
}
