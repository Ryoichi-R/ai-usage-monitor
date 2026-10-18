using System.ComponentModel;
using System.Runtime.CompilerServices;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.UI.Settings;

// WPF版SettingsWindowの内部型をPhase 2で分離したもの（挙動は同じ）。

public sealed record AnchorChoice(PlacementAnchor Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record ClaudeModeChoice(ClaudeUsageAcquisitionMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record DisplayModeChoice(WidgetDisplayMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record BackgroundFillModeChoice(BackgroundFillMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>設定画面で編集中のCodexアカウント。保存時に<see cref="ToSettings"/>で設定へ戻す。</summary>
public sealed class CodexAccountEditor : INotifyPropertyChanged
{
    private string _displayName;
    private string? _codexHomePath;
    private bool _enabled;
    private bool _showInWidget;

    public CodexAccountEditor(CodexAccountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Id = settings.Id;
        _displayName = settings.DisplayName;
        _codexHomePath = settings.CodexHomePath;
        _enabled = settings.Enabled;
        _showInWidget = settings.ShowInWidget;
    }

    public string Id { get; }

    public bool IsDefault => string.Equals(Id, CodexAccountSettings.DefaultAccountId, StringComparison.OrdinalIgnoreCase);

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string? CodexHomePath
    {
        get => _codexHomePath;
        set => SetField(ref _codexHomePath, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool ShowInWidget
    {
        get => _showInWidget;
        set => SetField(ref _showInWidget, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CodexAccountSettings ToSettings() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        CodexHomePath = CodexHomePath,
        Enabled = Enabled,
        ShowInWidget = ShowInWidget,
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}
