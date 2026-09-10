using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LoLHelper.Services
{
    internal sealed class LocaleService
    {
        private const string RelativeSettingsPath = @"Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml";
        private static readonly FileSystemRights LockedRights = FileSystemRights.WriteData |
            FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete;

        public string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), RelativeSettingsPath);

        public Task<LocaleStatus> GetStatusAsync() => Task.Run(() =>
        {
            if (!File.Exists(SettingsPath)) return new LocaleStatus();
            var text = ReadTextPreservingEncoding(SettingsPath).Text;
            var security = File.GetAccessControl(SettingsPath, AccessControlSections.Access);
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
            return new LocaleStatus
            {
                Exists = true,
                IsChinese = Regex.IsMatch(text, "(?m)^\\s*default_locale\\s*:\\s*['\\\"]?zh_CN['\\\"]?\\s*(?:#.*)?$") &&
                            Regex.IsMatch(text, "(?m)^\\s*locale\\s*:\\s*['\\\"]?zh_CN['\\\"]?\\s*(?:#.*)?$"),
                // Locked only when BOTH protections are in place: the read-only attribute, which even
                // a privileged client honours, and a deny-write ACE for every principal a launcher
                // could run as. A file missing either one is reported unlocked so applying again
                // repairs it, and a lock left by an older build is repaired rather than trusted.
                IsLocked = (File.GetAttributes(SettingsPath) & FileAttributes.ReadOnly) != 0 &&
                    LockedPrincipals().All(sid => rules.Any(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        sid.Equals(rule.IdentityReference) &&
                        (rule.FileSystemRights & LockedRights) == LockedRights))
            };
        });

        /// <summary>
        /// Applies zh_CN without keeping a backup copy: unlock, overwrite in place, lock again. Unlock
        /// runs first because the deny ACE blocks the writes the patch needs, and the read-only
        /// attribute is re-applied last so the client cannot rewrite the finished file.
        /// </summary>
        public Task ApplyChineseAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SettingsPath;
                if (!File.Exists(path)) throw new FileNotFoundException("未找到英雄联盟产品设置文件，请先安装并启动一次游戏。", path);
                EnsureSafeTarget(path);

                var temp = path + ".LoLHelper.tmp";
                try
                {
                    Unlock(path);
                    var document = ReadTextPreservingEncoding(path);
                    var updated = YamlLocaleEditor.Apply(document.Text);
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteText(temp, updated, document.Encoding, document.HasBom);
                    // The destination stays writable here, so no Delete right is needed to replace it.
                    File.Copy(temp, path, true);
                }
                finally
                {
                    TryDelete(temp);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Lock(path);
            }, cancellationToken);
        }

        public Task UnlockAsync() => Task.Run(() =>
        {
            if (!File.Exists(SettingsPath)) throw new FileNotFoundException("未找到产品设置文件。", SettingsPath);
            EnsureSafeTarget(SettingsPath);
            Unlock(SettingsPath);
        });

        private static void EnsureSafeTarget(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var expectedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games")) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("配置文件路径不安全，操作已取消。");
            var file = new FileInfo(fullPath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("配置文件是重解析点，操作已取消。");
            var current = file.Directory;
            while (current != null && !string.Equals(current.FullName, expectedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("配置路径包含重解析点，操作已取消。");
                current = current.Parent;
            }
        }

        // Test hooks: the round trip rewrites a deny-write ACL, which needs an elevated process.
        internal static void LockForTest(string path) => Lock(path);
        internal static void UnlockForTest(string path) => Unlock(path);

        private static void Lock(string path)
        {
            // Set the read-only attribute FIRST, then install the deny ACE. The order matters:
            // setting an attribute needs WriteAttributes, which the deny rule covers.
            // BOTH protections are required. The deny ACE alone is not enough: a client running
            // elevated with SeRestorePrivilege (or backup semantics) is exempt from deny ACEs and
            // silently rewrites the language. The read-only attribute is enforced when the file is
            // opened, so it stops that client too — which is why the lock must keep it.
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            // Deny every principal the client or the launcher can run as. Denying only the current
            // user left the file writable for an elevated client, SYSTEM services and the
            // Administrators group.
            var security = File.GetAccessControl(path, AccessControlSections.Access);
            RemoveManagedRules(security);
            foreach (var sid in LockedPrincipals())
                security.AddAccessRule(new FileSystemAccessRule(sid, LockedRights, AccessControlType.Deny));
            // WriteDac is deliberately not denied, so unlocking stays possible.
            File.SetAccessControl(path, security);
        }

        private static void Unlock(string path)
        {
            // Remove the deny ACE FIRST: while it is installed, WriteAttributes is refused, so the
            // read-only attribute it protects could never be cleared. Unlocking therefore needs
            // WRITE_DAC (which the rule does not deny) and then the attribute write.
            var security = File.GetAccessControl(path, AccessControlSections.Access);
            if (RemoveManagedRules(security)) File.SetAccessControl(path, security);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        private static SecurityIdentifier[] LockedPrincipals()
        {
            var identifiers = new List<SecurityIdentifier>();
            void Add(SecurityIdentifier? sid)
            {
                if (sid == null || identifiers.Any(existing => existing.Equals(sid))) return;
                identifiers.Add(sid);
            }
            Add(WindowsIdentity.GetCurrent().User);
            Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            Add(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
            return identifiers.ToArray();
        }

        private static bool RemoveManagedRules(FileSecurity security)
        {
            var managed = LockedPrincipals();
            var changed = false;
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
            foreach (var rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Deny) continue;
                if ((rule.FileSystemRights & LockedRights) != LockedRights) continue;
                if (!(rule.IdentityReference is SecurityIdentifier ruleSid) || !managed.Any(sid => sid.Equals(ruleSid))) continue;
                security.RemoveAccessRuleSpecific(rule);
                changed = true;
            }
            return changed;
        }

        private sealed class TextDocument
        {
            public string Text { get; set; } = string.Empty;
            public Encoding Encoding { get; set; } = new UTF8Encoding(false);
            public bool HasBom { get; set; }
        }

        private static TextDocument ReadTextPreservingEncoding(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Encoding encoding;
            var offset = 0;
            var hasBom = false;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { encoding = new UTF8Encoding(true, true); offset = 3; hasBom = true; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { encoding = new UnicodeEncoding(false, true, true); offset = 2; hasBom = true; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { encoding = new UnicodeEncoding(true, true, true); offset = 2; hasBom = true; }
            else encoding = new UTF8Encoding(false, true);
            return new TextDocument { Text = encoding.GetString(bytes, offset, bytes.Length - offset), Encoding = encoding, HasBom = hasBom };
        }

        private static void WriteText(string path, string text, Encoding encoding, bool withBom)
        {
            var body = encoding.GetBytes(text);
            var preamble = withBom ? encoding.GetPreamble() : Array.Empty<byte>();
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                if (preamble.Length > 0) stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush(true);
            }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    internal static class YamlLocaleEditor
    {
        private static readonly Regex KeyPattern = new Regex(@"^(?<indent>\s*)(?<key>[A-Za-z0-9_]+)\s*:\s*(?<value>.*)$", RegexOptions.Compiled);

        public static string Apply(string text)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var trailing = text.EndsWith(newline, StringComparison.Ordinal);
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (trailing && lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            var localeSection = FindSection(lines, "locale_data");
            var settingsSection = FindSection(lines, "settings");
            SetScalar(lines, localeSection, "default_locale", "\"zh_CN\"");
            AddAvailableLocale(lines, localeSection);
            settingsSection = FindSection(lines, "settings");
            SetScalar(lines, settingsSection, "locale", "\"zh_CN\"");
            return string.Join(newline, lines) + (trailing ? newline : string.Empty);
        }

        private static (int Start, int End, int Indent) FindSection(List<string> lines, string name)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var match = KeyPattern.Match(lines[i]);
                if (!match.Success || match.Groups["key"].Value != name || !string.IsNullOrWhiteSpace(StripComment(match.Groups["value"].Value))) continue;
                var indent = match.Groups["indent"].Value.Length;
                var end = lines.Count;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith("#")) continue;
                    // Sequence items ("- value") may sit one column deeper than their key and still
                    // belong to this section, so they never terminate it.
                    var trimmed = lines[j].TrimStart();
                    var nextIndent = lines[j].Length - trimmed.Length;
                    if (IsSequenceItem(trimmed))
                    {
                        if (nextIndent <= indent) { end = j; break; }
                        continue;
                    }
                    if (nextIndent <= indent) { end = j; break; }
                }
                return (i, end, indent);
            }
            throw new InvalidDataException("YAML 中缺少 " + name + " 配置段，未进行修改。");
        }

        private static void SetScalar(List<string> lines, (int Start, int End, int Indent) section, string key, string value)
        {
            for (var i = section.Start + 1; i < section.End; i++)
            {
                var match = KeyPattern.Match(lines[i]);
                if (!match.Success || match.Groups["key"].Value != key || match.Groups["indent"].Value.Length <= section.Indent) continue;
                var raw = match.Groups["value"].Value;
                var commentIndex = FindComment(raw);
                var comment = commentIndex >= 0 ? " " + raw.Substring(commentIndex).TrimStart() : string.Empty;
                lines[i] = match.Groups["indent"].Value + key + ": " + value + comment;
                return;
            }
            throw new InvalidDataException("YAML 中缺少 " + key + "，未进行修改。");
        }

        private static void AddAvailableLocale(List<string> lines, (int Start, int End, int Indent) section)
        {
            for (var i = section.Start + 1; i < section.End; i++)
            {
                var match = KeyPattern.Match(lines[i]);
                if (!match.Success || match.Groups["key"].Value != "available_locales" || match.Groups["indent"].Value.Length <= section.Indent) continue;
                var raw = match.Groups["value"].Value;
                var open = raw.IndexOf('[');
                var close = raw.LastIndexOf(']');
                if (open >= 0 && close > open)
                {
                    var inner = raw.Substring(open + 1, close - open - 1);
                    if (IsLastLocale(inner, "zh_CN")) return;
                    // Drop any existing entry first so the locale ends up in one place: the end.
                    var rebuilt = LocaleEntryPattern("zh_CN").Replace(inner, string.Empty).Trim().Trim(',').Trim();
                    var inline = match.Groups["indent"].Value + "available_locales: [" +
                        (rebuilt.Length == 0 ? "\"zh_CN\"" : rebuilt + ", \"zh_CN\"") + "]" + raw.Substring(close + 1);
                    lines[i] = inline;
                    return;
                }

                // Block sequence: confirm the list first, then append. A missing indent on an entry
                // (for example a "- \"en_US\"" left at column zero by an earlier edit) must not be
                // mistaken for the end of the section, and following keys such as default_locale are
                // only a boundary once the whole list has been seen.
                var keyIndent = match.Groups["indent"].Value.Length;
                var shallowest = -1;
                for (var j = i + 1; j < section.End; j++)
                {
                    var trimmed = lines[j].TrimStart();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                    var lineIndent = lines[j].Length - trimmed.Length;
                    if (!IsSequenceItem(trimmed))
                    {
                        // Stop at the next key, which is shallower than the sequence. The sequence is
                        // NOT bounded by keyIndent: the real product_settings.yaml holds its items at
                        // the very same indent as the key, and a deeper zh_CN entry was the stray one.
                        if (shallowest >= 0 && lineIndent < shallowest) break;
                        if (shallowest < 0 && IsSectionKey(trimmed, keyIndent)) break;
                        continue;
                    }
                    if (shallowest < 0 || lineIndent < shallowest) shallowest = lineIndent;
                }
                if (shallowest < 0) throw new InvalidDataException("available_locales 中没有可追加的列表项，未进行修改。");
                // Normalise every entry to the shallowest indentation present. That keeps a uniformly
                // deeper list in its own style and pulls back a single entry left deeper, so the list
                // ends up consistent and the appended locale matches its siblings.
                var prefix = new string(' ', shallowest);
                // Rewrite entries to the canonical indentation, drop an existing zh_CN, and remember
                // where to append. Deleting while walking needs the index pulled back each time.
                var insertAt = i + 1;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    var trimmed = lines[j].TrimStart();
                    var lineIndent = lines[j].Length - trimmed.Length;
                    if (!IsSequenceItem(trimmed))
                    {
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                        if (lineIndent < shallowest) break;
                        if (IsSectionKey(trimmed, keyIndent) && lineIndent <= shallowest) break;
                        continue;
                    }
                    if (ContainsLocale(trimmed, "zh_CN")) { lines.RemoveAt(j); j--; continue; }
                    lines[j] = prefix + trimmed;
                    insertAt = j + 1;
                }
                lines.Insert(insertAt, prefix + "- \"zh_CN\"");
                return;
            }
            throw new InvalidDataException("YAML 中缺少 available_locales，未进行修改。");
        }

        private static bool IsSequenceItem(string trimmed) =>
            trimmed.Length > 0 && trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' ');

        // A sibling key of available_locales, used to bound the list when its items sit at the very
        // same indentation as the key itself (as the real product_settings.yaml does).
        private static bool IsSectionKey(string trimmed, int keyIndent) =>
            trimmed.Length - trimmed.TrimStart().Length <= keyIndent &&
            Regex.IsMatch(trimmed, "^[A-Za-z0-9_]+\\s*:");

        private static bool ContainsLocale(string value, string locale) => LocaleEntryPattern(locale).IsMatch(value);

        private static bool IsLastLocale(string value, string locale)
        {
            var match = LocaleEntryPattern(locale).Match(value);
            return match.Success && value.Substring(match.Index + match.Length).Trim().Length == 0;
        }

        // Matches a single quoted or bare locale entry with the separator that follows it, so removing
        // a match leaves the rest of the inline list well formed.
        private static Regex LocaleEntryPattern(string locale) => new Regex(
            "['\\\"]?" + Regex.Escape(locale) + "['\\\"]?\\s*(?:,\\s*)?",
            RegexOptions.IgnoreCase);

        private static string StripComment(string value)
        {
            var index = FindComment(value);
            return index >= 0 ? value.Substring(0, index) : value;
        }

        private static int FindComment(string value)
        {
            var single = false; var dbl = false;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '\'' && !dbl) single = !single;
                else if (value[i] == '"' && !single) dbl = !dbl;
                else if (value[i] == '#' && !single && !dbl) return i;
            }
            return -1;
        }
    }
}
