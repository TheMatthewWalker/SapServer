using System.Collections.Concurrent;
using SAP.Middleware.Connector;
using SapServer.Configuration;

namespace SapServer.Services.Nco;

/// <summary>
/// The single IDestinationConfiguration registered with RfcDestinationManager
/// for the whole process (RegisterDestinationConfiguration may only be called
/// once — see NcoConnectionPool's constructor). Unlike the spike's original
/// NcoDestinationConfiguration (one fixed destination), every NcoWorker needs
/// its own uniquely-named destination — service workers so each can hold its
/// own pinned connection/credentials, elevated workers so a later acquisition
/// with a different user's credentials can never resolve to another user's
/// cached destination. Workers register their params here before calling
/// RfcDestinationManager.GetDestination(name); NCo calls back into
/// GetParameters for any name it hasn't resolved yet.
/// </summary>
internal sealed class NcoDestinationRegistry : IDestinationConfiguration
{
    private readonly SapNcoOptions _options;
    private readonly ConcurrentDictionary<string, RfcConfigParameters> _entries = new();

    public NcoDestinationRegistry(SapNcoOptions options) => _options = options;

    public void Register(string destinationName, SapConnectionOptions creds)
    {
        var parms = new RfcConfigParameters
        {
            { RfcConfigParameters.Name, destinationName },
            { RfcConfigParameters.AppServerHost, creds.AppServerHost },
            { RfcConfigParameters.SystemNumber, creds.SystemNumber },
            { RfcConfigParameters.Client, creds.Client },
            { RfcConfigParameters.User, creds.User },
            { RfcConfigParameters.Password, creds.Password },
            { RfcConfigParameters.Language, creds.Language },
            { RfcConfigParameters.PoolSize, _options.PoolSize.ToString() },
            { RfcConfigParameters.MaxPoolSize, _options.MaxPoolSize.ToString() },
        };
        _entries[destinationName] = parms;
    }

    public void Unregister(string destinationName) => _entries.TryRemove(destinationName, out _);

    public RfcConfigParameters GetParameters(string destinationName)
    {
        if (_entries.TryGetValue(destinationName, out var parms))
            return parms;

        throw new InvalidOperationException(
            $"No NCo connection parameters registered for destination '{destinationName}' — " +
            "the owning NcoWorker must call Register(name, creds) before GetDestination(name).");
    }

    // No config-change source (no file watcher, no admin UI) — every change
    // goes through Register/Unregister directly, called by the owning worker.
    public bool ChangeEventsSupported() => false;

    public event RfcDestinationManager.ConfigurationChangeHandler? ConfigurationChanged
    {
        add { }
        remove { }
    }
}
