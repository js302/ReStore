using System.Runtime.InteropServices;
using System.Text;

namespace ReStore.Interop
{
    /// <summary>Whether the process runs with MSIX package identity.</summary>
    public static class PackageInfo
    {
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);

        private static readonly Lazy<string?> _familyName = new(DetectFamilyName);

        /// <summary>The MSIX package family name, or null when unpackaged.</summary>
        public static string? FamilyName => _familyName.Value;

        private static string? DetectFamilyName()
        {
            if (!IsPackaged)
            {
                return null;
            }

            try
            {
                var length = 0;
                GetCurrentPackageFamilyName(ref length, null);
                if (length <= 0)
                {
                    return null;
                }

                var name = new StringBuilder(length);
                return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        private static readonly Lazy<bool> _isPackaged = new(Detect);

        public static bool IsPackaged => _isPackaged.Value;

        private static bool Detect()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                var length = 0;
                var result = GetCurrentPackageFullName(ref length, null);
                return result != APPMODEL_ERROR_NO_PACKAGE && (result == ERROR_INSUFFICIENT_BUFFER || result == 0);
            }
            catch (EntryPointNotFoundException)
            {
                // Pre-Windows 8: no packaged apps exist.
                return false;
            }
        }
    }
}
