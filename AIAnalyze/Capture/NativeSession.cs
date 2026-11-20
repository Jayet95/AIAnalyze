using System.Runtime.InteropServices;

namespace AIAnalyze.Capture;

/// <summary>
/// Win32 calls needed to start a process in the logged-on user's session from a service.
/// Only valid on Windows; the caller must be running as LocalSystem (or hold SE_TCB_NAME).
/// </summary>
internal static class NativeSession
{
    internal const uint NoActiveSession = 0xFFFFFFFF;

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint MaximumAllowed = 0x02000000;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationTypeLimited = 3;
    private const int TokenTypePrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenInformationHandle(
        IntPtr tokenHandle, int tokenInformationClass, out IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Starts a process with the token of the user logged on at <paramref name="sessionId"/>.
    /// For an administrator with UAC, the elevated linked token is used so the process can start ETW traces.
    /// Returns the new process id.
    /// </summary>
    internal static int StartInSession(uint sessionId, string commandLine, string workingDirectory)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
        }

        var primary = IntPtr.Zero;
        try
        {
            primary = ElevatedPrimaryToken(userToken);

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default",
            };

            if (!CreateProcessAsUser(
                    primary,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var info))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return (int)info.dwProcessId;
        }
        finally
        {
            if (primary != IntPtr.Zero && primary != userToken)
            {
                CloseHandle(primary);
            }
            CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Returns a primary token for the user. If UAC gave the user a limited (filtered) token,
    /// returns the linked elevated token instead, duplicated as a primary token.
    /// The caller closes the result if it differs from <paramref name="userToken"/>.
    /// </summary>
    private static IntPtr ElevatedPrimaryToken(IntPtr userToken)
    {
        if (!GetTokenInformation(userToken, TokenElevationTypeClass, out var elevationType, sizeof(int), out _))
        {
            return userToken;
        }
        if (elevationType != TokenElevationTypeLimited)
        {
            return userToken;
        }

        if (!GetTokenInformationHandle(userToken, TokenLinkedTokenClass, out var linked, IntPtr.Size, out _))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(), "Could not read the linked elevated token.");
        }

        try
        {
            if (!DuplicateTokenEx(linked, MaximumAllowed, IntPtr.Zero, 0, TokenTypePrimary, out var primary))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }
            return primary;
        }
        finally
        {
            CloseHandle(linked);
        }
    }
}
