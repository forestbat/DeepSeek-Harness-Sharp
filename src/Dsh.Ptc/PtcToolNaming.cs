using Microsoft.CodeAnalysis.CSharp;

namespace Dsh.Ptc;

/** PTC 绑定以工具名作为生成的 C# 方法名; 不是合法标识符(或撞上关键字)的工具不生成方法, 经 tools.call("<name>", args) 到达。 */
public static class PtcToolNaming
{
    public static bool IsCallable(string name)
        => name.Length > 0
           && (char.IsLetter(name[0]) || name[0] == '_')
           && name.All(character => char.IsLetterOrDigit(character) || character == '_')
           && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
}
