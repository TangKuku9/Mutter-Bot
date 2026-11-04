using Mutter.Core.Models;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;

namespace Mutter.Core.Interfaces;

/// <summary>执行一次已选定通道的模型调用；搜索/看图等业务编排由上层负责。</summary>
public interface IAiModelExecutor
{
    Task<AiChatResponse> ExecuteAsync(
        AiChatRequest request, AiConfig config, AiSearchStrategy strategy, CancellationToken ct);
}
