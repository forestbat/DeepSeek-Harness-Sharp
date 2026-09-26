using Microsoft.CodeAnalysis.CSharp;

namespace Dsh.Ptc;

/** PTC 绑定以工具名作为生成的 C# 方法名, 只有合法标识符且非关键字的工具才能进 SDK 与绑定表。 */
public static class PtcToolNaming
{
    public static bool IsCallable(string name)
        => name.Length > 0
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(character => char.IsLetterOrDigit(character) || character == '_')
           && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
}
