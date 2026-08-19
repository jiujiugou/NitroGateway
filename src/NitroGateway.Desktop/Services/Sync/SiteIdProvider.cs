using Microsoft.Extensions.Configuration;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services.Sync;

/// <summary>站点标识提供者（ADR-036）：生效 siteId 的解析、保存与重新生成。</summary>
public interface ISiteIdProvider
{
    /// <summary>当前生效的站点标识</summary>
    string Current { get; }

    /// <summary>保存站点标识（先校验）；失败返回带错误信息的 OperationResult</summary>
    OperationResult Save(string siteId);

    /// <summary>自动生成新站点标识并持久化，返回新值</summary>
    string Regenerate();
}

/// <summary>
/// 生效 siteId 解析顺序：配置/环境变量（Site:Id）＞ 本地存储（site.json）＞ 自动生成并持久化。
/// 保留值 "default"（旧版 appsettings 缺省）视为未初始化，一律进入自动生成路径；
/// 启动时 GatewayHost 把解析结果写回配置，采集/转发/告警/同步统一取用（ADR-036）。
/// </summary>
public sealed class SiteIdProvider : ISiteIdProvider
{
    private readonly ISiteSettingsStore _store;
    private string _current;

    public SiteIdProvider(IConfiguration configuration, ISiteSettingsStore store)
    {
        _store = store;
        _current = Resolve(configuration, store);
    }

    public string Current => _current;

    /// <summary>
    /// 解析生效站点标识。配置值合法（非空、非 default、格式合规）优先；
    /// 其次本地存储；都不可用时自动生成并持久化，保证首次启动即有唯一 siteId。
    /// </summary>
    public static string Resolve(IConfiguration configuration, ISiteSettingsStore store)
    {
        var configured = configuration["Site:Id"]?.Trim();
        if (SiteOptions.IsValidSiteId(configured))
            return configured!;

        var stored = store.Load().SiteId.Trim();
        if (SiteOptions.IsValidSiteId(stored))
            return stored;

        var generated = SiteOptions.GenerateSiteId();
        store.Save(new SiteSettings { SiteId = generated });
        return generated;
    }

    public OperationResult Save(string siteId)
    {
        var value = siteId?.Trim() ?? "";
        if (!SiteOptions.IsValidSiteId(value))
            return OperationResult.Failure(OperationalError.Validation(
                "站点标识不合法：小写字母/数字开头，可含连字符，≤32 位，且不能为 default"));

        _store.Save(new SiteSettings { SiteId = value });
        _current = value;
        return OperationResult.Success();
    }

    public string Regenerate()
    {
        var value = SiteOptions.GenerateSiteId();
        _store.Save(new SiteSettings { SiteId = value });
        _current = value;
        return value;
    }
}