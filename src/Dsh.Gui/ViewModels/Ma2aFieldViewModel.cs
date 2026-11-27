using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

public sealed partial class Ma2aFieldViewModel : ObservableObject
{
    private readonly JsonElement _schema;
    private readonly JsonElement _itemSchema;

    public Ma2aFieldViewModel(string name, JsonElement schema)
    {
        Name = name;
        schema = Ma2aCommandCatalog.Resolve(schema);
        IsNullable = schema.TryGetProperty("nullable", out var inner);
        _schema = Ma2aCommandCatalog.Resolve(IsNullable ? inner : schema);
        _isNull = IsNullable;
        Kind = _schema.GetProperty("type").GetString()!;
        if (IsEnum) Choices = _schema.GetProperty("values").EnumerateArray().Select(value => value.GetString()!).ToArray();
        _text = Choices.FirstOrDefault() ?? "";
        Minimum = _schema.TryGetProperty("minimum", out var minimum) ? minimum.GetDecimal() : 0;
        Maximum = _schema.TryGetProperty("maximum", out var maximum) ? maximum.GetDecimal() : ulong.MaxValue;
        _number = Minimum;
        if (IsObject) Children = _schema.GetProperty("fields").EnumerateObject().Select(field => new Ma2aFieldViewModel(field.Name, field.Value)).ToArray();
        if (!IsArray) return;
        _itemSchema = _schema.GetProperty("items");
        var minimumItems = _schema.TryGetProperty("min_items", out var count) ? count.GetInt32() : 0;
        for (var index = 0; index < minimumItems; index++) AddItem();
    }

    public string Name { get; }
    public string Label => Name.Replace('_', ' ');
    public string Kind { get; }
    public bool IsNullable { get; }
    public bool IsString => Kind == "string";
    public bool IsSecret => Name is "password" or "invitation" or "value";
    public char PasswordCharacter => IsSecret ? '●' : '\0';
    public bool IsEnum => Kind == "enum";
    public bool IsBoolean => Kind == "boolean";
    public bool IsNumber => Kind == "integer";
    public bool IsObject => Kind == "object";
    public bool IsArray => Kind == "array";
    public decimal Minimum { get; }
    public decimal Maximum { get; }
    public IReadOnlyList<string> Choices { get; } = [];
    public IReadOnlyList<Ma2aFieldViewModel> Children { get; } = [];
    public ObservableCollection<Ma2aFieldViewModel> Items { get; } = [];
    public string Hint => IsNullable ? "可留空；启用后填写值" : BoundsHint();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _checked;
    [ObservableProperty] private bool _isNull;
    [ObservableProperty] private decimal? _number;

    [RelayCommand]
    private void AddItem()
    {
        var maximum = _schema.TryGetProperty("max_items", out var bound) ? bound.GetInt32() : 256;
        if (Items.Count < maximum) Items.Add(new Ma2aFieldViewModel("item", _itemSchema));
    }

    [RelayCommand]
    private void RemoveItem(Ma2aFieldViewModel? item)
    {
        if (item is not null) Items.Remove(item);
    }

    public JsonNode? BuildValue()
    {
        if (IsNullable && IsNull) return null;
        if (IsObject) return new JsonObject(Children.Select(child => KeyValuePair.Create(child.Name, child.BuildValue())));
        if (IsArray) return BuildArray();
        if (IsBoolean) return JsonValue.Create(Checked);
        if (IsNumber) return BuildNumber();
        ValidateText();
        return JsonValue.Create(Text);
    }

    private JsonArray BuildArray()
    {
        var minimum = _schema.TryGetProperty("min_items", out var bound) ? bound.GetInt32() : 0;
        if (Items.Count < minimum) throw new InvalidOperationException($"{Label} 至少需要 {minimum} 项。");
        return new JsonArray(Items.Select(item => item.BuildValue()).ToArray());
    }

    private JsonNode BuildNumber()
    {
        if (Number is not { } value || value < Minimum || value > Maximum || value != decimal.Truncate(value))
            throw new InvalidOperationException($"{Label} 必须是 {Minimum} 至 {Maximum} 之间的整数。");
        return JsonValue.Create((ulong)value)!;
    }

    private void ValidateText()
    {
        if (IsEnum && !Choices.Contains(Text)) throw new InvalidOperationException($"请选择 {Label}。");
        var size = Encoding.UTF8.GetByteCount(Text);
        if (_schema.TryGetProperty("min_bytes", out var minimum) && size < minimum.GetInt32()) throw new InvalidOperationException($"请填写 {Label}。");
        if (_schema.TryGetProperty("max_bytes", out var maximum) && size > maximum.GetInt32()) throw new InvalidOperationException($"{Label} 超过允许的 UTF-8 字节数。");
        if (!_schema.TryGetProperty("format", out var format) || format.GetString() != "lowercase_hex") return;
        var length = _schema.GetProperty("bytes").GetInt32() * 2;
        if (Text.Length != length || Text.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException($"{Label} 必须为 {length} 位小写十六进制。");
    }

    private string BoundsHint() => _schema.TryGetProperty("max_bytes", out var maximum)
        ? $"最多 {maximum.GetInt32()} UTF-8 字节" : "";
}
