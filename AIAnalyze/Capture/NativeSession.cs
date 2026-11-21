using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIAnalyze.Capture;

/// <summary>A process started in a user session, with its stdout and stderr as readable streams.</summary>
internal sealed class SessionProcess(int processId, Stream standardOutput, Stream standardError) : IDisposable
{
    public int ProcessId { get; } = processId;
    public Stream StandardOutput { get; } = standardOutput;
    public Stream StandardError { get; } = standardError;

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
    }
}

/// <summary>
/// Win32 calls needed to start a process in the logged-on user's session from a service, with its
/// output on anonymous pipes. Only valid on Windows; the caller must run as LocalSystem (or hold SE_TCB_NAME).
/// </summary>
internal static class NativeSession
{
    internal const uint NoActiveSession = 0xFFFFFFFF;

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationTypeLimited = 3;
    private const int TokenTypePrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
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
    private static extern bool CreatePipe(
        out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Returns the console session id when a user is logged on there. At boot there is no console
    /// session, and at the sign-in screen there is a session but no user token; both return false.
    /// </summary>
    internal static bool TryGetLoggedOnConsoleSession(out uint sessionId)
    {
        sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession)
        {
            return false;
        }
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            return false;
        }
        CloseHandle(token);
        return true;
    }

    /// <summary>
    /// Starts a process with the token of the user logged on at <paramref name="sessionId"/>, with its
    /// stdout and stderr on anonymous pipes. For an administrator with UAC, the elevated linked token is used.
    /// </summary>
    internal static SessionProcess StartInSession(uint sessionId, string commandLine, string workingDirectory)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");
        }

        var primary = IntPtr.Zero;
        var outRead = IntPtr.Zero;
        var outWrite = IntPtr.Zero;
        var errRead = IntPtr.Zero;
        var errWrite = IntPtr.Zero;
        try
        {
            primary = ElevatedPrimaryToken(userToken);

            // The read ends stay with the service; the write ends go to PresentMon.
            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = 1,
            };
            if (!CreatePipe(out outRead, out outWrite, ref attributes, 0)
                || !CreatePipe(out errRead, out errWrite, ref attributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
            }
            SetHandleInformation(outRead, HandleFlagInherit, 0);
            SetHandleInformation(errRead, HandleFlagInherit, 0);

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = "winsta0\\default",
                dwFlags = StartfUseStdHandles,
                hStdInput = IntPtr.Zero,
                hStdOutput = outWrite,
                hStdError = errWrite,
            };

            if (!CreateProcessAsUser(
                    primary,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);

            // Close the write ends here, so the read ends see end-of-file when PresentMon exits.
            CloseHandle(outWrite);
            CloseHandle(errWrite);
            outWrite = IntPtr.Zero;
            errWrite = IntPtr.Zero;

            var stdout = new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            outRead = IntPtr.Zero;
            var stderr = new FileStream(new SafeFileHandle(errRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            errRead = IntPtr.Zero;

            return new SessionProcess((int)info.dwProcessId, stdout, stderr);
        }
        finally
        {
            foreach (var handle in new[] { outWrite, errWrite, outRead, errRead })
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the linked elevated token.");
        }

        try
        {
            if (!DuplicateTokenEx(linked, MaximumAllowed, IntPtr.Zero, 0, TokenTypePrimary, out var primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }
            return primary;
        }
        finally
        {
            CloseHandle(linked);
        }
    }
}
