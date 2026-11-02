using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using HarmonyLib;
using Il2CppInterop.Common;

namespace ChainedBeasts.Hybrids;

internal static class NativeHooks
{
    private static Harmony _harmony;
    private static long _base;
    private static Dictionary<string, long> _expected;
    private static readonly List<(MethodBase Target, long Entry)> Installed = new();
    internal static void Initialize(Harmony harmony)
    {
        Verify("GameAssembly.dll", "0deadea3eb9a6ee3bdb69ab7945d4b6dfe40a07be0727edb5cb4dc20c05c3f6e");
        Verify(Path.Combine("ChainedBeasts_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            "d3576ba8280b8a8f18a63ec895aa927617fdf255af03971bd0407cd1c2ee5fbc");
        using var stream = typeof(NativeHooks).Assembly.GetManifestResourceStream("ChainedBeasts.Hybrids.native-targets.json");
        _expected = JsonSerializer.Deserialize<Dictionary<string, long>>(stream);
        using var process = Process.GetCurrentProcess();
        _base = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)).BaseAddress.ToInt64();
        Installed.Clear(); _harmony = harmony;
    }
    private static void Verify(string file, string expected)
    {
        using var stream = File.OpenRead(Path.Combine(Paths.GameRootPath, file));
        using var hash = SHA256.Create();
        var actual = Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
        if (actual != expected) throw new NotSupportedException("Hybrids disabled: uninspected game file " + file + ", SHA256=" + actual);
    }
    private static long Entry(MethodBase target)
    {
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
        if (field == null) return 0;
        var info = (IntPtr)(field.GetValue(null) ?? IntPtr.Zero);
        return info == IntPtr.Zero ? 0 : Marshal.ReadIntPtr(info).ToInt64();
    }
    internal static void Patch(Type owner, Type target, string method, string prefix = null, string postfix = null, string finalizer = null, params Type[] args)
    {
        var original = AccessTools.DeclaredMethod(target, method, args) ?? throw new MissingMethodException(target.FullName, method);
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(original) ?? throw new InvalidOperationException("No native binding for " + original);
        var key = target.Name + "::" + field.Name;
        var entry = Entry(original);
        if (!_expected.TryGetValue(key, out var rva) || entry != _base + rva)
            throw new InvalidOperationException("Unexpected native entry: " + original);
        foreach (var item in Installed)
            if (item.Entry == entry && item.Target != original) throw new InvalidOperationException("Own native alias: " + original);
        CheckAlias(original, entry);
        HarmonyMethod Callback(string name) => name == null ? null : new HarmonyMethod(AccessTools.DeclaredMethod(owner, name) ?? throw new MissingMethodException(owner.FullName, name));
        _harmony.Patch(original, Callback(prefix), Callback(postfix), finalizer: Callback(finalizer));
        Installed.Add((original, entry));
        HybridRuntime.Log?.LogInfo("[Hybrids] Hook " + target.Name + "." + method + " RVA=0x" + rva.ToString("x"));
    }
    private static void CheckAlias(MethodBase original, long entry)
    {
        foreach (var other in Harmony.GetAllPatchedMethods())
            if (other != original && Entry(other) == entry) throw new InvalidOperationException("Another wrapper owns native entry for " + original + ": " + other);
    }
    internal static void Audit() { foreach (var item in Installed) CheckAlias(item.Target, item.Entry); }
}
