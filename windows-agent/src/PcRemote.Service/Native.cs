// Win32 entry points needed to start the input helper inside another user's
// session. Kept in one file so InputHelper reads as intent rather than as
// interop noise.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcRemote.Service;

internal static class Native
{
    internal const uint CREATE_NO_WINDOW = 0x08000000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    internal const uint TOKEN_DUPLICATE = 0x0002;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_SESSIONID = 0x0100;

    /// <summary>TokenInformationClass value for TokenSessionId.</summary>
    internal const int TokenSessionId = 12;

    internal enum SecurityImpersonationLevel { SecurityAnonymous }
    internal enum TokenType { TokenPrimary = 1 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        internal int cb;
        internal IntPtr lpReserved, lpDesktop, lpTitle;
        internal int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        internal short wShowWindow, cbReserved2;
        internal IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        internal IntPtr hProcess;
        internal IntPtr hThread;
        internal uint dwProcessId;
        internal uint dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        SecurityImpersonationLevel level, TokenType type, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(
        IntPtr token, int infoClass, ref uint info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}