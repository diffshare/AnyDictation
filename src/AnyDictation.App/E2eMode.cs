using System;
using System.Collections.Generic;
using System.IO;

namespace AnyDictation.App;

// 設定 GUI の専用起動。本番の保存先や周辺機能へ接続する前に境界を確定する。
internal static class E2eMode
{
    public static bool Enabled => DataDir != null;
    public static string? DataDir { get; private set; }
    public static string InstanceId { get; private set; } = "";

    public static bool Configure(string[] args)
    {
        if (args.Length == 0) return true;
        const string prefix = "--e2e-data-dir=";
        if (args.Length != 1 || !args[0].StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            var dir = Path.GetFullPath(args[0][prefix.Length..]);
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AnyDictation.E2E"));
            if (!Directory.Exists(dir) || !string.Equals(Path.GetDirectoryName(dir), root, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(dir), "N", out var id)) return false;
            for (var part = new DirectoryInfo(dir); part != null; part = part.Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(dir, ".anydictation-e2e");
            if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(marker) != id.ToString("N")) return false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return false;
            DataDir = dir;
            InstanceId = id.ToString("N");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class E2eCredentials : ICredentialStore
{
    readonly Dictionary<string, string> _values = new();
    public string? Read(string target) => _values.GetValueOrDefault(target);
    public void Write(string target, string secret) => _values[target] = secret;
    public void Delete(string target) => _values.Remove(target);
}
