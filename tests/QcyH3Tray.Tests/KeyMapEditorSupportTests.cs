using QcyH3Tray.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 按键编辑器纯逻辑测试（阶段 4b，App 层 <see cref="KeyMapEditorSupport"/>）：
/// 编辑表 ↔ 整表编码往返、出厂槽保护（action≥0x81 绝不改写）、未知功能值保护、
/// 整表写守卫（部分表/出厂槽变更必须被拒绝）、dirty 判定。
/// </summary>
public class KeyMapEditorSupportTests
{
    /// <summary>真机 H3 形态样表：2 键可编辑行 + 2 条出厂槽 + 1 条非标准动作行（模拟未知行透传）。</summary>
    private static readonly List<KeyMappingEntry> SampleTable =
    [
        new(0x07, 0x01, 0x01), new(0x07, 0x02, 0x07), new(0x07, 0x03, 0x04), new(0x07, 0x04, 0x0A), new(0x07, 0x05, 0x0B),
        new(0x03, 0x01, 0x06), new(0x03, 0x02, 0x02), new(0x03, 0x03, 0x03), new(0x03, 0x04, 0x08), new(0x03, 0x05, 0x09),
        new(0x07, 0x81, 0x01), new(0x07, 0x82, 0x02),   // 出厂槽（只读）
        new(0x03, 0x10, 0x05),                          // 非标准动作行：编辑器不渲染，但整表写必须原样带回
    ];

    // ───────────────────────── 分组与行读取 ─────────────────────────

    // 分组键 = 有可编辑行的 keyNum（首现顺序）；纯出厂槽键不进分组。
    [Fact]
    public void GetKeyNumbers_FirstSeenOrder_ExcludesFactoryOnlyKeys()
    {
        var table = new List<KeyMappingEntry>
        {
            new(0x05, 0x81, 0x01),          // 纯出厂槽键：不进分组
            new(0x07, 0x01, 0x01),
            new(0x03, 0x01, 0x06),
            new(0x07, 0x02, 0x07),
        };
        Assert.Equal([0x07, 0x03], KeyMapEditorSupport.GetKeyNumbers(table));
    }

    // GetFunction：可编辑行返回功能值；出厂槽 / 不存在的行返回 null（UI 据此禁用）。
    [Fact]
    public void GetFunction_EditableRow_Value_FactoryOrMissingRow_Null()
    {
        Assert.Equal((byte)0x01, KeyMapEditorSupport.GetFunction(SampleTable, 0x07, 0x01));
        Assert.Null(KeyMapEditorSupport.GetFunction(SampleTable, 0x07, 0x81));   // 出厂槽不可编辑
        Assert.Null(KeyMapEditorSupport.GetFunction(SampleTable, 0x05, 0x01));   // 行不存在
    }

    // 功能选项集 = 已知全集 + 表内未知值（如 0x7F 出现在可编辑行时必须可显示且原样回写）。
    [Fact]
    public void BuildFunctionOptions_KnownSet_PlusUnknownValuesFromTable()
    {
        var table = new List<KeyMappingEntry> { new(0x07, 0x01, 0x7F), new(0x07, 0x02, 0x01) };
        List<byte> options = KeyMapEditorSupport.BuildFunctionOptions(table);

        Assert.Equal(KeyMapEditorSupport.EditableFunctions.Append((byte)0x7F), options);
        // 0x7F 有显示名（不是 "未知"），保证 UI 可呈现
        Assert.Equal("无功能（APK 127→0 归一化）", KeyMapEditorSupport.FunctionOptionName(0x7F));
    }

    // ───────────────────────── 编辑 → 整表组装（往返） ─────────────────────────

    // 空 edits：整表逐字节不变（读取即写回 == 原表，真机探针验证过的性质）。
    [Fact]
    public void ApplyEdits_NoEdits_TableUnchanged()
    {
        List<KeyMappingEntry> result = KeyMapEditorSupport.ApplyEdits(SampleTable, new Dictionary<KeyMapRowKey, byte>());
        Assert.Equal(SampleTable, result);
    }

    // 编辑一条可编辑行：仅该行功能值变化，顺序/其余行/出厂槽/未知行全部原样。
    [Fact]
    public void ApplyEdits_ReplacesOnlyEditedRows()
    {
        var edits = new Dictionary<KeyMapRowKey, byte> { [new KeyMapRowKey(0x03, 0x01)] = 0x02 };
        List<KeyMappingEntry> result = KeyMapEditorSupport.ApplyEdits(SampleTable, edits);

        Assert.Equal(SampleTable.Count, result.Count);
        Assert.Equal(new KeyMappingEntry(0x03, 0x01, 0x02), result[5]);
        for (int i = 0; i < SampleTable.Count; i++)
        {
            if (i != 5)
            {
                Assert.Equal(SampleTable[i], result[i]);
            }
        }
    }

    // 出厂槽保护：即使编辑表里出现出厂槽 (keyNum, action) 条目，ApplyEdits 也不改写出厂槽行。
    [Fact]
    public void ApplyEdits_FactoryRowsNeverModified()
    {
        var edits = new Dictionary<KeyMapRowKey, byte>
        {
            [new KeyMapRowKey(0x07, 0x81)] = 0x00,   // 试图改出厂槽
            [new KeyMapRowKey(0x07, 0x82)] = 0x01,
            [new KeyMapRowKey(0x07, 0x01)] = 0x03,   // 正常编辑
        };
        List<KeyMappingEntry> result = KeyMapEditorSupport.ApplyEdits(SampleTable, edits);

        Assert.Equal(new KeyMappingEntry(0x07, 0x81, 0x01), result[10]);   // 原样
        Assert.Equal(new KeyMappingEntry(0x07, 0x82, 0x02), result[11]);   // 原样
        Assert.Equal(new KeyMappingEntry(0x07, 0x01, 0x03), result[0]);    // 可编辑行生效
    }

    // 编辑表 → 整表 → 线上 LTV → 解析回编辑表：逐字节往返一致（含出厂槽与未知行）。
    [Fact]
    public void FullTable_EncodeRoundTrip_ByteExact()
    {
        var edits = new Dictionary<KeyMapRowKey, byte>
        {
            [new KeyMapRowKey(0x07, 0x01)] = 0xFF,
            [new KeyMapRowKey(0x03, 0x05)] = 0x01,
        };
        List<KeyMappingEntry> fullTable = KeyMapEditorSupport.ApplyEdits(SampleTable, edits);
        byte[] payload = KeyMap.BuildSetAdvInfoPayload(fullTable);
        Assert.Equal(1 + fullTable.Count * KeyMap.BytesPerEntry, payload[0]);

        // 线上格式：[len][0x02][三元组×N]——解出 TLV 值再整表读回
        List<KeyMappingEntry>? parsed = KeyMap.ParseAdvInfoValue(payload[2..]);
        Assert.NotNull(parsed);
        Assert.Equal(ExpectedTableAfterRoundTripEdits(), parsed);
        Assert.Equal(fullTable, parsed);
    }

    // 意外形态必须被守卫拒绝：部分表（少行）/ 出厂槽被改写 → StructurePreserved=false。
    [Fact]
    public void StructurePreserved_RejectsPartialTableAndFactoryMutation()
    {
        List<KeyMappingEntry> fullTable = KeyMapEditorSupport.ApplyEdits(SampleTable, new Dictionary<KeyMapRowKey, byte>());
        Assert.True(KeyMapEditorSupport.StructurePreserved(SampleTable, fullTable));

        // 部分表：丢掉最后一行（未知行）
        List<KeyMappingEntry> partial = fullTable.Take(fullTable.Count - 1).ToList();
        Assert.False(KeyMapEditorSupport.StructurePreserved(SampleTable, partial));

        // 出厂槽被改写的候选表
        var mutated = fullTable.ToList();
        mutated[10] = new KeyMappingEntry(0x07, 0x81, 0x00);
        Assert.False(KeyMapEditorSupport.StructurePreserved(SampleTable, mutated));
    }

    // dirty：值实际变化 → true；编辑回原值 → false；出厂槽编辑不算 dirty。
    [Fact]
    public void IsDirty_TrueOnlyWhenEditableValueChanges()
    {
        Assert.True(KeyMapEditorSupport.IsDirty(
            SampleTable, new Dictionary<KeyMapRowKey, byte> { [new KeyMapRowKey(0x07, 0x01)] = 0x03 }));
        Assert.False(KeyMapEditorSupport.IsDirty(
            SampleTable, new Dictionary<KeyMapRowKey, byte> { [new KeyMapRowKey(0x07, 0x01)] = 0x01 }));
        Assert.False(KeyMapEditorSupport.IsDirty(
            SampleTable, new Dictionary<KeyMapRowKey, byte> { [new KeyMapRowKey(0x07, 0x81)] = 0x00 }));
        Assert.False(KeyMapEditorSupport.IsDirty(SampleTable, new Dictionary<KeyMapRowKey, byte>()));
    }

    // ───────────────────────── 辅助 ─────────────────────────

    /// <summary>样表经两处编辑（0x07 单击→0xFF、0x03 长按→0x01）后的期望整表（独立推导对照）。</summary>
    private static List<KeyMappingEntry> ExpectedTableAfterRoundTripEdits() =>
    [
        new(0x07, 0x01, 0xFF), new(0x07, 0x02, 0x07), new(0x07, 0x03, 0x04), new(0x07, 0x04, 0x0A), new(0x07, 0x05, 0x0B),
        new(0x03, 0x01, 0x06), new(0x03, 0x02, 0x02), new(0x03, 0x03, 0x03), new(0x03, 0x04, 0x08),
        new(0x03, 0x05, 0x01),
        new(0x07, 0x81, 0x01), new(0x07, 0x82, 0x02),
        new(0x03, 0x10, 0x05),
    ];
}
