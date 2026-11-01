# Provider 与多模型配置

版本：1.2.0.0；更新：2026-10-14。

“我的 → Provider 与模型”先列出服务，再进入每个服务的模型列表。每个 Provider 独立保存协议、服务地址、模型列表、默认模型、辅助模型和密钥；同一 Provider 下的模型共用该服务的凭据。可以创建多个使用同一协议的 Provider，同名模型通过 Provider ID 区分。

## 使用

1. 添加 Provider，选择 OpenAI 兼容、Anthropic 原生或 Gemini 原生协议，填写服务地址和密钥。
2. 默认开启“保存时自动获取模型”。OpenAI 兼容服务自动推导 /v1/models，也可在高级设置中指定模型列表地址；原生协议采用相应列表接口。
3. 打开 Provider 查看、搜索、刷新模型；点击一个模型设置这个服务的默认模型。“全局默认”单独指定默认服务。
4. 在会话顶部点击“Provider · 模型 ▾”，按服务选择模型。只更改当前会话的 Provider 与模型，保留历史；也可以选“跟随档案”恢复继承。
5. 模型“详情”显示能力与来源，允许分别覆盖上下文窗口、输出上限、图片输入、推理和工具调用。选择“自动”或“恢复自动能力”使用服务端/内置数据。
6. 不提供列表接口的服务可以手动添加模型。辅助模型从同一 Provider 的列表选择，避免跨服务误用模型和密钥。

刷新失败或取消时保留原有列表；手动模型与手动能力覆盖也会在刷新后保留。保存服务时自动获取失败，会保存服务配置并在服务详情显示原因。无密钥本地服务可关闭“服务需要 API 密钥”。

## 自动能力解析

| 信息 | 支持的服务端字段示例 |
| --- | --- |
| 模型 ID / 名称 | id、name、display_name |
| 上下文窗口 | context_length、context_window、max_context_length、max_input_tokens |
| 输出上限 | max_output_tokens、max_completion_tokens、top_provider.max_completion_tokens |
| 图片输入 | input_modalities、architecture.input_modalities、architecture.modality、capabilities.vision |
| 推理与工具 | supported_parameters、capabilities.reasoning / tools、supports_reasoning / supports_tools |
| 输入输出模态 | input_modalities、output_modalities（包括 architecture 内的字段） |

能力优先级：手动覆盖 → 服务端明确返回 → 内置参考 → 未知。显式“不支持”覆盖内置推断。仅支持图像输出不会被误判为支持图片输入。UI 标明来源，内置参考会注明“推断”。

标准 OpenAI 模型列表返回模型身份信息，不能保证提供所有能力；更丰富的兼容服务（例如 OpenRouter）会提供上下文、模态和参数。缺失信息不能当成服务端已经确认的能力。参见 [OpenAI 模型列表](https://developers.openai.com/api/reference/resources/models/methods/list)、[OpenRouter 模型目录](https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties)。

发送时使用选中模型所属 Provider 的地址、协议和密钥；已知模型窗口用于上下文预算，输出上限约束请求参数，明确不支持工具的模型不发送工具定义，明确不支持图片的模型在写入用户消息前给出提示。自定义请求体中的旧 model 字段不能覆盖当前选择。

## 数据兼容与验证

沿用 _shared/api-profiles.json 及旧 Provider ID / 凭据键，不批量改动已有会话。新增 modelsUrl、requiresApiKey、manualModels、modelOverrides 等字段；旧手动缓存在刷新时迁入覆盖层。修复 JsonBridge 对 Nullable<T> 的保存，能力、生成参数和刷新时间不会再写成空对象；旧空对象读成未知值。

核心回归 348/348，包括 12 项新增 Provider 用例：分页去重、凭据隔离、刷新保护、配置往返、已有会话跨服务连续切换、能力限制及旧数据恢复。测试使用模拟传输与既有本地 HTTP 回归，不使用用户密钥调用真实模型服务。

x86 Debug 已编译、注册并启动。启动日志确认 MainPage、主题、根列表及窗口激活完成。ARM Release 构建通过，包含 .NET Native 与 PyUwp；包内版本/架构及 SHA256 记录于 build-artifacts.json。

Computer Use 自动权限检查仍返回“Computer Use was not approved to use 言枢·消息”，所以 Provider 新页面尚未通过截图和实际点击验收；尚未进行真机验证。
