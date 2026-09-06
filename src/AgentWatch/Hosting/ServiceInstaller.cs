using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using AgentWatch.Configuration;
using AgentWatch.Storage;
using AgentWatch.Windows;
using Microsoft.Win32;

namespace AgentWatch.Hosting;

public static class ServiceInstaller
{
    public const string ServiceName = "AgentWatch";
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);
    public static string InstalledExecutable => Path.Combine(InstallDirectory, "AgentWatch.exe");
    private const string MarkerName = "agentwatch.installation.json";
    private const string Marker = "{\"product\":\"AgentWatch\",\"layoutVersion\":1}";
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static string GetState()
    {
        try { using var controller = new ServiceController(ServiceName); return controller.Status.ToString(); }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: 1060 }) { return "Not installed"; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return "Unavailable: " + e.Message; }
    }
    private static void RequireElevation()
    {
        if (!IsElevated()) throw new UnauthorizedAccessException("This command requires an elevated PowerShell (Run as administrator). No UAC bypass or automatic elevation is attempted.");
    }
    public static string BinaryCommand(string executable, string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(dataDirectory)
            || executable.Contains('"') || dataDirectory.Contains('"')) throw new ArgumentException("Service paths must be absolute and cannot contain quotes.");
        return $"\"{executable}\" service --data-dir \"{dataDirectory.TrimEnd(Path.DirectorySeparatorChar)}\"";
    }
    public static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("AgentWatch installation paths cannot traverse reparse points: " + current);
    }
    public static void EnsureSingleLink(string path)
    {
        if (!File.Exists(path)) return;
        using var file = NativeMethods.CreateFile(path, 0, 7, 0, 3, 0, 0);
        if (file.IsInvalid || !FileMetadataNative.GetFileInformationByHandleEx(file, 1, out var metadata, (uint)Marshal.SizeOf<FileStandardInformation>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot validate installation file metadata: " + path);
        if (metadata.NumberOfLinks != 1) throw new IOException("Installation files must not be hard-linked: " + path);
    }
    private static void ValidateOwnership(string path)
    {
        EnsureNoReparse(path);
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any()) return;
        var marker = Path.Combine(path, MarkerName); EnsureNoReparse(marker); EnsureSingleLink(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 1024 || File.ReadAllText(marker) != Marker)
            throw new IOException("Refusing to modify a nonempty directory without an AgentWatch ownership marker: " + path);
    }
    private static void ProtectDirectory(string path, SecurityIdentifier reader)
    {
        EnsureNoReparse(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new(admins, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new(reader, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            EnsureNoReparse(child); EnsureSingleLink(child);
            if (Directory.Exists(child)) ProtectDirectory(child, reader);
            else
            {
                var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false); fileAcl.SetOwner(admins);
                fileAcl.AddAccessRule(new(system, FileSystemRights.FullControl, AccessControlType.Allow));
                fileAcl.AddAccessRule(new(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                fileAcl.AddAccessRule(new(reader, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                new FileInfo(child).SetAccessControl(fileAcl);
            }
        }
    }
    private static unsafe void Configure(AgentWatch.Windows.SafeServiceHandle service)
    {
        var delayed = 1; Check(ServiceNative.Configure(service, 3, &delayed), "Set delayed automatic start");
        ServiceAction* actions = stackalloc ServiceAction[3];
        actions[0] = new() { Type = 1, Delay = 60000 }; actions[1] = new() { Type = 1, Delay = 60000 }; actions[2] = new() { Type = 0, Delay = 0 };
        var failures = new ServiceFailureActions { ResetPeriod = 86400, Count = 3, Actions = actions };
        Check(ServiceNative.Configure(service, 2, &failures), "Set bounded service recovery");
        var nonCrashFailures = 1; Check(ServiceNative.Configure(service, 4, &nonCrashFailures), "Enable recovery for nonzero exit codes");
        var text = Marshal.StringToHGlobalUni("Low-overhead local observability service for AI development workloads.");
        try { Check(ServiceNative.Configure(service, 1, &text), "Set service description"); }
        finally { Marshal.FreeHGlobal(text); }
    }
    private static void Check(bool success, string operation)
    { if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError(), operation); }

    public static async Task Install(string? profileOverride, string? sidOverride, CancellationToken cancellationToken = default)
    {
        RequireElevation();
        if (GetState() != "Not installed") throw new InvalidOperationException("AgentWatch service already exists or is inaccessible. Use the documented upgrade procedure; its configuration was not modified.");
#pragma warning disable IL3000 // An empty managed assembly Location deliberately distinguishes the bundled installer.
        if (!string.IsNullOrEmpty(typeof(ServiceInstaller).Assembly.Location))
            throw new InvalidOperationException("Install from the self-contained single-file artifacts/publish/AgentWatch.exe, not a build apphost.");
#pragma warning restore IL3000
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var data = WatchConfig.DefaultDataDirectory;
        ValidateOwnership(InstallDirectory); ValidateOwnership(data); EnsureNoReparse(source);
        using var identity = WindowsIdentity.GetCurrent();
        var reader = sidOverride is null ? identity.User! : new SecurityIdentifier(sidOverride);
        var profile = profileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile)) throw new ArgumentException("A valid monitored user profile is required.");
        Directory.CreateDirectory(InstallDirectory); Directory.CreateDirectory(data);
        ProtectDirectory(InstallDirectory, reader); ProtectDirectory(data, reader);
        File.WriteAllText(Path.Combine(InstallDirectory, MarkerName), Marker); File.WriteAllText(Path.Combine(data, MarkerName), Marker);
        if (!string.Equals(Path.GetFullPath(source), InstalledExecutable, StringComparison.OrdinalIgnoreCase)) File.Copy(source, InstalledExecutable, true);
        var config = WatchConfig.Load(data, out _); config.MonitoredUserProfile = profile; config.ReaderSid = reader.Value;
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(config, JsonDefaults.Options));
        var runtimeDirectory = Path.Combine(InstallDirectory, "runtime"); Directory.CreateDirectory(runtimeDirectory); ProtectDirectory(runtimeDirectory, reader);
        using var manager = ServiceNative.OpenManager(null, null, 3);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Open Service Control Manager");
        using var service = ServiceNative.Create(manager, ServiceName, "AgentWatch AI Development Monitor", 0xF01FF, 0x10, 2, 1,
            BinaryCommand(InstalledExecutable, data), null, 0, null, "LocalSystem", null);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Create AgentWatch service");
        try
        {
            Configure(service);
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AgentWatch", true)
                ?? throw new InvalidOperationException("Created service registry key not found."))
                key.SetValue("Environment", new[] { "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + runtimeDirectory }, RegistryValueKind.MultiString);
            var startup = DateTimeOffset.UtcNow;
            using var controller = new ServiceController(ServiceName); controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            for (var i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(data, "agentwatch.db");
                if (File.Exists(path))
                {
                    try
                    {
                        using var connection = AgentWatchDatabase.OpenReadOnly(path); var meta = HistoryReader.Metadata(connection);
                        if (meta.TryGetValue("startedUtc", out var time) && DateTimeOffset.Parse(time) >= startup)
                        { Console.WriteLine("AgentWatch installed, running, delayed automatic start, two recovery restarts, database health check passed."); return; }
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException) { }
                }
                await Task.Delay(1000, cancellationToken); controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Stopped) break;
            }
            throw new InvalidOperationException("Service did not pass its database startup health check.");
        }
        catch
        {
            // A failed new installation must not leave a broken automatic service.
            Check(ServiceNative.Change(service, uint.MaxValue, 4, uint.MaxValue, null, null, 0, null, null, null, null), "Disable failed AgentWatch installation");
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Running) { controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            throw;
        }
    }
    public static void Uninstall(bool purgeData, bool confirmedPurge)
    {
        RequireElevation();
        if (purgeData && !confirmedPurge) throw new ArgumentException("Deleting history requires both --purge-data and --confirm-purge.");
        ValidateOwnership(InstallDirectory); if (purgeData) ValidateOwnership(WatchConfig.DefaultDataDirectory);
        using var manager = ServiceNative.OpenManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Open Service Control Manager");
        using (var service = ServiceNative.OpenService(manager, ServiceName, 0xF01FF))
        {
            if (!service.IsInvalid)
            {
                using var registry = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AgentWatch");
                var expected = BinaryCommand(InstalledExecutable, WatchConfig.DefaultDataDirectory);
                if (!string.Equals(registry?.GetValue("ImagePath") as string, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Service image path differs from the owned AgentWatch installation; refusing to stop/delete it.");
                Check(ServiceNative.Change(service, uint.MaxValue, 4, uint.MaxValue, null, null, 0, null, null, null, null), "Disable AgentWatch before uninstall");
                using var controller = new ServiceController(ServiceName);
                if (controller.Status != ServiceControllerStatus.Stopped) { controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                Check(ServiceNative.Delete(service), "Delete AgentWatch service");
            }
            else if (Marshal.GetLastPInvokeError() != 1060) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Open AgentWatch service");
        }
        if (File.Exists(InstalledExecutable))
        {
            try { File.Delete(InstalledExecutable); }
            catch (IOException) when (string.Equals(Environment.ProcessPath, InstalledExecutable, StringComparison.OrdinalIgnoreCase))
            { Check(ServiceNative.MoveFile(InstalledExecutable, null, 4), "Schedule own executable removal"); Console.WriteLine("The running installed executable will be removed at reboot. Invoke uninstall from the publish copy for immediate removal."); }
        }
        var runtime = Path.GetFullPath(Path.Combine(InstallDirectory, "runtime"));
        if (!runtime.StartsWith(Path.GetFullPath(InstallDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Runtime cleanup target escaped the installation directory.");
        if (Directory.Exists(runtime))
        {
            // Validate every entry without following reparse points before recursive removal.
            void ValidateTree(string directory)
            {
                EnsureNoReparse(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    EnsureNoReparse(entry); EnsureSingleLink(entry);
                    if (Directory.Exists(entry)) ValidateTree(entry);
                }
            }
            ValidateTree(runtime);
            try { Directory.Delete(runtime, true); }
            catch (IOException) { Console.WriteLine("Runtime cache is in use and remains at " + runtime + "; repeat uninstall from the publish copy after closing the installed executable."); }
        }
        if (purgeData)        {
            var data = Path.GetFullPath(WatchConfig.DefaultDataDirectory); EnsureNoReparse(data);
            foreach (var name in new[] { "agentwatch.db", "agentwatch.db-wal", "agentwatch.db-shm", "agentwatch.db.writer-lock", "config.json", "agentwatch.log", "agentwatch.previous.log" })
            { var file = Path.Combine(data, name); EnsureNoReparse(file); File.Delete(file); }
        }
        Console.WriteLine(purgeData ? "AgentWatch service removed; explicit history purge completed." : "AgentWatch service removed; history and configuration retained.");
    }
}
