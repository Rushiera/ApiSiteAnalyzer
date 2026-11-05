using System;

namespace ApiSiteAnalyzer.Contract
{
    /// <summary>
    /// 业务核心的宿主契约——exe 与 Core 之间的**唯一**跨程序集面。
    /// <para>
    /// 存在理由：Core 要能进程内热重载（可回收 ALC），宿主就不能静态引用任何 Core 类型。
    /// 本接口只用框架类型（string / bool / int），不传递 Core 自定义类型——避免跨 ALC 类型身份问题。
    /// </para>
    /// <para>
    /// 契约变更（增删成员）等于改热重载边界：需要一次完整部署重启，不能靠热重载生效。
    /// 本契约六成员：Load / Start / Stop / SnapshotJson / Version / RunCommand。
    /// </para>
    /// </summary>
    public interface IShellCore : IDisposable
    {
        /// <summary>核心版本（Core 程序集版本，日志与状态面用）。</summary>
        string Version { get; }

        /// <summary>
        /// 装载配置与站点（部署期声明，只读一次——<c>config.json</c> 不因换核变成热读面）。
        /// </summary>
        /// <param name="configPath">配置文件路径（空 = 当前目录 → 程序目录）。</param>
        /// <param name="baseDir">程序目录（宿主传入，用于配置回落）。</param>
        void Load(string configPath, string baseDir);

        /// <summary>启动服务（Kestrel + 后台采集）。失败抛具名异常。</summary>
        /// <param name="openBrowser">true = 启动后打开浏览器（仅首次启动；换核不重开）。</param>
        void Start(bool openBrowser);

        /// <summary>停止服务（幂等）。</summary>
        void Stop();

        /// <summary>核心状态快照（版本 / pid / 端口 / 站点数 / 运行时长）。</summary>
        /// <returns>JSON 对象字符串。</returns>
        string SnapshotJson();

        /// <summary>跑一条 CLI 子命令（check / fetch / stats / sites / probe）。</summary>
        /// <param name="verb">子命令名。</param>
        /// <param name="optionsJson">选项 JSON（<c>{"full":true,"site":"xyjun"}</c>，可为空串）。</param>
        /// <returns>退出码。</returns>
        int RunCommand(string verb, string optionsJson);
    }
}
