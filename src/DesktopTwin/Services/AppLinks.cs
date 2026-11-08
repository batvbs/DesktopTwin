namespace DesktopTwin.Services;

/// <summary>
/// 项目的对外地址。
///
/// 单独放一处，是因为这些 URL 一旦散落在界面和模板文件里，改了仓库名就会漏改——
/// 之前 <c>.github/ISSUE_TEMPLATE</c> 里留着模板生成的 <c>OWNER</c> 占位符，
/// 点进去就是 404，正是这个原因。
/// </summary>
public static class AppLinks
{
    /// <summary>GitHub 仓库，格式 owner/repo。</summary>
    public const string Repository = "batvbs/DesktopTwin";

    /// <summary>Issues 列表。</summary>
    public const string Issues = "https://github.com/" + Repository + "/issues";

    /// <summary>排查手册。</summary>
    public const string Troubleshooting =
        "https://github.com/" + Repository + "/blob/main/docs/troubleshooting.md";

    /// <summary>
    /// 新建 Issue 的地址。
    ///
    /// 仓库用的是 issue form（带表单字段的 yml 模板），GitHub 不支持通过 URL 预填正文，
    /// 只能预填标题；正文由调用方复制到剪贴板，用户粘进「环境信息」一栏。
    /// </summary>
    /// <param name="title">预填标题，留空则用模板里的默认标题。</param>
    public static string NewIssue(string? title = null)
    {
        var url = "https://github.com/" + Repository + "/issues/new?template=bug_report.yml";
        return string.IsNullOrWhiteSpace(title)
            ? url
            : url + "&title=" + System.Uri.EscapeDataString(title);
    }
}
