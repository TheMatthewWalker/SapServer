using SAP.Middleware.Connector;
using SapServer.Configuration;

namespace SapServer.Services.Nco;

/// <summary>
/// Supplies connection parameters for the single SAP NCo destination this
/// spike uses. Registered once via RfcDestinationManager.RegisterDestinationConfiguration
/// (see NcoRfcService's constructor) — NCo calls back into GetParameters
/// whenever RfcDestinationManager.GetDestination(name) is asked to resolve a
/// destination it hasn't cached yet.
/// </summary>
internal sealed class NcoDestinationConfiguration : IDestinationConfiguration
{
    private readonly SapNcoOptions _options;

    public NcoDestinationConfiguration(SapNcoOptions options) => _options = options;

    public RfcConfigParameters GetParameters(string destinationName)
    {
        var parms = new RfcConfigParameters
        {
            { RfcConfigParameters.Name, destinationName },
            { RfcConfigParameters.AppServerHost, _options.AppServerHost },
            { RfcConfigParameters.SystemNumber, _options.SystemNumber },
            { RfcConfigParameters.Client, _options.Client },
            { RfcConfigParameters.User, _options.User },
            { RfcConfigParameters.Password, _options.Password },
            { RfcConfigParameters.Language, _options.Language },
            { RfcConfigParameters.PoolSize, _options.PoolSize.ToString() },
            { RfcConfigParameters.MaxPoolSize, _options.MaxPoolSize.ToString() },
        };
        return parms;
    }

    // No config-change source (no file watcher, no admin UI) — this spike's
    // parameters are fixed for the process lifetime once bound from appsettings.
    public bool ChangeEventsSupported() => false;

    public event RfcDestinationManager.ConfigurationChangeHandler? ConfigurationChanged
    {
        add { }
        remove { }
    }
}
