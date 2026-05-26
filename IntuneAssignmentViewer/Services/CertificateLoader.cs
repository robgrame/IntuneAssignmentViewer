using System.Security.Cryptography.X509Certificates;

namespace IntuneAssignmentViewer.Services;

/// <summary>
/// Loads an X509 certificate from multiple sources for Graph client-credentials auth.
/// </summary>
/// <remarks>
/// Source priority:
/// 1. <c>thumbprint</c> — looks up in the Windows certificate store (LocalMachine/CurrentUser, My/Root, etc.)
/// 2. <c>pfxBase64</c> — inline base64-encoded PFX content (useful for Kubernetes secrets / Key Vault references)
/// 3. <c>pfxPath</c> — file path to a PFX on disk
/// </remarks>
public static class CertificateLoader
{
    public static X509Certificate2? Load(
        string? thumbprint,
        string? pfxPath,
        string? pfxPassword,
        string? pfxBase64,
        string storeLocationName,
        string storeNameName,
        ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            if (!Enum.TryParse<StoreLocation>(storeLocationName, true, out var storeLocation))
            {
                logger.LogError("Invalid Graph:CertificateStoreLocation '{Loc}'. Use CurrentUser or LocalMachine.", storeLocationName);
                return null;
            }
            if (!Enum.TryParse<StoreName>(storeNameName, true, out var storeName))
            {
                logger.LogError("Invalid Graph:CertificateStoreName '{Name}'. Use My, Root, etc.", storeNameName);
                return null;
            }

            try
            {
                using var store = new X509Store(storeName, storeLocation);
                store.Open(OpenFlags.ReadOnly);
                var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
                if (found.Count == 0)
                {
                    logger.LogError("Certificate with thumbprint {Thumb} not found in {Location}\\{Name}",
                        normalized, storeLocation, storeName);
                    return null;
                }
                return found[0];
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load certificate from store {Location}\\{Name}", storeLocation, storeName);
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(pfxBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(pfxBase64);
                return X509CertificateLoader.LoadPkcs12(bytes, pfxPassword,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load certificate from Graph:CertificateBase64");
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(pfxPath))
        {
            try
            {
                if (!File.Exists(pfxPath))
                {
                    logger.LogError("Certificate file not found at path: {Path}", pfxPath);
                    return null;
                }
                return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pfxPassword,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load certificate from PFX file: {Path}", pfxPath);
                return null;
            }
        }

        return null;
    }
}
