using System.Diagnostics;
using System.Runtime.InteropServices;
using TidyWin.Core.Erase;

namespace TidyWin.Platform;

/// <summary>
/// The parts of "erase an AI tool" that are not files: whether it is running, secrets kept in environment variables, and entries
/// in Windows Credential Manager. Values are never read: a variable is only noted as present, and a credential's secret blob is
/// never touched, only its target name (and type, which deleting needs).
/// </summary>
public sealed class WindowsEraseSystem : IEraseSystem
{
    public bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);
        try { return found.Length > 0; }
        finally { foreach (var p in found) p.Dispose(); }
    }

    public IReadOnlyList<SecretVariable> FindVariables(IEnumerable<string> names)
    {
        var list = new List<SecretVariable>();
        foreach (var name in names)
        {
            var user = Has(name, EnvironmentVariableTarget.User);
            var machine = Has(name, EnvironmentVariableTarget.Machine);
            var process = Has(name, EnvironmentVariableTarget.Process);
            if (user || machine || process) list.Add(new SecretVariable(name, user, machine, process));
        }
        return list;
    }

    private static bool Has(string name, EnvironmentVariableTarget target)
    {
        try { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, target)); }
        catch (Exception e) when (e is System.Security.SecurityException or IOException) { return false; }
    }

    public bool RemoveUserVariable(string name)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            return !Has(name, EnvironmentVariableTarget.User);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------------------------------------------------------------- Windows Credential Manager

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    public IReadOnlyList<CredentialEntry> FindCredentials(IEnumerable<string> keywords)
    {
        var words = keywords.Where(k => k.Length > 0).ToList();
        var list = new List<CredentialEntry>();
        if (words.Count == 0 || !CredEnumerateW(null, 0, out var count, out var buffer)) return list;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<Credential>(item);
                var target = Marshal.PtrToStringUni(cred.TargetName);
                if (string.IsNullOrEmpty(target)) continue;
                if (words.Any(w => target.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new CredentialEntry(target, (int)cred.Type));
            }
        }
        finally { CredFree(buffer); }
        return list;
    }

    public bool RemoveCredential(CredentialEntry entry) => CredDeleteW(entry.Target, (uint)entry.Type, 0);
}
