using SapServer.Services.Interfaces;

namespace SapServer.Services;

/// <summary>
/// Development-only permission service that grants access to every RFC function.
/// Registered only when Auth:DevBypassAuth = true and environment is Development.
/// </summary>
internal sealed class NullPermissionService : IPermissionService
{
    public Task<bool> CanExecuteAsync(int userId, string functionName, CancellationToken ct = default)
        => Task.FromResult(true);
}
