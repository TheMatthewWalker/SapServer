using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class CommitHelper
{
    internal const string FnCommit = "BAPI_TRANSACTION_COMMIT";
    internal const string FnRollback = "BAPI_TRANSACTION_ROLLBACK";

    internal static RfcRequest BuildBapiCommit()
    {
        var builder = new RfcRequestBuilder(FnCommit);
            builder.Import("WAIT", "X");
        return builder.Build();
    }

    internal static RfcRequest BuildBapiRollback()
    {
        var builder = new RfcRequestBuilder(FnRollback);
        return builder.Build();
    }
}

