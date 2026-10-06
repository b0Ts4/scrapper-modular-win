using System.Runtime.InteropServices;

namespace Prescriva.Agent.Windows.Startup;

/// <summary>Whether the Agent runs with a package identity (installed from the Microsoft Store or an MSIX).</summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged
    {
        get
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
    }

    /// <summary>True when Windows started this packaged process through its startup task (sign-in).</summary>
    public static bool ActivatedByStartupTask()
    {
        if (!IsPackaged)
        {
            return false;
        }

        try
        {
            return global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (COMException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
