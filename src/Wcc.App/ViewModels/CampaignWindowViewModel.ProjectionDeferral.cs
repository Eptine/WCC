using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private int _campaignProjectionDeferralDepth;
    private long _campaignProjectionDeferralVersion;
    private bool _campaignProjectionRefreshPending;
    private TaskCompletionSource? _campaignProjectionDeferralCompletion;

    // Configuration changes still revoke old authority immediately. A batch
    // starts its replacement producer only after all current inputs are assigned.
    internal IDisposable DeferCampaignFieldProjection()
    {
        lock (_campaignFieldGate)
        {
            _campaignProjectionDeferralDepth++;
            return new CampaignProjectionDeferral(this, _campaignProjectionDeferralVersion);
        }
    }

    private bool DeferRequestedCampaignFieldProjection()
    {
        if (_campaignProjectionDeferralDepth == 0) return false;
        _campaignProjectionRefreshPending = true;
        _campaignProjectionDeferralCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private void RetireCampaignProjectionDeferrals()
    {
        _campaignProjectionDeferralVersion++;
        _campaignProjectionDeferralDepth = 0;
        CancelDeferredCampaignProjection();
    }

    private void CancelDeferredCampaignProjection()
    {
        _campaignProjectionRefreshPending = false;
        var completion = _campaignProjectionDeferralCompletion;
        _campaignProjectionDeferralCompletion = null;
        // A retired wait grants no authority, just as a cancelled capture returns
        // without publishing. Callers must still check their source binding.
        completion?.TrySetResult();
    }

    private static async Task CompleteDeferredCampaignProjectionAsync(TaskCompletionSource completion, Task producer)
    {
        try { await producer.ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException cancellation) { completion.TrySetCanceled(cancellation.CancellationToken); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }

    private sealed class CampaignProjectionDeferral(CampaignWindowViewModel owner, long version) : IDisposable
    {
        private CampaignWindowViewModel? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._campaignFieldGate)
            {
                // An older Open cannot release or restart a different source's batch.
                if (version != current._campaignProjectionDeferralVersion) return;
                if (--current._campaignProjectionDeferralDepth != 0 || !current._campaignProjectionRefreshPending) return;
                current._campaignProjectionRefreshPending = false;
                var completion = current._campaignProjectionDeferralCompletion;
                try
                {
                    current.StartCampaignFieldProjection();
                    if (ReferenceEquals(current._campaignProjectionDeferralCompletion, completion))
                        current._campaignProjectionDeferralCompletion = null;
                    if (completion is not null)
                        _ = CompleteDeferredCampaignProjectionAsync(completion, current._campaignProjectionTask);
                }
                catch (Exception failure)
                {
                    if (ReferenceEquals(current._campaignProjectionDeferralCompletion, completion))
                        current._campaignProjectionDeferralCompletion = null;
                    completion?.TrySetException(failure);
                    throw;
                }
            }
        }
    }
}