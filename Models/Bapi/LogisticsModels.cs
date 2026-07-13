using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;



public sealed class PicksheetRow
{
    public string               DeliveryNumber { get; init; } = string.Empty;
    public string               CustomerNumber { get; init; } = string.Empty;
    public string               DispatchDate { get; init; } = string.Empty;
    public string               DeliveryDate { get; init; } = string.Empty;
    public string               Incoterms { get; init; } = string.Empty;
}

