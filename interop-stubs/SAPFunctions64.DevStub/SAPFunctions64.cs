// DEV/CI-ONLY STUB — NOT THE REAL SAP INTEROP ASSEMBLY.
//
// SapServer.csproj normally references the real SAPFunctions64 COM interop
// assembly generated from an actual SAP GUI installation (see
// SapServer/README.md's "Place the SAP COM Interop DLL" section) at
// libs/Interop.SAPFunctions64.dll. That file is machine/license-specific and
// deliberately not checked into source control.
//
// Any environment without it — a fresh dev machine that hasn't generated the
// interop DLL yet, or a CI runner, which can never have a real SAP GUI
// install — cannot compile SapServer.csproj at all without some stand-in for
// these types, even though SapStaWorker.cs's actual COM calls are explicitly
// out of scope for automated tests (see SapServer.Tests).
//
// This supplies just enough of the SAPFunctions64 namespace's shape for
// SapStaWorker.cs to compile — a constructible SAPFunctions class with the
// members it statically references (Connection, Add, RemoveAll), and the
// IFunction interface it casts to. None of it does anything real, and every
// member throws if actually invoked — nothing in this test suite calls
// through to it. SapServer.csproj only references this project when
// libs/Interop.SAPFunctions64.dll is absent (see the Condition="!Exists(...)"
// there) — the real DLL always wins when present.

namespace SAPFunctions64;

public interface IFunction
{
    string? Exception { get; }
    bool Call();
}

public class SAPFunctions
{
    public dynamic? Connection { get; set; }

    public dynamic Add(string functionName) => throw new NotSupportedException(
        "SAPFunctions64.DevStub — no real SAP COM connection is available in this environment.");

    public void RemoveAll() { }
}
