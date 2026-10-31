using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>编辑器一行的标识：物理键编号 + 动作索引（单击..长按）。</summary>
public readonly record struct KeyMapRowKey(byte KeyNumber, byte Action);

/// <summary>
/// 按键编辑器纯逻辑（阶段 4b，与控件零依赖、可单测）：
/// <list type="bullet">
/// <item>整表 ↔ 分组编辑行：按 keyNum 分组（首现顺序），每组渲染 单击/双击/三击/四击/长按 5 行，
/// 表中不存在的 (keyNum, action) 行禁用（不凭空发明数据）。</item>
/// <item>出厂槽保护：action ≥ <see cref="FactoryActionThreshold"/> 的三元组语义未知，只读展示、
/// 编辑与写入一律原样带回（<see cref="ApplyEdits"/> 结构性保证）。</item>
/// <item>整表写守卫：写路径唯一来源是 <see cref="ApplyEdits"/>（原表 + 编辑差异 → 等长整表），
/// 写前可用 <see cref="StructurePreserved"/> 复核（条数一致、出厂槽逐字节一致）——
/// 「整表或全不写」由结构保证，绝不产出部分表。</item>
/// <item>未知功能值保护：可编辑选项 = 已知功能全集 + 表中出现但不属于全集的值（原样带回，不静默改写）。</item>
/// </list>
/// </summary>
internal static class KeyMapEditorSupport
{
    /// <summary>出厂槽动作阈值：真机出厂三元组 action ≥ 0x81（0x81..0x85），语义未知，禁止编辑。</summary>
    public const byte FactoryActionThreshold = 0x81;

    /// <summary>每键渲染的动作行：1..5 = 单击/双击/三击/四击/长按（<see cref="KeyMap.ActionName"/>）。</summary>
    public static readonly byte[] EditableActions = [0x01, 0x02, 0x03, 0x04, 0x05];

    /// <summary>
    /// 可编辑功能选项全集（<see cref="KeyMap.FunctionName"/> 已知值；0x7F 为 APK 127→0 归一化
    /// 变体不单列——表内出现时经 <see cref="BuildFunctionOptions"/> 并入，保证原样回写）。
    /// </summary>
    public static readonly byte[] EditableFunctions =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF,
    ];

    /// <summary>是否为出厂槽（只读展示，禁止编辑与改写）。</summary>
    public static bool IsFactoryAction(byte action) => action >= FactoryActionThreshold;

    /// <summary>按键分组显示名：物理位置语义未知，原样标注（UI 另附「以耳机实际按键为准」提示）。</summary>
    public static string KeyDisplayName(byte keyNumber) => $"按键 0x{keyNumber:X2}";

    /// <summary>功能选项显示名（语义以 <see cref="KeyMap.FunctionName"/> 为准）。</summary>
    public static string FunctionOptionName(byte function) => KeyMap.FunctionName(function);

    /// <summary>动作行显示名（1..5 → 单击..长按；其余值理论不出现在可编辑行）。</summary>
    public static string ActionName(byte action) => KeyMap.ActionName(action);

    /// <summary>
    /// 需要分组渲染的 keyNum 列表（拥有至少一条可编辑行；按表中首现顺序）。
    /// 纯出厂槽键不进分组（它们只出现在只读折叠区）。
    /// </summary>
    public static List<byte> GetKeyNumbers(IReadOnlyList<KeyMappingEntry> entries)
    {
        var keys = new List<byte>();
        foreach (KeyMappingEntry entry in entries)
        {
            if (!IsFactoryAction(entry.Action) && !keys.Contains(entry.KeyNumber))
            {
                keys.Add(entry.KeyNumber);
            }
        }
        return keys;
    }

    /// <summary>表中是否存在该可编辑行（出厂槽不算可编辑行）。</summary>
    public static bool HasEditableRow(IReadOnlyList<KeyMappingEntry> entries, byte keyNumber, byte action)
        => GetFunction(entries, keyNumber, action) is not null;

    /// <summary>
    /// 读取 (keyNum, action) 行的当前功能值；行不存在或为出厂槽 → null（UI 据此禁用该行）。
    /// </summary>
    public static byte? GetFunction(IReadOnlyList<KeyMappingEntry> entries, byte keyNumber, byte action)
    {
        foreach (KeyMappingEntry entry in entries)
        {
            if (entry.KeyNumber == keyNumber && entry.Action == action)
            {
                return IsFactoryAction(entry.Action) ? null : entry.Function;
            }
        }
        return null;
    }

    /// <summary>出厂槽三元组（原样、保序；编辑器折叠区只读展示）。</summary>
    public static List<KeyMappingEntry> FactoryEntries(IReadOnlyList<KeyMappingEntry> entries)
        => entries.Where(entry => IsFactoryAction(entry.Action)).ToList();

    /// <summary>
    /// ComboBox 功能选项值集：已知全集 + 表中出现但不在全集的值（保序追加）——
    /// 保证任何读到的可编辑行都有对应选项，改动前原值可原样回写。
    /// </summary>
    public static List<byte> BuildFunctionOptions(IReadOnlyList<KeyMappingEntry> entries)
    {
        var options = new List<byte>(EditableFunctions);
        foreach (KeyMappingEntry entry in entries)
        {
            if (!IsFactoryAction(entry.Action) && !options.Contains(entry.Function))
            {
                options.Add(entry.Function);
            }
        }
        return options;
    }

    /// <summary>
    /// 组装写回整表：原表基础上仅把 <paramref name="edits"/> 命中的可编辑行替换为新功能值——
    /// 条数、顺序、出厂槽与未编辑行全部原样保留（空 edits → 与原表逐字节一致）。
    /// </summary>
    public static List<KeyMappingEntry> ApplyEdits(
        IReadOnlyList<KeyMappingEntry> entries, IReadOnlyDictionary<KeyMapRowKey, byte> edits)
    {
        var result = new List<KeyMappingEntry>(entries.Count);
        foreach (KeyMappingEntry entry in entries)
        {
            if (!IsFactoryAction(entry.Action)
                && edits.TryGetValue(new KeyMapRowKey(entry.KeyNumber, entry.Action), out byte function))
            {
                result.Add(entry with { Function = function });
            }
            else
            {
                result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>
    /// 整表写守卫：候选表与原表条数一致、出厂槽逐字节一致（可编辑行允许功能值变化）。
    /// 写前为 false 时必须拒绝写入（「整表或全不写」铁律的显式复核）。
    /// </summary>
    public static bool StructurePreserved(
        IReadOnlyList<KeyMappingEntry> original, IReadOnlyList<KeyMappingEntry> candidate)
    {
        if (original.Count != candidate.Count)
        {
            return false;
        }
        for (int i = 0; i < original.Count; i++)
        {
            if (IsFactoryAction(original[i].Action)
                && original[i] != candidate[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>编辑是否引入了与原表不同的功能值（用户改回原值 → 不 dirty）。</summary>
    public static bool IsDirty(IReadOnlyList<KeyMappingEntry> entries, IReadOnlyDictionary<KeyMapRowKey, byte> edits)
    {
        foreach ((KeyMapRowKey key, byte function) in edits)
        {
            if (GetFunction(entries, key.KeyNumber, key.Action) is byte current && current != function)
            {
                return true;
            }
        }
        return false;
    }
}
