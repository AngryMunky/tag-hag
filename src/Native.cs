using System.Runtime.InteropServices;

namespace TheTagHag;

/// <summary>Reused verbatim from CivitaiHarvesterApp — lets headless CLI output (e.g.
/// --selftest) be visible when launched from a terminal, without allocating a new console.</summary>
internal static class Native
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    public static void TryAttachParentConsole()
    {
        if (Console.IsOutputRedirected) return;
        AttachConsole(ATTACH_PARENT_PROCESS);
    }
}
