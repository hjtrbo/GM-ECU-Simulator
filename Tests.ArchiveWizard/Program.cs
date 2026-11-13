using System.IO;
using System.Reflection;
using System.Text.Json;
using Core.Dps;
using GmEcuSimulator.ViewModels.PrimeWizard;

// Exercise the actual view models without opening windows, file dialogs,
// adapters, IPC servers or the application's startup/registration paths.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var results = new List<object>();
        int failures = 0;
        string directory = Path.Combine(Path.GetTempPath(),
            "archive-wizard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "first.zip");
            string second = Path.Combine(directory, "second.zip");
            string invalid = Path.Combine(directory, "invalid.zip");
            SyntheticArchive.Create(first);
            SyntheticArchive.Create(second, new byte[] { 0x92, 0, 0, 0 });
            File.WriteAllText(invalid, "deliberately not an archive");

            void Check(string name, Action test)
            {
                try { test(); results.Add(new { name, passed = true }); }
                catch (Exception exception)
                {
                    failures++;
                    results.Add(new { name, passed = false, error = exception.ToString() });
                }
            }

            PrimeWizardContext WithOverride() => new()
            {
                ArchivePath = first,
                OverrideSecurityModuleId = "gm-bypass-2byte",
                OverrideFixedSeedHex = "1234"
            };

            Check("commit page defaults to unresolved without inventing an override", () =>
            {
                var context = new PrimeWizardContext();
                var page = new Page3CommitViewModel(context);
                page.OnEnter();
                Require(page.SelectedSecurityModule == UtilitySecuritySelection.UnresolvedModuleId);
                Require(page.AvailableSecurityModules.Contains(UtilitySecuritySelection.UnresolvedModuleId));
                Require(!page.IsFixedSeedVisible && context.OverrideSecurityModuleId is null);
            });

            Check("commit page preserves an explicit model and seed on re-entry", () =>
            {
                var context = WithOverride();
                var page = new Page3CommitViewModel(context);
                page.OnEnter();
                page.OnEnter();
                Require(page.SelectedSecurityModule == "gm-bypass-2byte");
                Require(page.FixedSeedHex == "1234" && context.OverrideFixedSeedHex == "1234");
                Require(context.OverrideSecurityModuleId == "gm-bypass-2byte");
            });

            Check("restoring the same valid archive preserves explicit choices", () =>
            {
                var context = WithOverride();
                var page = new Page1ArchiveViewModel(context, () => { });
                page.RestoreFromContext();
                Require(page.IsNextEnabled && context.Archive is not null);
                Require(context.OverrideSecurityModuleId == "gm-bypass-2byte");
                Require(context.OverrideFixedSeedHex == "1234");
            });

            Check("choosing a different archive clears stale model and seed", () =>
            {
                var context = WithOverride();
                var page = new Page1ArchiveViewModel(context, () => { });
                // Drive the post-file-dialog entry point without exposing a new
                // production API or invoking the native file picker in CI.
                var load = typeof(Page1ArchiveViewModel).GetMethod("Load",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Archive load method missing");
                load.Invoke(page, new object[] { second });
                Require(page.IsNextEnabled && context.ArchivePath == second);
                Require(context.OverrideSecurityModuleId is null && context.OverrideFixedSeedHex is null);
            });

            foreach (string path in new[] { Path.Combine(directory, "missing.zip"), invalid })
            {
                Check($"failed archive load clears choices ({Path.GetFileName(path)})", () =>
                {
                    var context = WithOverride();
                    context.ArchivePath = path;
                    var page = new Page1ArchiveViewModel(context, () => { });
                    page.RestoreFromContext();
                    Require(!page.IsNextEnabled && page.HasError && context.Archive is null);
                    Require(context.OverrideSecurityModuleId is null && context.OverrideFixedSeedHex is null);
                });
            }
        }
        finally { Directory.Delete(directory, true); }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            synthetic = true,
            hardware_access = false,
            tests = results.Count,
            failures,
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Wizard state mismatch");
    }
}
