using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

public sealed class SapReturnMessage
{
    public string Type    { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}


