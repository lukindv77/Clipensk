using Clipensk.Core.Applications;
using Clipensk.Core.Input;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Microsoft.UI.Xaml.Controls;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private ProtectedStorageSessionLease? _journalApplicationFilterSession;
    private InvocationApplication? _pendingJournalInvocationApplication;

    /// <summary>
    /// Remembers the application the journal's hotkey was invoked from, per
    /// <c>docs/REQUIREMENTS.md</c> §1. It is a hint only: the filter list resolves it to a durable
    /// <see cref="ApplicationId"/> read-only, and never creates a new identity merely because the
    /// journal was opened.
    /// </summary>
    internal void SetJournalInvocationApplicationHint(InvocationApplication? invocation) =>
        _pendingJournalInvocationApplication = invocation;

    private async Task EnsureJournalApplicationFilterLoadedAsync()
    {
        ProtectedStorageSessionLease? session = _protectedStorageSession;
        if (session is null || !session.IsActive || !_lifecycle.CanAccessProtectedData)
        {
            return;
        }

        if (ReferenceEquals(_journalApplicationFilterSession, session))
        {
            return;
        }

        InvocationApplication? invocation = _pendingJournalInvocationApplication;
        try
        {
            (IReadOnlyList<ApplicationIdentitySummary> identities, Guid? invocationApplicationId) =
                await Task.Run(
                    async () =>
                    {
                        var repository = new SqliteApplicationIdentityRepository(session);
                        IReadOnlyList<ApplicationIdentitySummary> list = await repository
                            .ListAsync(session.CancellationToken)
                            .ConfigureAwait(false);

                        Guid? resolvedInvocationId = null;
                        if (invocation is { } invocationValue)
                        {
                            var observation = new ApplicationIdentityObservation(
                                invocationValue.ApplicationUserModelId,
                                invocationValue.ExecutablePath);
                            if (observation.HasResolvableEvidence)
                            {
                                ApplicationIdentityAliasLookup lookup = await repository
                                    .FindAliasesAsync(observation, session.CancellationToken)
                                    .ConfigureAwait(false);
                                resolvedInvocationId = (lookup.ApplicationUserModelIdApplicationId
                                    ?? lookup.ExecutablePathApplicationId)?.Value;
                            }
                        }

                        return (list, resolvedInvocationId);
                    },
                    session.CancellationToken);

            if (!ReferenceEquals(_protectedStorageSession, session) ||
                !session.IsActive ||
                !_lifecycle.CanAccessProtectedData)
            {
                return;
            }

            var items = new List<JournalApplicationFilterItem>
            {
                new(null, JournalText("Application.All")),
            };

            if (invocationApplicationId is Guid invocationId)
            {
                ApplicationIdentitySummary? invocationSummary = identities
                    .FirstOrDefault(summary => summary.ApplicationId.Value == invocationId);
                string invocationName = invocationSummary is not null
                    ? ApplicationDisplayName.From(invocationSummary)
                    : invocationId.ToString();
                items.Add(new JournalApplicationFilterItem(
                    invocationId,
                    string.Format(_localization.GetString("Journal.Application.Invocation"), invocationName)));
            }

            items.AddRange(identities
                .Where(summary => summary.ApplicationId.Value != invocationApplicationId)
                .Select(summary => new JournalApplicationFilterItem(
                    summary.ApplicationId.Value,
                    ApplicationDisplayName.From(summary))));

            // Reopening the journal must not silently keep a stale selection from a previous
            // protected session, so this always resets to "all applications".
            _journalInitializingPeriod = true;
            try
            {
                JournalApplicationFilter.ItemsSource = items;
                JournalApplicationFilter.SelectedIndex = 0;
            }
            finally
            {
                _journalInitializingPeriod = false;
            }

            _journalApplicationFilterSession = session;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The application filter is a convenience over the full journal; failing to list known
            // applications must not block reading the journal itself.
        }
    }

    private void OnJournalApplicationFilterChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_journalInitializingPeriod)
        {
            return;
        }

        ClearJournalGroupFilterForApplicationFilter();
        ResetJournalForPendingQueryChange("FilterChanged");
    }

    private Guid? CurrentJournalApplicationFilter() =>
        (JournalApplicationFilter.SelectedItem as JournalApplicationFilterItem)?.ApplicationId;

    private sealed record JournalApplicationFilterItem(Guid? ApplicationId, string DisplayName);
}
