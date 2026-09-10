using LoLHelper.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LoLHelper.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--acl-diag") return RunAclDiagnostic();
            if (args.Length > 0 && args[0] == "--ui-smoke")
            {
                try { return UiSmoke.Run(); }
                catch (Exception ex)
                {
                    for (Exception? current = ex; current != null; current = current.InnerException)
                        Console.WriteLine(current.GetType().FullName + ": " + current.Message);
                    return 1;
                }
            }
            if (args.Length == 1)
            {
                var signature = new SignatureVerifier().Verify(args[0]);
                Console.WriteLine("trusted={0}; riot={1}; publisher={2}; error={3}",
                    signature.IsTrusted, signature.IsRiot, signature.Publisher, signature.Error);
                return signature.IsTrusted && signature.IsRiot ? 0 : 2;
            }
            try
            {
                TestBlockList();
                TestInlineList();
                TestMalformedListIndent();
                TestRealWorldFileIsNormalised();
                TestAlreadyChinese();
                TestUnsignedFileIsRejected();
                TestTrustedNonRiotFile();
                TestDownloadTransitions();
                TestConfigurationLockRoundTrip();
                TestFreshSignatureRejectsTampering();
            }
            catch (Exception ex)
            {
                for (Exception? current = ex; current != null; current = current.InnerException)
                    Console.WriteLine(current.GetType().FullName + ": " + current.Message);
                return 1;
            }
            Console.WriteLine("Locale, signature and download state tests passed.");
            return 0;
        }

        private static void TestBlockList()
        {
            const string input = "locale_data:\r\n  available_locales:\r\n    - \"en_US\"\r\n  default_locale: \"en_US\" # keep\r\nsettings:\r\n  locale: en_US\r\n  untouched: true\r\n";
            var result = YamlLocaleEditor.Apply(input);
            Require(result.Contains("    - \"zh_CN\"\r\n"), "block list insertion");
            Require(result.Contains("default_locale: \"zh_CN\" # keep"), "comment preservation");
            Require(result.Contains("  untouched: true"), "unrelated setting preservation");
            Require(result.EndsWith("\r\n", StringComparison.Ordinal), "newline preservation");
        }

        private static void TestInlineList()
        {
            const string input = "locale_data:\n  available_locales: [\"en_US\", \"ja_JP\"]\n  default_locale: ja_JP\nsettings:\n  locale: ja_JP\n";
            var result = YamlLocaleEditor.Apply(input);
            Require(result.Contains("[\"en_US\", \"ja_JP\", \"zh_CN\"]"), "inline list insertion");
            Require(result.Contains("  locale: \"zh_CN\""), "settings locale update");
        }

        private static void TestAlreadyChinese()
        {
            const string input = "locale_data:\n  available_locales:\n    - zh_CN\n  default_locale: zh_CN\nsettings:\n  locale: zh_CN\n";
            var result = YamlLocaleEditor.Apply(input);
            Require(Regex.Matches(result, "zh_CN").Count == 3, "locale should not be duplicated");
        }

        private static void TestMalformedListIndent()
        {
            // Regression, observed in a real product_settings.yaml: the list's FIRST entry was
            // indented one level deeper than the rest and already held zh_CN. Anchoring the append on
            // that first entry produced a "      - \"zh_CN\"" deeper than its siblings.
            const string input =
                "patching_policy: \"manual\"\r\n" +
                "locale_data:\r\n" +
                "    available_locales:\r\n" +
                "      - \"zh_CN\"\r\n" +
                "    - \"en_US\"\r\n" +
                "    - \"ja_JP\"\r\n" +
                "    - \"zh_TW\"\r\n" +
                "    default_locale: \"en_US\"\r\n" +
                "settings:\r\n" +
                "    locale: \"en_US\"\r\n";
            var lines = YamlLocaleEditor.Apply(input).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var key = Array.FindIndex(lines, line => line == "    available_locales:");
            Require(key >= 0, "available_locales key preserved");
            var items = new List<string>();
            for (var i = key + 1; i < lines.Length && lines[i].TrimStart().StartsWith("-", StringComparison.Ordinal); i++) items.Add(lines[i]);
            // deduplicated and normalised: the existing zh_CN entry is moved, not copied
            Require(items.Count == 4, "locales deduplicated into one list, found " + items.Count);
            Require(items[0] == "    - \"en_US\"" && items[1] == "    - \"ja_JP\"" && items[2] == "    - \"zh_TW\"",
                "remaining locales keep their order");
            Require(items[3] == "    - \"zh_CN\"", "zh_CN appended last, got '" + items[3] + "'");
            // The list must come out as one consistent sequence at the shallowest indent.
            foreach (var item in items)
                Require(item.Length - item.TrimStart().Length == 4, "every item at the canonical indent: '" + item + "'");
            Require(Array.FindIndex(lines, line => line == "    default_locale: \"zh_CN\"") > key + items.Count,
                "default_locale stays inside locale_data after the list");
            foreach (var line in lines) Require(!line.StartsWith("-", StringComparison.Ordinal), "no list item at column zero");
        }

        private static void TestRealWorldFileIsNormalised()
        {
            // Verbatim shape of the real league_of_legends...product_settings.yaml: locale_data at 0,
            // available_locales at 4 with its items at the SAME indent 4 (lenient YAML the client
            // accepts), and one stray zh_CN left at 6 by an earlier edit.
            const string locales = "ar_AE id_ID cs_CZ de_DE el_GR en_AU en_GB en_PH en_SG en_US es_AR es_ES es_MX " +
                "fr_FR hu_HU it_IT ja_JP ko_KR pl_PL pt_BR ro_RO ru_RU th_TH tr_TR vi_VN zh_MY zh_TW";
            var input = "auto_patching_enabled_by_player: false\r\n" +
                "dependencies:\r\n" +
                "    Direct X 9:\r\n" +
                "        hash: \"f231f4ed\"\r\n" +
                "    vanguard: true\r\n" +
                "locale_data:\r\n" +
                "    available_locales:\r\n" +
                string.Join(string.Empty, locales.Split(' ').Select(x => "    - \"" + x + "\"\r\n")) +
                "      - \"zh_CN\"\r\n" +
                "    default_locale: \"zh_CN\"\r\n" +
                "patching_policy: \"manual\"\r\n" +
                "settings:\r\n" +
                "    locale: \"zh_CN\"\r\n" +
                "should_repair: false\r\n";
            var lines = YamlLocaleEditor.Apply(input).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var key = Array.FindIndex(lines, line => line == "    available_locales:");
            Require(key >= 0, "available_locales survives a real file layout");
            var items = new List<string>();
            for (var i = key + 1; i < lines.Length && lines[i].TrimStart().StartsWith("-", StringComparison.Ordinal); i++) items.Add(lines[i]);
            Require(items.Count == 28, "28 locales after adding zh_CN, found " + items.Count);
            // The stray 6-space entry must be pulled back to the 4 every other entry uses.
            foreach (var item in items)
                Require(item.Length - item.TrimStart().Length == 4, "list is uniformly indented: '" + item + "'");
            Require(items.Count(x => x.Contains("zh_CN")) == 1, "zh_CN appears exactly once");
            Require(items[items.Count - 1] == "    - \"zh_CN\"", "zh_CN is the last entry, got '" + items[items.Count - 1] + "'");
            Require(items[0] == "    - \"ar_AE\"", "other locales keep their order, got '" + items[0] + "'");
            Require(lines.Any(line => line == "    default_locale: \"zh_CN\""), "default_locale rewritten");
            Require(lines.Any(line => line == "    locale: \"zh_CN\""), "settings.locale rewritten");
            Require(lines.Any(line => line == "        hash: \"f231f4ed\""), "unrelated sections untouched");
            Require(lines.Any(line => line == "should_repair: false") && lines.Any(line => line == "patching_policy: \"manual\""),
                "keys after locale_data untouched");
        }

        /// <summary>
        /// Elevated ACL round trip against a temp file. Run with --acl-diag from an elevated prompt:
        /// the lock rewrites a deny-write DACL and sets a read-only attribute, which a normal user
        /// token cannot do, so this is the only way to exercise the shipped lock on a real file.
        /// It asserts BOTH protections, because either one alone lets the client rewrite the locale.
        /// </summary>
        private static int RunAclDiagnostic()
        {
            var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Console.WriteLine("elevated=" + elevated + " user=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rift-acl-diag-" + Guid.NewGuid().ToString("N") + ".yaml");
            System.IO.File.WriteAllText(path, "locale: zh_CN");
            try
            {
                LocaleService.LockForTest(path);
                var readOnly = (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0;
                Console.WriteLine("lock: readonly=" + readOnly + " denyRules=" + DenyRuleCount(path) + " writable=" + CanWrite(path));
                if (!readOnly) { Console.WriteLine("[FAIL] read-only attribute missing; a privileged client would bypass the deny ACE"); return 1; }
                if (DenyRuleCount(path) == 0) { Console.WriteLine("[FAIL] deny rules missing"); return 1; }
                if (CanWrite(path)) { Console.WriteLine("[FAIL] locked file still writable"); return 1; }

                LocaleService.UnlockForTest(path);
                Console.WriteLine("unlock: readonly=" + ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0) +
                    " denyRules=" + DenyRuleCount(path) + " writable=" + CanWrite(path));
                if ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0) { Console.WriteLine("[FAIL] read-only survived unlock"); return 1; }
                if (DenyRuleCount(path) != 0) { Console.WriteLine("[FAIL] deny rules survived unlock"); return 1; }
                if (!CanWrite(path)) { Console.WriteLine("[FAIL] unlocked file still not writable"); return 1; }

                LocaleService.LockForTest(path);
                LocaleService.UnlockForTest(path);
                if ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0 || !CanWrite(path))
                { Console.WriteLine("[FAIL] second cycle left the file locked"); return 1; }

                Console.WriteLine("RESULT: ACL round trip passed (both protections applied and removed)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("RESULT: aborted");
                for (var current = ex; current != null; current = current.InnerException)
                    Console.WriteLine("  " + current.GetType().FullName + " :: " + current.Message);
                return 1;
            }
            finally
            {
                try { LocaleService.UnlockForTest(path); } catch { }
                try { System.IO.File.Delete(path); } catch { }
            }
        }

        private static bool CanWrite(string path)
        {
            try { System.IO.File.WriteAllText(path, "locale: en_US"); return true; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static int DenyRuleCount(string path) =>
            System.IO.File.GetAccessControl(path, System.Security.AccessControl.AccessControlSections.Access)
                .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .OfType<System.Security.AccessControl.FileSystemAccessRule>()
                .Count(rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);

        private static void TestUnsignedFileIsRejected()
        {
            Console.WriteLine("Testing Authenticode rejection…");
            var result = new SignatureVerifier().Verify(Assembly.GetExecutingAssembly().Location);
            Require(!result.IsTrusted && !result.IsRiot, "unsigned executable rejection");
        }

        private static void TestTrustedNonRiotFile()
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"dotnet\dotnet.exe");
            var result = new SignatureVerifier().Verify(path);
            Console.WriteLine("Signed test: trusted={0}, riot={1}, publisher={2}, error={3}",
                result.IsTrusted, result.IsRiot, result.Publisher, result.Error);
            Require(result.IsTrusted && !result.IsRiot && !string.IsNullOrWhiteSpace(result.Publisher),
                "trusted non-Riot publisher classification");
        }

        private static void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Failed: " + name);
        }

        private static void TestDownloadTransitions()
        {
            var state = new DownloadSession();
            Require(state.TryStart(), "initial download starts");
            var generation = state.Generation;
            Require(!state.TryStart(), "double click cannot start a second download");
            Require(state.Report(generation, DownloadState.Downloading), "download progress accepted");
            Require(state.Report(generation, DownloadState.Verifying), "verification entered");
            Require(!state.CanPause && !state.TryStart(), "verification blocks pause and duplicate downloads");
            Require(!state.Report(generation, DownloadState.Downloading), "late timer cannot regress verification");
            state.RequestStop();
            Require(!state.Report(generation, DownloadState.Verifying), "stop rejects queued progress");
            state.Finish(DownloadState.Paused);
            Require(state.TryStart(), "paused download resumes");
            Require(!state.Report(generation, DownloadState.Downloading), "previous generation progress ignored");
            state.Finish(DownloadState.Ready);
            Require(!state.TryStart(), "ready installer remains available");
            Require(!state.Report(state.Generation, DownloadState.Downloading), "finished progress ignored");
            state.Finish(DownloadState.Failed);
            Require(state.TryStart(), "failed install can be downloaded again");
        }

        private static void TestConfigurationLockRoundTrip()
        {
            // Locking rewrites a deny-write ACL, which only an elevated process may do; the shipped app
            // declares requireAdministrator, so report the elevation problem instead of an opaque crash.
            if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("ACL lock round trip requires an elevated (administrator) process. " +
                    "Run this test suite from an elevated prompt, matching the app manifest's requireAdministrator.");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rift-lock-test-" + Guid.NewGuid().ToString("N") + ".yaml");
            var principalsMethod = typeof(LocaleService).GetMethod("LockedPrincipals", BindingFlags.NonPublic | BindingFlags.Static)!;
            var lockedRights = (System.Security.AccessControl.FileSystemRights)typeof(LocaleService)
                .GetField("LockedRights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            System.IO.File.WriteAllText(path, "locale: zh_CN");
            try
            {
                LocaleService.LockForTest(path);
                // BOTH protections are required: the read-only attribute is what stops an elevated
                // client that is exempt from deny ACEs, and the ACE stops ordinary writers.
                Require((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0,
                    "lock sets the read-only attribute");
                Require(DenyRuleCount(path) > 0, "lock installs deny rules");

                var principals = (System.Security.Principal.SecurityIdentifier[])principalsMethod.Invoke(null, null)!;
                Require(principals.Length >= 4, "lock covers the current user, SYSTEM, Administrators and Users");
                var denyRules = System.IO.File.GetAccessControl(path, System.Security.AccessControl.AccessControlSections.Access)
                    .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                    .OfType<System.Security.AccessControl.FileSystemAccessRule>()
                    .Where(rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny)
                    .ToArray();
                foreach (var principal in principals)
                    Require(denyRules.Any(rule => principal.Equals(rule.IdentityReference) &&
                            (rule.FileSystemRights & lockedRights) == lockedRights),
                        "deny write rule installed for " + principal.Value);

                // The whole point of the lock: a normal writer must actually be refused.
                Require(!CanWrite(path), "locked configuration rejects writes");

                // Regression guard: unlocking a locked file must succeed and clear BOTH protections.
                // An earlier build removed the read-only flag without it, which let the client rewrite
                // the language; the build before that could not clear it at all (无法解除锁定).
                LocaleService.UnlockForTest(path);
                Require((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) == 0, "unlock clears the read-only attribute");
                Require(CanWrite(path), "unlock restores write access");
                var afterUnlock = System.IO.File.GetAccessControl(path, System.Security.AccessControl.AccessControlSections.Access)
                    .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                    .OfType<System.Security.AccessControl.FileSystemAccessRule>()
                    .Where(rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny)
                    .ToArray();
                Require(!afterUnlock.Any(rule => principals.Any(principal => principal.Equals(rule.IdentityReference))), "unlock removes every managed deny rule");
                Require(DenyRuleCount(path) == 0, "no deny rules survive unlock");

                // Apply sequence: unlock, overwrite in place, lock again — no backup copy kept.
                LocaleService.LockForTest(path);
                LocaleService.UnlockForTest(path);
                System.IO.File.WriteAllText(path, "locale: zh_CN");
                LocaleService.LockForTest(path);
                Require((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0 &&
                        DenyRuleCount(path) > 0 && System.IO.File.ReadAllText(path) == "locale: zh_CN",
                    "apply-then-lock leaves zh_CN locked with both protections");
                Console.WriteLine("Configuration ACL lock/unlock round trip passed for " + principals.Length + " principals.");
            }
            finally
            {
                try { LocaleService.UnlockForTest(path); } catch { }
                System.IO.File.Delete(path);
            }
        }

        private static void TestFreshSignatureRejectsTampering()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rift-signature-test-" + Guid.NewGuid().ToString("N") + ".exe");
            var source = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe");
            System.IO.File.Copy(source, path);
            try
            {
                var verifier = new SignatureVerifier();
                Require(verifier.Verify(path).IsTrusted, "signed fixture is initially trusted");
                var timestamp = System.IO.File.GetLastWriteTimeUtc(path);
                using (var stream = System.IO.File.Open(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite))
                {
                    stream.Position = 0x400;
                    var value = stream.ReadByte();
                    stream.Position = 0x400;
                    stream.WriteByte((byte)(value ^ 0xFF));
                }
                System.IO.File.SetLastWriteTimeUtc(path, timestamp);
                Require(!verifier.Verify(path, useCache: false).IsTrusted, "installation revalidates bytes even when file size and timestamp are unchanged");
                Console.WriteLine("Fresh signature tamper rejection passed.");
            }
            finally { System.IO.File.Delete(path); }
        }
    }
}
