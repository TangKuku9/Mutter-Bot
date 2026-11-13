using Microsoft.EntityFrameworkCore;
using Mutter.Infrastructure.Repositories;
using Mutter.Web.Models;

namespace Mutter.Web.Controllers;

public partial class ConfigController
{
    private async Task<string?> ResolveConnectionAsync(AiConfigInputDto dto)
    {
        dto.ProviderConnectionId = (dto.ProviderConnectionId ?? "").Trim();
        dto.ProviderConnectionVersion ??= "";
        if (dto.ProviderConnectionId.Length == 0) return null; // 兼容旧 API 与未绑定的旧预设。
        var connection = await _dbContext.AiProviderConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.ProviderConnectionId);
        if (connection == null) return "服务商连接已不存在，请重新选择";
        var model = AiConnectionStore.Models(connection).FirstOrDefault(m => m.Id == (dto.ModelName ?? "").Trim());
        if (model == null) return "该连接下没有选中的模型，请先在管理服务商中添加模型";
        if (model.MaxOutputTokens is int output && dto.MaxTokens > output)
            return $"该模型的回复输出上限为 {output}，请调整后保存";
        if (model.ContextWindowTokens is int context && dto.ContextWindowTokens > context)
            return $"该模型的上下文容量为 {context}，请调整后保存";
        var version = connection.Revision;
        if (dto.ProviderConnectionVersion.Length > 0 && dto.ProviderConnectionVersion != version)
        {
            dto.WebSearchEndpoint = "";
            dto.WebSearchShape = "";
        }
        dto.ProviderConnectionVersion = version;
        if (!string.IsNullOrEmpty(dto.WebSearchEndpoint) && dto.WebSearchEndpoint != connection.Protocol)
            return "搜索通道与连接协议不同，请选择相同协议的策略，或创建对应协议的连接";
        dto.ApiBaseUrl = connection.ApiBaseUrl;
        dto.ApiKey = connection.ApiKey;
        return null;
    }
}
