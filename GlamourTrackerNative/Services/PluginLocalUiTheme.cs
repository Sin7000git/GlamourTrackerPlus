using System.Numerics;
using System.Runtime.Serialization;
using System.Text;
using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;

namespace GlamourTracker.Services;

/// <summary>
/// Full local ImGui theme (all pushable colors + style vars) for Glamour Tracker+.
/// Defaults match the user-tuned in-game-like style (2026-07-27).
/// Stored in config; edited under Settings → Edit theme colors.
/// </summary>
[Serializable]
public sealed class PluginLocalUiTheme
{
    /// <summary>ImGuiCol name → RGBA.</summary>
    public Dictionary<string, Vector4> Colors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ImGuiStyleVar name → float (rounding, border size, alpha, …).</summary>
    public Dictionary<string, float> FloatVars { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ImGuiStyleVar name → Vector2 (padding, spacing, aligns, …).</summary>
    public Dictionary<string, Vector2> VectorVars { get; set; } = new(StringComparer.Ordinal);

    // --- Legacy fields (pre-dictionary theme). Still deserialized so existing user themes migrate. ---

    public Vector4 WindowBg { get; set; } = new(0.24313726f, 0.23921569f, 0.24313726f, 1.0f);
    public Vector4 ChildBg { get; set; } = new(0.2f, 0.19607843f, 0.2f, 0.43137252f);
    public Vector4 PopupBg { get; set; } = new(0.2f, 0.19607843f, 0.2f, 1.0f);
    public Vector4 Border { get; set; } = new(0.58431375f, 0.55643475f, 0.4170396f, 1.0f);
    public Vector4 Text { get; set; } = new(0.9372549f, 0.9156342f, 0.86374474f, 1.0f);
    public Vector4 TextDisabled { get; set; } = new(0.55f, 0.54f, 0.5f, 1.0f);
    public Vector4 TitleBg { get; set; } = new(0.2f, 0.19607843f, 0.2f, 1.0f);
    public Vector4 TitleBgActive { get; set; } = new(0.24313726f, 0.23921569f, 0.24313726f, 1.0f);
    public Vector4 FrameBg { get; set; } = new(0.15294118f, 0.15294118f, 0.15294118f, 0.5333333f);
    public Vector4 FrameBgHovered { get; set; } = new(0.2f, 0.19607843f, 0.2f, 1.0f);
    public Vector4 FrameBgActive { get; set; } = new(0.2f, 0.19607843f, 0.2f, 1.0f);
    public Vector4 Button { get; set; } = new(0.2f, 0.2f, 0.2f, 1.0f);
    public Vector4 ButtonHovered { get; set; } = new(0.34117648f, 0.3019608f, 0.22352941f, 1.0f);
    public Vector4 ButtonActive { get; set; } = new(0.43529412f, 0.38431373f, 0.28235295f, 1.0f);
    public Vector4 Header { get; set; } = new(0.15294118f, 0.15294118f, 0.15294118f, 0.5333333f);
    public Vector4 HeaderHovered { get; set; } = new(0.2f, 0.19607843f, 0.2f, 1.0f);
    public Vector4 HeaderActive { get; set; } = new(0.44f, 0.36f, 0.18f, 0.95f);
    public Vector4 Tab { get; set; } = new(0.2f, 0.2f, 0.2f, 1.0f);
    public Vector4 TabHovered { get; set; } = new(0.34117645f, 0.30148402f, 0.22209917f, 1.0f);
    public Vector4 TabActive { get; set; } = new(0.43529412f, 0.38431373f, 0.28235295f, 1.0f);
    public Vector4 CheckMark { get; set; } = new(0.9f, 0.78f, 0.4f, 1.0f);
    public Vector4 SliderGrab { get; set; } = new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f);
    public Vector4 SliderGrabActive { get; set; } = new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f);
    public Vector4 ScrollbarGrab { get; set; } = new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f);
    public Vector4 Separator { get; set; } = new(0.55f, 0.5f, 0.35f, 0.4f);
    public Vector4 ResizeGrip { get; set; } = new(0.72f, 0.66f, 0.48f, 0.3f);
    public Vector4 ResizeGripHovered { get; set; } = new(0.86f, 0.76f, 0.42f, 0.7f);
    public Vector4 ResizeGripActive { get; set; } = new(0.9f, 0.78f, 0.4f, 0.95f);
    public float WindowRounding { get; set; } = 8.0f;
    public float ChildRounding { get; set; } = 6.0f;
    public float FrameRounding { get; set; } = 13.0f;
    public float TabRounding { get; set; } = 16.0f;
    public float PopupRounding { get; set; } = 2.0f;
    public float WindowBorderSize { get; set; } = 3.4f;
    public float ChildBorderSize { get; set; } = 0.1f;
    public float FrameBorderSize { get; set; } = 1.8f;

    private static readonly HashSet<string> VectorStyleVars = new(StringComparer.Ordinal)
    {
        "WindowPadding",
        "WindowMinSize",
        "WindowTitleAlign",
        "FramePadding",
        "ItemSpacing",
        "ItemInnerSpacing",
        "CellPadding",
        "TouchExtraPadding",
        "ButtonTextAlign",
        "SelectableTextAlign",
        "DisplayWindowPadding",
        "DisplaySafeAreaPadding",
        "SeparatorTextAlign",
        "SeparatorTextPadding",
    };

    public static PluginLocalUiTheme CreateDefault()
    {
        var theme = new PluginLocalUiTheme();
        theme.EnsureInitialized();
        return theme;
    }

    [OnDeserialized]
    internal void OnDeserialized(StreamingContext _) => EnsureInitialized();

    /// <summary>Fill missing keys + migrate legacy named fields into dictionaries.</summary>
    public void EnsureInitialized()
    {
        Colors ??= new Dictionary<string, Vector4>(StringComparer.Ordinal);
        FloatVars ??= new Dictionary<string, float>(StringComparer.Ordinal);
        VectorVars ??= new Dictionary<string, Vector2>(StringComparer.Ordinal);

        MigrateLegacyIntoDictionaries();
        SeedMissingFromDefaults();
    }

    public (int Vars, int Colors) Push()
    {
        EnsureInitialized();
        var vars = 0;
        var colors = 0;

        foreach (ImGuiStyleVar styleVar in Enum.GetValues<ImGuiStyleVar>())
        {
            var name = styleVar.ToString();
            if (IsCountName(name))
                continue;

            if (IsVectorStyleVar(name))
            {
                if (!VectorVars.TryGetValue(name, out var vec))
                    continue;
                ImGui.PushStyleVar(styleVar, vec);
                vars++;
            }
            else if (FloatVars.TryGetValue(name, out var value))
            {
                ImGui.PushStyleVar(styleVar, value);
                vars++;
            }
        }

        foreach (ImGuiCol col in Enum.GetValues<ImGuiCol>())
        {
            var name = col.ToString();
            if (IsCountName(name))
                continue;
            if (!Colors.TryGetValue(name, out var value))
                continue;
            ImGui.PushStyleColor(col, value);
            colors++;
        }

        return (vars, colors);
    }

    public static void Pop(int vars, int colors)
    {
        if (colors > 0)
            ImGui.PopStyleColor(colors);
        if (vars > 0)
            ImGui.PopStyleVar(vars);
    }

    /// <summary>
    /// Writes the current theme to theme-snapshot.json (log dir + plugin DLL dir when possible)
    /// so it can be reviewed later and copied into CreateDefault.
    /// </summary>
    public IReadOnlyList<string> WriteSnapshot()
    {
        EnsureInitialized();
        var payload = new
        {
            ExportedUtc = DateTime.UtcNow.ToString("o"),
            Colors,
            FloatVars,
            VectorVars,
        };
        var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        var written = new List<string>();

        void TryWrite(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, json, Encoding.UTF8);
                written.Add(path);
            }
            catch (Exception ex)
            {
                PluginFileLog.Warn("ui.theme", $"Could not write snapshot to {path}: {ex.Message}");
            }
        }

        TryWrite(Path.Combine(Path.GetDirectoryName(PluginFileLog.LogPath) ?? ".", "theme-snapshot.json"));

        try
        {
            var asmDir = Path.GetDirectoryName(typeof(PluginLocalUiTheme).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir))
                TryWrite(Path.Combine(asmDir, "theme-snapshot.json"));
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("ui.theme", $"DLL-dir snapshot skipped: {ex.Message}");
        }

        if (written.Count > 0)
            PluginFileLog.Info("ui.theme", "Theme snapshot written → " + string.Join(" | ", written));
        else
            PluginFileLog.Warn("ui.theme", "Theme snapshot failed (no writable path).");

        return written;
    }

    public bool DrawEditor()
    {
        EnsureInitialized();
        var changed = false;

        if (ImGui.CollapsingHeader("Colors", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ImGuiCol col in Enum.GetValues<ImGuiCol>())
            {
                var name = col.ToString();
                if (IsCountName(name))
                    continue;
                if (!Colors.TryGetValue(name, out var value))
                    value = DefaultColor(name);

                if (ImGui.ColorEdit4(FriendlyColorLabel(name), ref value, ImGuiColorEditFlags.AlphaBar))
                {
                    Colors[name] = value;
                    SyncLegacyColor(name, value);
                    changed = true;
                }
            }
        }

        if (ImGui.CollapsingHeader("Sizes & rounding", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ImGuiStyleVar styleVar in Enum.GetValues<ImGuiStyleVar>())
            {
                var name = styleVar.ToString();
                if (IsCountName(name) || IsVectorStyleVar(name))
                    continue;
                if (!FloatVars.TryGetValue(name, out var value))
                    value = DefaultFloat(name);

                var (min, max, format) = FloatSliderRange(name);
                if (ImGui.SliderFloat(FriendlyStyleLabel(name), ref value, min, max, format))
                {
                    FloatVars[name] = value;
                    SyncLegacyFloat(name, value);
                    changed = true;
                }
            }
        }

        if (ImGui.CollapsingHeader("Padding & spacing", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (ImGuiStyleVar styleVar in Enum.GetValues<ImGuiStyleVar>())
            {
                var name = styleVar.ToString();
                if (IsCountName(name) || !IsVectorStyleVar(name))
                    continue;
                if (!VectorVars.TryGetValue(name, out var value))
                    value = DefaultVector(name);

                if (ImGui.DragFloat2(FriendlyStyleLabel(name), ref value, 0.25f, 0f, 64f, "%.1f"))
                {
                    VectorVars[name] = value;
                    changed = true;
                }
            }
        }

        ImGui.TextDisabled(
            "Tip: ImGui uses one shared border color for windows, panels, inputs, and tabs. "
            + "Border thickness can still differ per control type.");

        return changed;
    }

    private void MigrateLegacyIntoDictionaries()
    {
        void Color(string key, Vector4 value)
        {
            if (!Colors.ContainsKey(key))
                Colors[key] = value;
        }

        void Flt(string key, float value)
        {
            if (!FloatVars.ContainsKey(key))
                FloatVars[key] = value;
        }

        Color(nameof(WindowBg), WindowBg);
        Color(nameof(ChildBg), ChildBg);
        Color(nameof(PopupBg), PopupBg);
        Color(nameof(Border), Border);
        Color(nameof(Text), Text);
        Color(nameof(TextDisabled), TextDisabled);
        Color(nameof(TitleBg), TitleBg);
        Color(nameof(TitleBgActive), TitleBgActive);
        Color(nameof(FrameBg), FrameBg);
        Color(nameof(FrameBgHovered), FrameBgHovered);
        Color(nameof(FrameBgActive), FrameBgActive);
        Color(nameof(Button), Button);
        Color(nameof(ButtonHovered), ButtonHovered);
        Color(nameof(ButtonActive), ButtonActive);
        Color(nameof(Header), Header);
        Color(nameof(HeaderHovered), HeaderHovered);
        Color(nameof(HeaderActive), HeaderActive);
        Color(nameof(Tab), Tab);
        Color(nameof(TabHovered), TabHovered);
        Color(nameof(TabActive), TabActive);
        Color(nameof(CheckMark), CheckMark);
        Color(nameof(SliderGrab), SliderGrab);
        Color(nameof(SliderGrabActive), SliderGrabActive);
        Color(nameof(ScrollbarGrab), ScrollbarGrab);
        Color(nameof(Separator), Separator);
        Color(nameof(ResizeGrip), ResizeGrip);
        Color(nameof(ResizeGripHovered), ResizeGripHovered);
        Color(nameof(ResizeGripActive), ResizeGripActive);

        Flt(nameof(WindowRounding), WindowRounding);
        Flt(nameof(ChildRounding), ChildRounding);
        Flt(nameof(FrameRounding), FrameRounding);
        Flt(nameof(TabRounding), TabRounding);
        Flt(nameof(PopupRounding), PopupRounding);
        Flt(nameof(WindowBorderSize), WindowBorderSize);
        Flt(nameof(ChildBorderSize), ChildBorderSize);
        Flt(nameof(FrameBorderSize), FrameBorderSize);
    }

    private void SeedMissingFromDefaults()
    {
        foreach (ImGuiCol col in Enum.GetValues<ImGuiCol>())
        {
            var name = col.ToString();
            if (IsCountName(name) || Colors.ContainsKey(name))
                continue;
            Colors[name] = DefaultColor(name);
        }

        foreach (ImGuiStyleVar styleVar in Enum.GetValues<ImGuiStyleVar>())
        {
            var name = styleVar.ToString();
            if (IsCountName(name))
                continue;

            if (IsVectorStyleVar(name))
            {
                if (!VectorVars.ContainsKey(name))
                    VectorVars[name] = DefaultVector(name);
            }
            else if (!FloatVars.ContainsKey(name))
            {
                FloatVars[name] = DefaultFloat(name);
            }
        }
    }

    private static bool IsVectorStyleVar(string name)
    {
        if (VectorStyleVars.Contains(name))
            return true;

        // Float exceptions that look like spacing/padding names.
        if (name is "IndentSpacing" or "ScrollbarSize" or "GrabMinSize" or "LogSliderDeadzone"
            or "SeparatorTextBorderSize")
            return false;

        return name.Contains("Padding", StringComparison.Ordinal)
               || name.EndsWith("Align", StringComparison.Ordinal)
               || name.EndsWith("MinSize", StringComparison.Ordinal)
               || name is "ItemSpacing" or "ItemInnerSpacing";
    }

    private void SyncLegacyColor(string name, Vector4 value)
    {
        switch (name)
        {
            case nameof(WindowBg): WindowBg = value; break;
            case nameof(ChildBg): ChildBg = value; break;
            case nameof(PopupBg): PopupBg = value; break;
            case nameof(Border): Border = value; break;
            case nameof(Text): Text = value; break;
            case nameof(TextDisabled): TextDisabled = value; break;
            case nameof(TitleBg): TitleBg = value; break;
            case nameof(TitleBgActive): TitleBgActive = value; break;
            case nameof(FrameBg): FrameBg = value; break;
            case nameof(FrameBgHovered): FrameBgHovered = value; break;
            case nameof(FrameBgActive): FrameBgActive = value; break;
            case nameof(Button): Button = value; break;
            case nameof(ButtonHovered): ButtonHovered = value; break;
            case nameof(ButtonActive): ButtonActive = value; break;
            case nameof(Header): Header = value; break;
            case nameof(HeaderHovered): HeaderHovered = value; break;
            case nameof(HeaderActive): HeaderActive = value; break;
            case nameof(Tab): Tab = value; break;
            case nameof(TabHovered): TabHovered = value; break;
            case nameof(TabActive): TabActive = value; break;
            case nameof(CheckMark): CheckMark = value; break;
            case nameof(SliderGrab): SliderGrab = value; break;
            case nameof(SliderGrabActive): SliderGrabActive = value; break;
            case nameof(ScrollbarGrab): ScrollbarGrab = value; break;
            case nameof(Separator): Separator = value; break;
            case nameof(ResizeGrip): ResizeGrip = value; break;
            case nameof(ResizeGripHovered): ResizeGripHovered = value; break;
            case nameof(ResizeGripActive): ResizeGripActive = value; break;
        }
    }

    private void SyncLegacyFloat(string name, float value)
    {
        switch (name)
        {
            case nameof(WindowRounding): WindowRounding = value; break;
            case nameof(ChildRounding): ChildRounding = value; break;
            case nameof(FrameRounding): FrameRounding = value; break;
            case nameof(TabRounding): TabRounding = value; break;
            case nameof(PopupRounding): PopupRounding = value; break;
            case nameof(WindowBorderSize): WindowBorderSize = value; break;
            case nameof(ChildBorderSize): ChildBorderSize = value; break;
            case nameof(FrameBorderSize): FrameBorderSize = value; break;
        }
    }

    private static bool IsCountName(string name) =>
        name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COUNT_", StringComparison.OrdinalIgnoreCase);

    private static Vector4 DefaultColor(string name) => name switch
    {
        "Border" => new(0.58431375f, 0.55643475f, 0.4170396f, 1.0f),
        "BorderShadow" => new(1.0f, 1.0f, 1.0f, 0.0f),
        "Button" => new(0.2f, 0.2f, 0.2f, 1.0f),
        "ButtonActive" => new(0.43529412f, 0.38431373f, 0.28235295f, 1.0f),
        "ButtonHovered" => new(0.34117648f, 0.3019608f, 0.22352941f, 1.0f),
        "CheckMark" => new(0.9f, 0.78f, 0.4f, 1.0f),
        "ChildBg" => new(0.2f, 0.19607843f, 0.2f, 0.43137252f),
        "DockingEmptyBg" => new(0.12f, 0.12f, 0.14f, 1.0f),
        "DockingPreview" => new(0.72f, 0.62f, 0.34f, 0.5f),
        "DragDropTarget" => new(0.9f, 0.78f, 0.4f, 0.9f),
        "FrameBg" => new(0.15294118f, 0.15294118f, 0.15294118f, 0.5333333f),
        "FrameBgActive" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "FrameBgHovered" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "Header" => new(0.15294118f, 0.15294118f, 0.15294118f, 0.5333333f),
        "HeaderActive" => new(0.44f, 0.36f, 0.18f, 0.95f),
        "HeaderHovered" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "MenuBarBg" => new(1.0f, 1.0f, 1.0f, 1.0f),
        "ModalWindowDimBg" => new(0.1f, 0.1f, 0.1f, 0.55f),
        "NavHighlight" => new(0.9f, 0.78f, 0.4f, 0.8f),
        "NavWindowingDimBg" => new(0.1f, 0.1f, 0.1f, 0.4f),
        "NavWindowingHighlight" => new(1.0f, 1.0f, 1.0f, 0.7f),
        "PlotHistogram" => new(0.72f, 0.62f, 0.34f, 0.85f),
        "PlotHistogramHovered" => new(0.9f, 0.78f, 0.4f, 0.9f),
        "PlotLines" => new(0.7f, 0.7f, 0.65f, 1.0f),
        "PlotLinesHovered" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "PopupBg" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "ResizeGrip" => new(0.72f, 0.66f, 0.48f, 0.3f),
        "ResizeGripActive" => new(0.9f, 0.78f, 0.4f, 0.95f),
        "ResizeGripHovered" => new(0.86f, 0.76f, 0.42f, 0.7f),
        "ScrollbarBg" => new(0.02f, 0.02f, 0.03f, 0.0f),
        "ScrollbarGrab" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "ScrollbarGrabActive" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "ScrollbarGrabHovered" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "Separator" => new(0.55f, 0.5f, 0.35f, 0.4f),
        "SeparatorActive" => new(0.9f, 0.78f, 0.4f, 0.9f),
        "SeparatorHovered" => new(0.72f, 0.62f, 0.34f, 0.7f),
        "SliderGrab" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "SliderGrabActive" => new(0.5372549f, 0.5372549f, 0.5372549f, 1.0f),
        "Tab" => new(0.2f, 0.2f, 0.2f, 1.0f),
        "TabActive" => new(0.43529412f, 0.38431373f, 0.28235295f, 1.0f),
        "TabHovered" => new(0.34117645f, 0.30148402f, 0.22209917f, 1.0f),
        "TabUnfocused" => new(0.1f, 0.1f, 0.12f, 0.9f),
        "TabUnfocusedActive" => new(0.22f, 0.2f, 0.14f, 0.95f),
        "TableBorderLight" => new(0.35f, 0.33f, 0.28f, 0.5f),
        "TableBorderStrong" => new(0.45f, 0.42f, 0.32f, 0.8f),
        "TableHeaderBg" => new(0.14f, 0.14f, 0.16f, 1.0f),
        "TableRowBg" => new(0.0f, 0.0f, 0.0f, 0.0f),
        "TableRowBgAlt" => new(1.0f, 1.0f, 1.0f, 0.03f),
        "Text" => new(0.9372549f, 0.9156342f, 0.86374474f, 1.0f),
        "TextDisabled" => new(0.55f, 0.54f, 0.5f, 1.0f),
        "TextSelectedBg" => new(0.72f, 0.62f, 0.34f, 0.35f),
        "TitleBg" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "TitleBgActive" => new(0.24313726f, 0.23921569f, 0.24313726f, 1.0f),
        "TitleBgCollapsed" => new(0.2f, 0.19607843f, 0.2f, 1.0f),
        "WindowBg" => new(0.24313726f, 0.23921569f, 0.24313726f, 1.0f),
        _ => new(0.50f, 0.50f, 0.50f, 1f),
    };

    private static float DefaultFloat(string name) => name switch
    {
        "Alpha" => 1.0f,
        "ChildBorderSize" => 0.1f,
        "ChildRounding" => 6.0f,
        "DisabledAlpha" => 0.6f,
        "FrameBorderSize" => 1.8f,
        "FrameRounding" => 13.0f,
        "GrabMinSize" => 13.0f,
        "GrabRounding" => 10.0f,
        "IndentSpacing" => 5.0f,
        "PopupBorderSize" => 1.5f,
        "PopupRounding" => 2.0f,
        "ScrollbarRounding" => 10.0f,
        "ScrollbarSize" => 9.0f,
        "TabRounding" => 16.0f,
        "WindowBorderSize" => 3.4f,
        "WindowRounding" => 8.0f,
        _ => 0f,
    };

    private static Vector2 DefaultVector(string name) => name switch
    {
        "ButtonTextAlign" => new(0.5f, 0.5f),
        "CellPadding" => new(4.0f, 4.8f),
        "FramePadding" => new(12.2f, 3.0f),
        "ItemInnerSpacing" => new(7.0f, 4.0f),
        "ItemSpacing" => new(6.5f, 4.0f),
        "SelectableTextAlign" => new(0.0f, 0.0f),
        "WindowMinSize" => new(32.0f, 32.0f),
        "WindowPadding" => new(10.0f, 10.0f),
        "WindowTitleAlign" => new(0.5f, 0.5f),
        _ => Vector2.Zero,
    };


    private static (float Min, float Max, string Format) FloatSliderRange(string name)
    {
        if (name.Contains("Alpha", StringComparison.Ordinal))
            return (0f, 1f, "%.2f");
        if (name.Contains("BorderSize", StringComparison.Ordinal) || name.Contains("Border", StringComparison.Ordinal) && name.Contains("Size", StringComparison.Ordinal))
            return (0f, 4f, "%.1f");
        if (name.Contains("Rounding", StringComparison.Ordinal))
            return (0f, 16f, "%.0f");
        if (name is "ScrollbarSize" or "GrabMinSize" or "IndentSpacing" or "LogSliderDeadzone" or "SeparatorTextBorderSize")
            return (0f, 40f, "%.0f");
        return (0f, 32f, "%.1f");
    }

    private static string FriendlyColorLabel(string name) => name switch
    {
        "WindowBg" => "Window background",
        "ChildBg" => "Panel background",
        "PopupBg" => "Popup background",
        "TextDisabled" => "Disabled text",
        "TitleBg" => "Title bar",
        "TitleBgActive" => "Title bar (active)",
        "TitleBgCollapsed" => "Title bar (collapsed)",
        "FrameBg" => "Input fields",
        "FrameBgHovered" => "Input fields (hover)",
        "FrameBgActive" => "Input fields (active)",
        "ButtonHovered" => "Buttons (hover)",
        "ButtonActive" => "Buttons (pressed)",
        "HeaderHovered" => "Headers (hover)",
        "HeaderActive" => "Headers (active)",
        "TabHovered" => "Tabs (hover)",
        "TabActive" => "Tabs (selected)",
        "TabUnfocused" => "Tabs (unfocused)",
        "TabUnfocusedActive" => "Tabs (unfocused selected)",
        "ScrollbarBg" => "Scrollbar track",
        "ScrollbarGrab" => "Scrollbar thumb",
        "ScrollbarGrabHovered" => "Scrollbar thumb (hover)",
        "ScrollbarGrabActive" => "Scrollbar thumb (active)",
        "SliderGrab" => "Sliders",
        "SliderGrabActive" => "Sliders (active)",
        "CheckMark" => "Check marks",
        "TextSelectedBg" => "Selected text background",
        "ModalWindowDimBg" => "Modal dim overlay",
        "BorderShadow" => "Border shadow",
        _ => SplitCamel(name),
    };

    private static string FriendlyStyleLabel(string name) => name switch
    {
        "WindowRounding" => "Window corner roundness",
        "ChildRounding" => "Panel corner roundness",
        "PopupRounding" => "Popup corner roundness",
        "FrameRounding" => "Input/button corner roundness",
        "TabRounding" => "Tab corner roundness",
        "ScrollbarRounding" => "Scrollbar corner roundness",
        "GrabRounding" => "Slider grab corner roundness",
        "WindowBorderSize" => "Window border thickness",
        "ChildBorderSize" => "Panel border thickness",
        "PopupBorderSize" => "Popup border thickness",
        "FrameBorderSize" => "Input/button border thickness",
        "TabBorderSize" => "Tab border thickness",
        "ScrollbarSize" => "Scrollbar width",
        "GrabMinSize" => "Slider grab minimum size",
        "WindowPadding" => "Window padding",
        "FramePadding" => "Input/button padding",
        "ItemSpacing" => "Item spacing",
        "ItemInnerSpacing" => "Inner item spacing",
        "IndentSpacing" => "Indent spacing",
        "DisabledAlpha" => "Disabled control opacity",
        "Alpha" => "Overall opacity",
        _ => SplitCamel(name),
    };

    private static string SplitCamel(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        var chars = new List<char>(name.Length + 8) { name[0] };
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                chars.Add(' ');
            chars.Add(c);
        }

        return new string(chars.ToArray());
    }
}
