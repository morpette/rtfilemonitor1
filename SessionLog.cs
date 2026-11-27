using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RtFileMonitor;

internal sealed record TimelineEntry(int Line, DateTimeOffset? Timestamp, string RecordType, string PayloadType,
    string Category, string Title, string Summary, string Raw)
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool HasSummary => !string.IsNullOrEmpty(Summary);
    public bool HasTimestamp => Timestamp.HasValue;
    public string Time => Timestamp?.ToLocalTime().ToString("G") ?? "";

    public string PrettyJson
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Raw);
                return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            }
            catch (JsonException)
            {
                return Raw;
            }
        }
    }

    public static TimelineEntry Read(int line, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var item = document.RootElement;
            var payload = Field(item, "payload");
            string type = Text(item, "type");
            string subtype = Text(payload, "type");
            string role = Text(payload, "role");
            string category;
            string title;
            string summary = "";
            switch (type, subtype)
            {
                case ("response_item", "message"):
                    category = role switch
                    {
                        "user" or "assistant" => "Conversation",
                        _ => "Configuration"
                    };
                    title = Label(role.Length == 0 ? "message" : role);
                    if (category == "Conversation")
                    {
                        var content = Field(payload, "content");
                        if (content.ValueKind == JsonValueKind.Array)
                            summary = string.Join("\n\n", content.EnumerateArray()
                                .Select(part => Text(part, "text")).Where(text => text.Length > 0));
                    }
                    break;
                case ("session_meta", _):
                    category = "Session";
                    title = "Session started";
                    summary = Text(payload, "cwd");
                    break;
                case ("event_msg", "task_started" or "task_complete"):
                    category = "Session";
                    title = subtype == "task_started" ? "Task started" : "Task completed";
                    break;
                case ("turn_context" or "world_state", _) or ("event_msg", "thread_settings_applied")
                    or ("response_item", "additional_tools"):
                    category = "Configuration";
                    title = Label(subtype.Length == 0 ? type : subtype);
                    break;
                case ("token_usage_record", _) or ("event_msg", "token_count"):
                    category = "Usage";
                    title = "Token usage";
                    var usage = Field(payload, "usage");
                    if (usage.ValueKind == JsonValueKind.Undefined)
                        usage = Field(Field(payload, "info"), "total_token_usage");
                    var total = Field(usage, "total_tokens");
                    if (total.ValueKind == JsonValueKind.Number)
                        summary = total.GetRawText() + " total tokens";
                    break;
                case ("event_msg", "item_completed" or "item_started")
                    when Text(Field(payload, "item"), "type") is "AgentMessage" or "Reasoning" or "UserMessage":
                    var completedType = Text(Field(payload, "item"), "type");
                    category = completedType == "Reasoning" ? "Other" : "Conversation";
                    title = (completedType == "Reasoning" ? "Reasoning" : "Message") +
                        (subtype == "item_completed" ? " completed" : " started");
                    break;
                case ("response_item", "function_call" or "function_call_output" or "custom_tool_call" or
                    "custom_tool_call_output" or "web_search_call" or "local_shell_call")
                    or ("event_msg", "item_completed" or "item_started" or "exec_command_end"):
                    category = "Tools";
                    var tool = Field(payload, "item");
                    var toolType = Text(tool, "type");
                    title = Label(toolType.Length == 0 ? subtype : toolType);
                    summary = Text(payload, "name");
                    if (summary.Length == 0)
                        summary = Text(tool, "name");
                    break;
                default:
                    category = "Other";
                    title = Label(subtype.Length == 0 ? type : subtype);
                    break;
            }

            string timestamp = Text(item, "timestamp");
            if (timestamp.Length == 0)
                timestamp = Text(payload, "timestamp");
            return new(line, DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed : null,
                type, subtype, category, title, summary, raw);
        }
        catch (JsonException)
        {
            return new(line, null, "", "", "Invalid JSON", "Malformed JSONL record", "Malformed JSONL record", raw);
        }
    }

    private static JsonElement Field(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;

    private static string Text(JsonElement item, string key)
    {
        var value = Field(item, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static string Label(string key) => key.Length == 0
        ? "JSON record"
        : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');
}

internal sealed class SessionLog
{
    private readonly MemoryStream partial = new();
    private FileIdentity? identity;
    private long offset;
    private byte[] anchor = [];
    private int lineNumber;
    public bool NeedsReset { get; private set; }
    public TimelineEntry? Preview { get; private set; }

    public List<TimelineEntry> ReadNew(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var current = FileIdentity.Read(stream.SafeFileHandle);
        long end = stream.Length;
        if (current != identity || end < offset || !MatchesAnchor(stream))
        {
            NeedsReset = true;
            identity = current;
            offset = 0;
            anchor = [];
            lineNumber = 0;
            partial.SetLength(0);
        }

        var entries = new List<TimelineEntry>();
        var buffer = new byte[64 * 1024];
        stream.Position = offset;
        while (stream.Position < end)
        {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - stream.Position));
            if (count == 0)
                break;
            int start = 0;
            for (int index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n')
                    continue;
                partial.Write(buffer, start, index - start);
                string raw = Encoding.UTF8.GetString(partial.GetBuffer(), 0, checked((int)partial.Length)).TrimEnd('\r');
                if (++lineNumber == 1)
                    raw = raw.TrimStart('\uFEFF');
                if (!string.IsNullOrWhiteSpace(raw))
                    entries.Add(TimelineEntry.Read(lineNumber, raw));
                partial.SetLength(0);
                start = index + 1;
            }
            partial.Write(buffer, start, count - start);
            offset += count;
            int keep = Math.Min(256, anchor.Length + count);
            var tail = new byte[keep];
            int fromBuffer = Math.Min(count, keep);
            anchor.AsSpan(anchor.Length - (keep - fromBuffer)).CopyTo(tail);
            buffer.AsSpan(count - fromBuffer, fromBuffer).CopyTo(tail.AsSpan(keep - fromBuffer));
            anchor = tail;
        }
        Preview = null;
        if (partial.Length > 0)
        {
            string raw = Encoding.UTF8.GetString(partial.GetBuffer(), 0, checked((int)partial.Length)).TrimEnd('\r');
            if (lineNumber == 0)
                raw = raw.TrimStart('\uFEFF');
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var candidate = TimelineEntry.Read(lineNumber + 1, raw);
                if (candidate.Category != "Invalid JSON")
                    Preview = candidate;
            }
        }
        return entries;
    }

    public void ResetAcknowledged() => NeedsReset = false;

    public void RestartRead()
    {
        identity = null;
    }

    private bool MatchesAnchor(FileStream stream)
    {
        if (anchor.Length == 0)
            return true;
        stream.Position = offset - anchor.Length;
        Span<byte> bytes = stackalloc byte[anchor.Length];
        return stream.ReadAtLeast(bytes, bytes.Length, false) == bytes.Length
            && bytes.SequenceEqual(anchor);
    }

    private readonly record struct FileIdentity(uint Volume, uint High, uint Low)
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Information
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);

        public static FileIdentity Read(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new IOException("Cannot identify the session file.", new Win32Exception());
            return new(info.Volume, info.IndexHigh, info.IndexLow);
        }
    }
}
