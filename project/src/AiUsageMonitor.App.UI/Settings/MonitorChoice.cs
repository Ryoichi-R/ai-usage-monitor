namespace AiUsageMonitor.App.UI.Settings;

// Phase 2 port of the WPF App's MonitorChoice (unchanged behavior).

public sealed record MonitorChoice(
    string? DeviceName,
    string? StableId,
    string DisplayName,
    bool IsDisconnected = false)
{
    public const string AutomaticDisplayName = "自動（プライマリ）";
    public const string DisconnectedDisplayName = "保存済みのモニタ（未接続のためプライマリに表示中）";

    public static MonitorChoice Automatic { get; } = new(null, null, AutomaticDisplayName);

    public override string ToString() => DisplayName;

    public static MonitorChoice ForScreen(string deviceName, string? stableId, bool primary) =>
        new(deviceName, stableId, deviceName + (primary ? "（プライマリ）" : string.Empty));

    /// <summary>
    /// 保存値に対応する選択肢を返す。固有IDが保存されていれば固有IDだけで照合し、
    /// 未接続なら設定を保存し直しても失われないよう未接続の選択肢を返す。
    /// 固有IDのない旧設定はdevice名で照合し、見つからなければ自動へ戻す
    /// （DISPLAYnは振り直されるため、未接続の旧device名は保持しない）。
    /// </summary>
    public static MonitorChoice Select(
        IReadOnlyList<MonitorChoice> connected,
        string? savedDeviceName,
        string? savedStableId)
    {
        if (!string.IsNullOrWhiteSpace(savedStableId))
        {
            return connected.FirstOrDefault(choice => string.Equals(
                    choice.StableId,
                    savedStableId,
                    StringComparison.OrdinalIgnoreCase))
                ?? new MonitorChoice(savedDeviceName, savedStableId, DisconnectedDisplayName, IsDisconnected: true);
        }
        if (!string.IsNullOrWhiteSpace(savedDeviceName))
        {
            MonitorChoice? byName = connected.FirstOrDefault(choice => string.Equals(
                choice.DeviceName,
                savedDeviceName,
                StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }
        return Automatic;
    }
}
