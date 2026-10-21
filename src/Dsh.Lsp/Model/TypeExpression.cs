namespace Dsh.Lsp.Model;

public enum TypeExpressionKind
{
    Unknown,
    Primitive,
    Named,
    Generic,
    Array,
    Tuple,
    Function,
    Union,
    Intersection,
    Nullable,
    Pointer,
    Reference,
}

public sealed record TypeExpression(
    string Display,
    TypeExpressionKind Kind = TypeExpressionKind.Unknown,
    string? Name = null,
    IReadOnlyList<TypeExpression>? Arguments = null,
    IReadOnlyList<TypeExpression>? Elements = null,
    IReadOnlyList<TypeExpression>? Parameters = null,
    TypeExpression? ReturnType = null,
    TypeExpression? Element = null,
    bool IsNullable = false,
    bool IsDeclared = false,
    bool IsInferred = false,
    IReadOnlyList<string>? OverloadSignatures = null,
    IReadOnlyList<string>? Conversions = null,
    IReadOnlyList<TypeMember>? Members = null);

public sealed record TypeMember(string Name, string Kind, TypeExpression? Type);
