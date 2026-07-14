using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Chat2ApiTray.Services;

public static class LocalDataDirectorySecurity
{
    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureWindowsPrivateDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsPrivateDirectory(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to determine the current Windows user.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        AddFullControl(security, owner, inheritance);
        AddFullControl(security, new SecurityIdentifier("S-1-5-18"), inheritance);
        AddFullControl(security, new SecurityIdentifier("S-1-5-32-544"), inheritance);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid, InheritanceFlags inheritance)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
