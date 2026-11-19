using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// Chooses the SQLite engine. bsm ships one (the native library "e_sqlite3"), but on Linux it needs glibc 2.33 or later:
/// on older systems, such as RHEL 8 and Ubuntu 20.04, it cannot load. There bsm uses the system's libsqlite3 instead,
/// when it is SQLite 3.26 or later (the oldest bsm supports). BSM_SQLITE_LIBRARY names a library to use instead of
/// either.
/// </summary>
internal static class SqliteNativeLibrary
{
    private const string BundledName = "e_sqlite3";

    private static readonly string[] SystemNames = ["libsqlite3.so.0", "libsqlite3.so", "libsqlite3.dylib", "sqlite3"];

    /// <summary>Which library was loaded and why ("built in", "system ...", or the reason none could be).</summary>
    public static string? Description { get; private set; }

    /// <summary>A library path that wins over the built-in engine (tests set it; BSM_SQLITE_LIBRARY otherwise).</summary>
    internal static string? Override { get; set; } = Environment.GetEnvironmentVariable("BSM_SQLITE_LIBRARY") is { Length: > 0 } path ? path : null;

    // The resolver must be in place before anything in this assembly opens a connection, whichever type is used first.
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Picks the native engine before any SQLite call; a library has no earlier hook.")]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != BundledName)
        {
            return IntPtr.Zero;
        }

        if (Override is { } path)
        {
            var handle = NativeLibrary.Load(path);
            Description = $"{path} (BSM_SQLITE_LIBRARY)";
            return handle;
        }

        if (NativeLibrary.TryLoad(name, assembly, searchPath, out var bundled))
        {
            Description = "built in";
            return bundled;
        }

        foreach (var candidate in SystemNames)
        {
            if (!NativeLibrary.TryLoad(candidate, out var system))
            {
                continue;
            }

            var version = LibraryVersion(system);
            if (version >= 3_026_000)
            {
                Description = $"system {candidate} (the built-in engine cannot load here)";
                return system;
            }

            NativeLibrary.Free(system);
            Description = $"the built-in SQLite cannot load here (on Linux it needs glibc 2.33 or later), and the system's {candidate} is SQLite {version / 1_000_000}.{version / 1000 % 1000}, older than the 3.26 bsm needs";
            return IntPtr.Zero;
        }

        Description = "the built-in SQLite cannot load here (on Linux it needs glibc 2.33 or later), and no system libsqlite3 was found: install SQLite 3.26 or later (sqlite-libs or libsqlite3-0), or set BSM_SQLITE_LIBRARY to a libsqlite3 file";
        return IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VersionNumber();

    private static int LibraryVersion(IntPtr library) =>
        NativeLibrary.TryGetExport(library, "sqlite3_libversion_number", out var export) ? Marshal.GetDelegateForFunctionPointer<VersionNumber>(export)() : 0;
}
