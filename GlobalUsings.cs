// Microsoft.NET.Sdk.Web (the old ASP.NET Core Web SDK) implicitly imported
// Microsoft.Extensions.Logging project-wide, which is why every controller's
// "ILogger<T> _logger" field compiled with no explicit using. Plain
// Microsoft.NET.Sdk (required for net48 + System.Web.Http) has no such list,
// so this single file replaces it rather than adding the using to every file
// that relies on ILogger/ILogger<T>.
global using Microsoft.Extensions.Logging;

// See Net48Polyfills.cs — restores KeyValuePair deconstruction and
// Dictionary.GetValueOrDefault, which net48's BCL predates.
global using SapServer.Net48Polyfills;
