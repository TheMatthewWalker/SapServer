namespace SapServer.Services.Interfaces;

public interface IPermissionService
{
    /// <summary>
    /// Returns true if the user identified by <paramref name="userId"/> is permitted
    /// to call <paramref name="functionName"/>. Results are cached per
    /// <see cref="Configuration.AuthOptions.PermissionCacheSeconds"/>.
    /// Fails closed (returns false) on any database error.
    /// </summary>
    Task<bool> CanExecuteAsync(int userId, string functionName, CancellationToken ct = default);
}
