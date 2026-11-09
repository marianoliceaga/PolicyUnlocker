using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace PolicyUnlocker;

/// <summary>One registry value that lives under a policy location.</summary>
public sealed record PolicyEntry(RegistryHive Hive, string KeyPath, string ValueName, string ValueKind, string Data)
{
    public string HiveName => Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
    public string FullKey => $@"{HiveName}\{KeyPath}";
}

public sealed record OperationResult(
    string BackupDir,
    int ValuesRemoved,
    int KeysPruned,
    bool PolicyFilesDeleted,
    IReadOnlyList<string> Errors,
    string GpUpdateOutput)
{
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Values removed: {ValuesRemoved}");
        sb.AppendLine($"Empty keys cleaned up: {KeysPruned}");
        sb.AppendLine($"Registry.pol files deleted: {(PolicyFilesDeleted ? "yes" : "no")}");
        sb.AppendLine($"Backup folder: {BackupDir}");
        sb.AppendLine();
        sb.AppendLine("gpupdate: " + GpUpdateOutput.Trim());

        if (Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{Errors.Count} error(s):");
            foreach (var e in Errors.Take(10))
                sb.AppendLine(e);
        }
        return sb.ToString();
    }
}

public static class PolicyScanner
{
    /// <summary>Locations where Group Policy and organisation-enforced values are stored.</summary>
    public static readonly string[] PolicyRoots =
    {
        @"SOFTWARE\Policies",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
    };

    public static readonly RegistryHive[] Hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

    public static string BackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PolicyUnlocker", "Backups");

    public static List<PolicyEntry> Scan()
    {
        var results = new List<PolicyEntry>();
        foreach (var hive in Hives)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var root in PolicyRoots)
            {
                using var key = TryOpen(baseKey, root, writable: false);
                if (key != null)
                    Walk(key, hive, root, results);
            }
        }
        results.Sort((a, b) => string.Compare(a.FullKey + a.ValueName, b.FullKey + b.ValueName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>Organisation MDM enrollments (a sign the device is managed by a company).</summary>
    public static List<string> GetMdmEnrollments()
    {
        var list = new List<string>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var enrollments = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            if (enrollments == null) return list;

            foreach (var id in enrollments.GetSubKeyNames())
            {
                using var k = enrollments.OpenSubKey(id);
                var provider = k?.GetValue("ProviderID") as string;
                if (!string.IsNullOrEmpty(provider))
                    list.Add($"{provider}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            list.Add("(could not read enrollments: " + ex.Message + ")");
        }
        return list;
    }

    /// <summary>
    /// Backs up every policy root (as .reg files) and the local Registry.pol files.
    /// Throws if any backup step fails, so nothing is changed unless a backup exists.
    /// </summary>
    public static string CreateBackup()
    {
        var dir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        int n = 0;
        foreach (var hive in Hives)
        {
            var hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var root in PolicyRoots)
            {
                using (var key = TryOpen(baseKey, root, writable: false))
                {
                    if (key == null) continue;
                }

                var file = Path.Combine(dir, $"{++n:00}_{hiveName}_{root.Replace('\\', '_')}.reg");
                var (code, output) = Run("reg.exe", $"export \"{hiveName}\\{root}\" \"{file}\" /y");
                if (code != 0)
                    throw new IOException($"Backup of {hiveName}\\{root} failed: {output.Trim()}");
            }
        }

        foreach (var (name, path) in GroupPolicyFiles())
        {
            if (File.Exists(path))
                File.Copy(path, Path.Combine(dir, $"Registry.pol_{name}.bak"));
        }

        return dir;
    }

    /// <summary>
    /// Deletes the given values (after a backup), prunes keys left empty, optionally deletes
    /// the local Registry.pol files, then runs gpupdate /force.
    /// </summary>
    public static OperationResult Apply(IReadOnlyCollection<PolicyEntry> entries, bool deletePolicyFiles)
    {
        var backupDir = CreateBackup(); // throws before anything is changed

        var errors = new List<string>();
        int removed = 0;
        var touched = new HashSet<(RegistryHive Hive, string Path)>();

        foreach (var group in entries.GroupBy(e => (e.Hive, e.KeyPath)))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(group.Key.Hive, RegistryView.Registry64);
                using var key = TryOpen(baseKey, group.Key.KeyPath, writable: true);
                if (key == null)
                {
                    errors.Add($"Key not found: {group.Key.KeyPath}");
                    continue;
                }

                foreach (var entry in group)
                {
                    try
                    {
                        key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                        removed++;
                    }
                    catch (Exception ex) when (IsAccessError(ex))
                    {
                        errors.Add($"{entry.FullKey} [{entry.ValueName}]: {ex.Message}");
                    }
                }

                touched.Add((group.Key.Hive, group.Key.KeyPath));
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                errors.Add($"{group.Key.KeyPath}: {ex.Message}");
            }
        }

        int pruned = 0;
        foreach (var (hive, path) in touched.OrderByDescending(t => t.Path.Length))
        {
            try
            {
                pruned += PruneEmptyKeys(hive, path);
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                errors.Add($"Could not clean up {path}: {ex.Message}");
            }
        }

        bool filesDeleted = false;
        if (deletePolicyFiles)
        {
            foreach (var (_, file) in GroupPolicyFiles())
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        filesDeleted = true;
                    }
                }
                catch (Exception ex) when (IsAccessError(ex))
                {
                    errors.Add($"{file}: {ex.Message}");
                }
            }
        }

        var (gpCode, gpOut) = Run("gpupdate.exe", "/force");
        return new OperationResult(backupDir, removed, pruned, filesDeleted, errors, $"exit {gpCode}\n{gpOut}");
    }

    public static IEnumerable<(string Name, string Path)> GroupPolicyFiles()
    {
        var gp = Path.Combine(Environment.SystemDirectory, "GroupPolicy");
        yield return ("Machine", Path.Combine(gp, "Machine", "Registry.pol"));
        yield return ("User", Path.Combine(gp, "User", "Registry.pol"));
    }

    // ---------------------------------------------------------------- helpers

    private static void Walk(RegistryKey key, RegistryHive hive, string path, List<PolicyEntry> results)
    {
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            results.Add(new PolicyEntry(hive, path, name, kind.ToString(), FormatData(raw)));
        }

        foreach (var sub in key.GetSubKeyNames())
        {
            using var child = TryOpen(key, sub, writable: false);
            if (child != null)
                Walk(child, hive, $@"{path}\{sub}", results);
        }
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string subPath, bool writable)
    {
        try
        {
            return parent.OpenSubKey(subPath, writable);
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return null;
        }
    }

    /// <summary>Deletes path (and its ancestors) while they are empty and are not a policy root.</summary>
    private static int PruneEmptyKeys(RegistryHive hive, string path)
    {
        int pruned = 0;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        var current = path;

        while (!IsRoot(current))
        {
            int idx = current.LastIndexOf('\\');
            if (idx < 0) break;

            var parent = current[..idx];
            var name = current[(idx + 1)..];

            using var parentKey = baseKey.OpenSubKey(parent, writable: true);
            if (parentKey == null) break;

            using (var child = parentKey.OpenSubKey(name, writable: false))
            {
                if (child == null || child.ValueCount > 0 || child.SubKeyCount > 0)
                    break;
            }

            parentKey.DeleteSubKey(name, throwOnMissingSubKey: false);
            pruned++;
            current = parent;
        }

        return pruned;
    }

    private static bool IsRoot(string path) =>
        PolicyRoots.Any(r => r.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsAccessError(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException;

    private static string FormatData(object? raw) => raw switch
    {
        null => "",
        string s => s,
        int i => $"{i} (0x{i:X})",
        long l => $"{l} (0x{l:X})",
        string[] arr => string.Join("; ", arr),
        byte[] b => Convert.ToHexString(b.Length > 32 ? b[..32] : b) + (b.Length > 32 ? "..." : ""),
        _ => raw.ToString() ?? "",
    };

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }
}
