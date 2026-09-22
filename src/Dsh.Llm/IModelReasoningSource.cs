namespace Dsh.Llm;

/** 模型推理强度的在线元数据来源(如 openai 兼容端点的 /models 响应), 为静态表与 settings 标记提供实时补充。 */
public interface IModelReasoningSource
{
    LlmModelReasoningInfo? ReasoningFor(string model);
}
