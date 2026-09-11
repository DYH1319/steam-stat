using System.Net.NetworkInformation;

namespace SteamStat.Core.Steam.Session.Internal;

internal interface INetworkAvailability
{
    bool IsAvailable { get; }
    Task WaitUntilAvailableAsync(CancellationToken cancellationToken);
}

internal sealed class SystemNetworkAvailability : INetworkAvailability
{
    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();

    public async Task WaitUntilAvailableAsync(CancellationToken cancellationToken)
    {
        if (IsAvailable) return;
        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, EventArgs __)
        {
            if (IsAvailable) available.TrySetResult();
        }
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
        try
        {
            if (IsAvailable) return;
            await available.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        }
    }
}
