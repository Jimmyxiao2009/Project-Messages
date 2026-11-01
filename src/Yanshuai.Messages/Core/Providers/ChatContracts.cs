// Core/Providers/ChatContracts.cs —— provider 无关的对话契约
//
// 上层（Agent 循环）只跟这些类型打交道，不碰任何 provider 专有字段。
// 旧项目的工具循环里到处是 "if (profile.ProviderType == \"claude\")" 的分叉，
// 加一个 provider 就要动主循环；这里把差异全部关进 provider 实现。

using System;
using System.Collections.Generic;

namespace Yanshuai.Messages.Core
{
    public sealed class ImagePart
    {
        public string MimeType { get; set; } = "image/png";
        public string Base64 { get; set; } = "";
    }

    public sealed class ToolCallRequest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ArgumentsJson { get; set; } = "";
    }

    /// <summary>发给模型的一条消息（provider 无关形态）。</summary>
    public sealed class ChatTurn
    {
        /// <summary>system | user | assistant | tool</summary>
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        /// <summary>助手消息的思维链。是否回传给服务端由 ChatRequest.SendReasoningBack 决定。</summary>
        public string Reasoning { get; set; } = "";
        public List<ToolCallRequest> ToolCalls { get; set; }
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public List<ImagePart> Images { get; set; }

        public ChatTurn()
        {
            ToolCalls = new List<ToolCallRequest>();
            Images = new List<ImagePart>();
        }

        public static ChatTurn System(string text)
        {
            return new ChatTurn { Role = "system", Content = text ?? "" };
        }

        public static ChatTurn User(string text)
        {
            return new ChatTurn { Role = "user", Content = text ?? "" };
        }

        public static ChatTurn Assistant(string text)
        {
            return new ChatTurn { Role = "assistant", Content = text ?? "" };
        }

        public static ChatTurn ToolResult(string callId, string toolName, string text)
        {
            return new ChatTurn
            {
                Role = "tool",
                ToolCallId = callId ?? "",
                ToolName = toolName ?? "",
                Content = text ?? ""
            };
        }
    }

    public sealed class ToolParameter
    {
        public string Name { get; set; } = "";
        /// <summary>string | number | integer | boolean | array | object</summary>
        public string Type { get; set; } = "string";
        public string Description { get; set; } = "";
        public bool Required { get; set; }
        public List<string> Enum { get; set; }

        public ToolParameter()
        {
            Enum = new List<string>();
        }
    }

    public sealed class ToolSchema
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<ToolParameter> Parameters { get; set; }

        public ToolSchema()
        {
            Parameters = new List<ToolParameter>();
        }

        public ToolSchema Param(string name, string type, string description, bool required)
        {
            Parameters.Add(new ToolParameter
            {
                Name = name,
                Type = type,
                Description = description,
                Required = required
            });
            return this;
        }

        public ToolSchema ParamEnum(string name, string[] values, string description, bool required)
        {
            var p = new ToolParameter
            {
                Name = name,
                Type = "string",
                Description = description,
                Required = required
            };
            foreach (var v in values) p.Enum.Add(v);
            Parameters.Add(p);
            return this;
        }
    }

    public sealed class ChatRequest
    {
        public ApiProfile Api { get; set; }
        public string ApiKey { get; set; } = "";
        public List<ChatTurn> Turns { get; set; }
        /// <summary>空表示不使用工具（走纯对话，可流式）。</summary>
        public List<ToolSchema> Tools { get; set; }

        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? MaxTokens { get; set; }
        public bool Stream { get; set; }
        public bool VisionEnabled { get; set; }

        /// <summary>
        /// 是否把 reasoning 回传给服务端。默认 false —— 标准 OpenAI 接口不接受这个字段，
        /// 只有明确需要的网关（部分 DeepSeek-R1 中转）才打开，否则会被 400 拒绝。
        /// </summary>
        /// <summary>推理强度：auto / low / medium / high。auto 或空都不发这个字段。</summary>
        public string ReasoningEffort { get; set; } = "";

        public bool SendReasoningBack { get; set; }

        /// <summary>附加到请求体里的额外字段（中转站常用）。</summary>
        public Dictionary<string, object> ExtraBody { get; set; }

        public ChatRequest()
        {
            Turns = new List<ChatTurn>();
            Tools = new List<ToolSchema>();
            ExtraBody = new Dictionary<string, object>();
        }
    }

    public sealed class ChatResult
    {
        public bool Ok { get; set; }
        /// <summary>稳定的错误码，供 UI 判断与日志使用：http_401 / timeout / network / parse / empty。</summary>
        public string ErrorCode { get; set; } = "";
        /// <summary>给用户看的一句话。不含请求正文，也不含密钥。</summary>
        public string ErrorMessage { get; set; } = "";

        public string Content { get; set; } = "";
        public string Reasoning { get; set; } = "";
        public List<ToolCallRequest> ToolCalls { get; set; }
        public UsageInfo Usage { get; set; }

        public ChatResult()
        {
            ToolCalls = new List<ToolCallRequest>();
            Usage = new UsageInfo();
        }

        public bool HasToolCalls
        {
            get { return ToolCalls != null && ToolCalls.Count > 0; }
        }

        public static ChatResult Fail(string code, string message)
        {
            return new ChatResult { Ok = false, ErrorCode = code, ErrorMessage = message };
        }
    }

    public sealed class StreamChunk
    {
        public string ContentDelta { get; set; }
        public string ReasoningDelta { get; set; }
    }

    public interface IChatProvider
    {
        /// <summary>非流式补全。工具循环全程走这条（需要完整的 tool_calls）。</summary>
        System.Threading.Tasks.Task<ChatResult> CompleteAsync(ChatRequest request, System.Threading.CancellationToken ct);

        /// <summary>流式补全。只在不用工具时使用。</summary>
        System.Threading.Tasks.Task<ChatResult> StreamAsync(
            ChatRequest request,
            Action<StreamChunk> onChunk,
            System.Threading.CancellationToken ct);
    }

    /// <summary>把 HTTP 状态码翻译成稳定错误码与人话提示。</summary>
    public static class ProviderErrors
    {
        public static ChatResult FromStatus(int status, string bodyPreview)
        {
            string code;
            string message;

            switch (status)
            {
                case 400:
                    code = "http_400";
                    message = "请求被拒绝（400）。可能是模型名不对、参数不支持，或图片格式有问题。";
                    break;
                case 401:
                    code = "http_401";
                    message = "密钥无效或已过期（401）。请到「我的 → 模型档案」重新填写。";
                    break;
                case 402:
                    code = "http_402";
                    message = "账户余额不足（402）。";
                    break;
                case 403:
                    code = "http_403";
                    message = "该密钥没有访问这个模型的权限（403）。";
                    break;
                case 404:
                    code = "http_404";
                    message = "接口地址或模型不存在（404）。检查 URL 是否少了 /v1 或模型名写错。";
                    break;
                case 413:
                    code = "http_413";
                    message = "请求体过大（413）。可能是上下文太长，试试压缩上下文或减少附件。";
                    break;
                case 422:
                    code = "http_422";
                    message = "参数校验失败（422）。";
                    break;
                case 429:
                    code = "http_429";
                    message = "触发限流或额度用尽（429）。稍后再试或换一个模型档案。";
                    break;
                case 500:
                case 502:
                case 503:
                case 504:
                    code = "http_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    message = "服务端错误（" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）。已自动重试，仍失败。";
                    break;
                default:
                    code = "http_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    message = "请求失败（" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）。";
                    break;
            }

            var result = ChatResult.Fail(code, message);
            // 服务端给的原因通常是一句话，对用户很有用；截断并压平后带上
            if (!string.IsNullOrEmpty(bodyPreview))
            {
                result.ErrorMessage = message + " 服务端说明：" + TextUtil.Truncate(TextUtil.Normalize(bodyPreview), 200);
            }
            return result;
        }

        /// <summary>这些状态码值得重试。</summary>
        public static bool IsRetryable(int status)
        {
            return status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
        }
    }
}
