// global using directives don't flow across project references — this test
// project needs its own copy of SapServer's GlobalUsings.cs entries. See
// Net48Polyfills.cs in the main project for what this restores.
global using Microsoft.Extensions.Logging;
global using SapServer.Net48Polyfills;
