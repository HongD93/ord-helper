using System.Text;
using System.Text.Json;

namespace OrdDesktopHost;

internal sealed class CraftUiTrace(string logsDirectory)
{
    private const long MaximumFileBytes = 1_048_576;
    private readonly Dictionary<string, string> lastStateByScope = new(StringComparer.Ordinal);

    internal bool TryWrite(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !ReadText(message, "scope", 16, out var scope) || scope is not ("board" or "advisor") ||
            !ReadText(message, "snapshotStatus", 64, out var snapshotStatus) ||
            !ReadCount(message, "enabled", out var enabled) || !ReadCount(message, "total", out var total) ||
            enabled > total || !message.TryGetProperty("blocked", out var blocked) || blocked.ValueKind != JsonValueKind.Object)
            return false;

        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var blockedTotal = 0;
        foreach (var reason in blocked.EnumerateObject())
        {
            if (reasons.Count >= 24 || !SafeText(reason.Name, 100) || reason.Value.ValueKind != JsonValueKind.Number ||
                !reason.Value.TryGetInt32(out var count) || count < 1 || count > 2000 || !reasons.TryAdd(reason.Name, count))
                return false;
            blockedTotal += count;
        }
        if (blockedTotal > total - enabled) return false;

        var state = new Dictionary<string, object?>
        {
            ["scope"] = scope, ["snapshotStatus"] = snapshotStatus,
            ["enabled"] = enabled, ["total"] = total, ["blocked"] = reasons
        };
        foreach (var name in new[] { "calculating", "connected", "craftRunning" })
        {
            if (!message.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            state[name] = value.GetBoolean();
        }

        long? sequence = null;
        if (message.TryGetProperty("snapshotSequence", out var sequenceValue) && sequenceValue.ValueKind != JsonValueKind.Null)
        {
            if (sequenceValue.ValueKind != JsonValueKind.Number || !sequenceValue.TryGetInt64(out var number) || number < 0) return false;
            sequence = number;
        }
        DateTimeOffset? timestamp = null;
        if (message.TryGetProperty("timestamp", out var timeValue) && timeValue.ValueKind != JsonValueKind.Null)
        {
            if (timeValue.ValueKind != JsonValueKind.String || !timeValue.TryGetDateTimeOffset(out var time)) return false;
            timestamp = time;
        }

        var signature = JsonSerializer.Serialize(state);
        if (lastStateByScope.GetValueOrDefault(scope!) == signature) return true;
        state["timeUtc"] = DateTimeOffset.UtcNow;
        if (sequence.HasValue) state["snapshotSequence"] = sequence.Value;
        if (timestamp.HasValue) state["snapshotTimestamp"] = timestamp.Value;
        var line = JsonSerializer.Serialize(state) + Environment.NewLine;
        try
        {
            Directory.CreateDirectory(logsDirectory);
            var current = Path.Combine(logsDirectory, "craft-ui-state.jsonl");
            if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes)
                File.Move(current, Path.Combine(logsDirectory, "craft-ui-state.previous.jsonl"), overwrite: true);
            File.AppendAllText(current, line, Encoding.UTF8);
            lastStateByScope[scope!] = signature;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SafeText(string value, int maximum) =>
        value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool ReadText(JsonElement message, string name, int maximum, out string? value)
    {
        value = null;
        if (!message.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString();
        return value is not null && SafeText(value, maximum);
    }

    private static bool ReadCount(JsonElement message, string name, out int value)
    {
        value = 0;
        return message.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt32(out value) && value is >= 0 and <= 2000;
    }
}
