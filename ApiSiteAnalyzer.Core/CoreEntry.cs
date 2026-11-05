using System;
using ApiSiteAnalyzer.Contract;

namespace ApiSiteAnalyzer
{
    /// <summary>
    /// 核心入口——宿主经反射调用的唯一静态入口（跨 ALC 边界）。
    /// <para>
    /// 签名固定：<c>public static IShellCore Create()</c>，无参数（框架类型约束）。
    /// 改签名等于改热重载边界，需要一次完整部署重启。
    /// </para>
    /// </summary>
    public static class CoreEntry
    {
        /// <summary>创建核心实例（未 Load / 未 Start）。</summary>
        /// <returns>核心实例。</returns>
        public static IShellCore Create()
        {
            return new ShellCore();
        }
    }
}
