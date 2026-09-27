namespace Dsh.Tui;

/** 转录行的着色提示: 仅 diff 卡片使用(diff 标题/新增行/删除行), 其余行为 None。 */
internal enum TranscriptTint
{
    None,
    DiffTitle,
    DiffAdded,
    DiffRemoved,
}
