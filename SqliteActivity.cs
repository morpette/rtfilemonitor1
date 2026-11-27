using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RtFileMonitor;

internal static class SqliteActivity
{
    internal sealed record Snapshot(string Schema, Dictionary<string, string>? Keyed, Dictionary<string, int> Rows);
    internal sealed record Change(DateTime Observed, string Table, int Added, int? Updated, int Removed);

    internal sealed record Column(string Name, string DeclaredType);
    internal sealed record SchemaObject(string Type, string Name, IReadOnlyList<Column> Columns, string? Error);

    internal static async Task WatchCommits(string root, string[] names, Action<string[]> changed, CancellationToken stop)
    {
        var probes = names.Select(name => new CommitProbe(Path.Combine(root, name))).ToArray();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            do
            {
                var active = new List<string>();
                for (int i = 0; i < probes.Length; i++)
                {
                    stop.ThrowIfCancellationRequested();
                    if (probes[i].HasChanges())
                        active.Add(names[i]);
                }
                if (active.Count > 0)
                    changed(active.ToArray());
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        finally
        {
            foreach (var probe in probes)
                probe.Dispose();
        }
    }

    private sealed class CommitProbe(string path) : IDisposable
    {
        // SQLite data_version values can only be compared on the same connection.
        private Database? database;
        private long? version;
        private bool wasMissing;

        public bool HasChanges()
        {
            if (!File.Exists(path))
            {
                Dispose();
                version = null;
                wasMissing = true;
                return false;
            }

            try
            {
                database ??= new Database(path);
                using var query = database.Query("PRAGMA main.data_version");
                if (!query.Next())
                    return false;
                long current = Native.sqlite3_column_int64(query.Handle, 0);
                bool changed = wasMissing || (version.HasValue && version != current);
                version = current;
                wasMissing = false;
                return changed;
            }
            catch (IOException)
            {
                // A busy database can be read again on the next poll.
                return false;
            }
        }

        public void Dispose()
        {
            database?.Dispose();
            database = null;
        }
    }

    internal static IEnumerable<SchemaObject> TablesAndViews(string path)
    {
        using var database = new Database(path);
        using var query = database.Query("SELECT type, name FROM sqlite_master WHERE type IN ('table', 'view') " +
            "ORDER BY type, name COLLATE NOCASE, name");
        while (query.Next())
        {
            string name = query.Text(1);
            var columns = new List<Column>();
            string? error = null;
            try
            {
                string argument = name.Replace("'", "''");
                using var info = database.Query($"PRAGMA main.table_xinfo('{argument}')");
                while (info.Next())
                    columns.Add(new Column(info.Text(1), info.Text(2)));
            }
            catch (IOException failure)
            {
                error = failure.Message;
            }
            yield return new SchemaObject(query.Text(0), name, columns, error);
        }
    }

    internal const int PageSize = 100;

    internal static (List<string[]> Rows, bool HasMore) ReadPage(string path, SchemaObject table, long offset)
    {
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
        using var database = new Database(path);
        string columns = string.Join(", ", table.Columns.Select(column => Quote(column.Name)));
        using var query = database.Query($"SELECT {columns} FROM main.{Quote(table.Name)} LIMIT {PageSize + 1} OFFSET {offset}");
        var rows = new List<string[]>();
        while (query.Next())
        {
            if (rows.Count == PageSize)
                return (rows, true);
            var row = new string[table.Columns.Count];
            for (int i = 0; i < row.Length; i++)
            {
                int type = Native.sqlite3_column_type(query.Handle, i);
                if (type == 5)
                    row[i] = "NULL";
                else if (type == 4)
                    row[i] = $"BLOB ({Native.sqlite3_column_bytes(query.Handle, i)} bytes)";
                else
                {
                    IntPtr value = Native.sqlite3_column_text(query.Handle, i);
                    int length = Native.sqlite3_column_bytes(query.Handle, i);
                    row[i] = Marshal.PtrToStringUTF8(value, Math.Min(length, 4096)) + (length > 4096 ? "…" : "");
                }
            }
            rows.Add(row);
        }
        return (rows, false);
    }

    internal static string[] Tables(string path)
    {
        using var database = new Database(path);
        using var query = database.Query("SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT GLOB 'sqlite_*' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%' ORDER BY name");
        var tables = new List<string>();
        while (query.Next())
            tables.Add(query.Text(0));
        return tables.ToArray();
    }

    internal static Snapshot Read(string path, string table, CancellationToken stop)
    {
        using var database = new Database(path);
        using (var begin = database.Query("BEGIN"))
            begin.Next();
        string schema;
        using (var catalog = database.Query("SELECT name, sql FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT GLOB 'sqlite_*' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%'"))
        {
            do
            {
                if (!catalog.Next())
                    throw new IOException("The selected table is unavailable. Reopen the database to refresh its table list.");
            }
            while (!string.Equals(catalog.Text(0), table, StringComparison.Ordinal));
            schema = catalog.Text(1);
        }

        string quotedTable = "\"" + table.Replace("\"", "\"\"") + "\"";
        var primaryKey = new HashSet<string>(StringComparer.Ordinal);
        using (var info = database.Query($"PRAGMA table_info({quotedTable})"))
        {
            while (info.Next())
            {
                if (Native.sqlite3_column_int64(info.Handle, 5) > 0)
                    primaryKey.Add(info.Text(1));
            }
        }

        using var query = database.Query($"SELECT * FROM main.{quotedTable} LIMIT 100001");
        int columns = Native.sqlite3_column_count(query.Handle);
        bool[] keyColumns = Enumerable.Range(0, columns)
            .Select(index => primaryKey.Contains(Marshal.PtrToStringUTF8(Native.sqlite3_column_name(query.Handle, index))!))
            .ToArray();
        Dictionary<string, string>? keyed = primaryKey.Count > 0 ? new(StringComparer.Ordinal) : null;
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        using var rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var keyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        int rowCount = 0;
        long bytesRead = 0;
        while (query.Next())
        {
            stop.ThrowIfCancellationRequested();
            if (++rowCount > 100_000)
                throw new IOException("This table exceeds the 100,000-row monitoring limit.");
            bool nullKey = false;
            for (int index = 0; index < columns; index++)
            {
                int type = Native.sqlite3_column_type(query.Handle, index);
                bool isKey = keyed is not null && keyColumns[index];
                nullKey |= isKey && type == 5;
                buffer[0] = (byte)type;
                rowHash.AppendData(buffer, 0, 1);
                if (isKey)
                    keyHash.AppendData(buffer, 0, 1);
                if (type == 5)
                    continue;
                if (type is 1 or 2)
                {
                    long value = type == 1 ? Native.sqlite3_column_int64(query.Handle, index)
                        : BitConverter.DoubleToInt64Bits(Native.sqlite3_column_double(query.Handle, index));
                    BitConverter.TryWriteBytes(buffer.AsSpan(), value);
                    Append(8);
                    continue;
                }

                IntPtr data = Native.sqlite3_column_blob(query.Handle, index);
                int length = Native.sqlite3_column_bytes(query.Handle, index);
                bytesRead += length;
                if (bytesRead > 64 * 1024 * 1024)
                    throw new IOException("This table exceeds the 64 MiB monitoring limit.");
                BitConverter.TryWriteBytes(buffer.AsSpan(), length);
                Append(4);
                for (int offset = 0; offset < length; offset += buffer.Length)
                {
                    stop.ThrowIfCancellationRequested();
                    int count = Math.Min(buffer.Length, length - offset);
                    Marshal.Copy(data + offset, buffer, 0, count);
                    Append(count);
                }

                void Append(int count)
                {
                    rowHash.AppendData(buffer, 0, count);
                    if (isKey)
                        keyHash.AppendData(buffer, 0, count);
                }
            }
            string row = Convert.ToHexString(rowHash.GetHashAndReset());
            rows[row] = rows.GetValueOrDefault(row) + 1;
            if (keyed is not null && (nullKey || !keyed.TryAdd(Convert.ToHexString(keyHash.GetHashAndReset()), row)))
                keyed = null;
        }
        return new(schema, keyed, rows);
    }

    internal static Change Compare(string table, Snapshot before, Snapshot after)
    {
        if (before.Keyed is { } oldKeys && after.Keyed is { } newKeys)
        {
            int added = 0, updated = 0;
            foreach (var (key, row) in newKeys)
            {
                if (!oldKeys.TryGetValue(key, out string? oldRow))
                    added++;
                else if (row != oldRow)
                    updated++;
            }
            return new(DateTime.Now, table, added, updated, oldKeys.Count - (newKeys.Count - added));
        }
        return new(DateTime.Now, table, Difference(after.Rows, before.Rows), null, Difference(before.Rows, after.Rows));
    }

    private static int Difference(Dictionary<string, int> left, Dictionary<string, int> right) =>
        left.Sum(pair => Math.Max(0, pair.Value - right.GetValueOrDefault(pair.Key)));

    private sealed class Database : IDisposable
    {
        private readonly IntPtr handle;

        public Database(string path)
        {
            int result = Native.sqlite3_open_v2(path, out handle, 1, IntPtr.Zero);
            if (result == 0)
            {
                Native.sqlite3_busy_timeout(handle, 250);
                return;
            }
            var error = Error(result);
            Dispose();
            throw error;
        }

        public Query Query(string sql)
        {
            int result = Native.sqlite3_prepare_v2(handle, sql, -1, out IntPtr statement, IntPtr.Zero);
            return result == 0 ? new(this, statement) : throw Error(result);
        }

        public IOException Error(int result) => new($"SQLite could not read this database (error {result}).");
        public void Dispose() => Native.sqlite3_close(handle);
    }

    private sealed class Query(Database database, IntPtr handle) : IDisposable
    {
        public IntPtr Handle => handle;
        public bool Next() => Native.sqlite3_step(handle) switch
        {
            100 => true,
            101 => false,
            int result => throw database.Error(result)
        };
        public string Text(int index) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(handle, index)) ?? "";
        public void Dispose() => Native.sqlite3_finalize(handle);
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
    }
}
