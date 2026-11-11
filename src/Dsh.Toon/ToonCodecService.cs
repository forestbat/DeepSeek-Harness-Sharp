using System.Text.Json.Nodes;
using Dsh.Runtime;

namespace Dsh.Toon;

/**
 * 把 ToonCodec 注册成命名服务, 供 TUI 的窗格读取工具等消费方解析;
 * 未装本插件时该服务不存在, toon 输出格式在请求时就报插件缺失。
 */
public sealed class ToonCodecService(Context ctx) : Service(ctx, ToonCodecService.ServiceName)
{
    public const string ServiceName = "toon-codec";

    public string Encode(JsonNode? value) => ToonCodec.Encode(value);
}
