// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace SeeMe
{
    /// <summary>
    /// 单实例 + 文件接力。
    ///
    /// <para><b>为什么需要</b>：SeeMe 可以被设为各格式的默认查看器（见 <see cref="FileAssoc"/>）。
    /// 在此之前，双击 10 个文档 = <b>10 个独立进程</b>，每个进程都自带一套 WebView2 宿主 ——
    /// 实测单窗口常驻约 280 MB。单实例让第 2 个及以后的进程把文件路径<b>转发</b>给首个实例后立即退出。</para>
    ///
    /// <para><b>接力而非"复用窗口内容"</b>：首个实例收到路径后在本进程内
    /// <see cref="App.CreateWindow"/> 开新窗口（与 Ctrl+N 同一路径）。
    /// 这样既拿到"一个进程"的内存收益，又保持了既有的多窗口阅读模型 ——
    /// 不会把用户正在读的文档顶掉。</para>
    ///
    /// <para><b>失败即降级</b>：互斥体/管道任一环节异常都退化为"允许多实例"，
    /// 绝不因为单实例机制本身出问题而让程序打不开文件。
    /// 同理，转发失败（首实例恰好在退出）时次级实例继续正常启动。</para>
    ///
    /// <para><b>作用域</b>：互斥体用 <c>Local\</c> 前缀 + 会话 ID，管道名同样带会话 ID ——
    /// 终端服务器/多用户登录场景下，不同会话各自的 SeeMe 互不干扰。</para>
    /// </summary>
    public static class SingleInstance
    {
        private static readonly int SessionId = ResolveSessionId();

        private static string MutexName => @"Local\SeeMe.SingleInstance.v1." + SessionId;

        private static string PipeName => "SeeMe.FileRelay.v1." + SessionId;

        /// <summary>转发连接超时（毫秒）。对方忙/在退出时不必让用户干等。</summary>
        private const int RelayTimeoutMs = 2000;

        private static Mutex? _mutex;
        private static CancellationTokenSource? _cts;

        private static int ResolveSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return 0; }
        }

        /// <summary>
        /// 尝试成为首个实例。
        /// </summary>
        /// <returns>
        /// <c>true</c> = 本进程是首实例（调用方应接着 <see cref="StartServer"/> 并正常启动）；
        /// <c>false</c> = 已有实例在运行（调用方应 <see cref="RelayToExisting"/> 后退出，
        /// 或在其失败时按普通启动继续）。
        /// </returns>
        public static bool TryAcquire()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                return createdNew;
            }
            catch (Exception ex)
            {
                // 互斥体创建失败（权限/内核对象异常）→ 不阻断启动，退化为多实例。
                SeeMeLog.Warn("SingleInstance.TryAcquire", ex.Message);
                return true;
            }
        }

        /// <summary>
        /// 把文件路径转发给已在运行的首实例。成功返回 <c>true</c>，调用方可直接退出。
        /// </summary>
        /// <param name="filePath">要打开的文件；<c>null</c> 表示"仅把已有窗口唤到前台"。</param>
        public static bool RelayToExisting(string? filePath)
        {
            try
            {
                // 🔴 必须显式指定 TokenImpersonationLevel.Identification。
                // NamedPipeClientStream 的默认构造函数用的是 TokenImpersonationLevel.**None**，
                // 于是服务端的 GetImpersonationUserName() 拿不到可用身份 —— 后果是
                // 合法的接力会被服务端当成"身份不可确认"拒掉（实测：客户端报 Pipe is broken，
                // 服务端报"拒绝非同用户/身份不可确认的连接"，且异常信息为空，极难定位）。
                // Identification 只允许对方**识别**本进程身份，不允许冒用，对我们是刚好够用的档位。
                using var client = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out,
                    PipeOptions.None, TokenImpersonationLevel.Identification);
                client.Connect(RelayTimeoutMs);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                // 一行一条路径；空行 = 只激活窗口。路径本身可能含空格/中文，故不额外转义。
                writer.WriteLine(filePath ?? "");
                return true;
            }
            catch (Exception ex)
            {
                // 首实例可能恰好在退出（管道已关闭）——调用方据此回退为正常启动。
                SeeMeLog.Warn("SingleInstance.Relay", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 首实例：启动管道服务端，循环接收后续实例转发的路径。
        /// </summary>
        /// <param name="onFileReceived">
        /// 收到路径时的回调。<b>在后台线程调用</b>，实现方需自行切回 UI 线程。
        /// </param>
        public static void StartServer(Action<string?> onFileReceived)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                        // 用可取消的异步等待，而不是阻塞的 WaitForConnection()：
                        // 后者在退出时无法被 token 打断，会留下一个卡在等待上的线程池线程。
                        await server.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(server, Encoding.UTF8);
                        // 顺序是**被迫**的，不要"顺手"把身份校验提到前面：
                        // GetImpersonationUserName 在"尚未从管道读过数据"时会直接抛
                        //   IOException: 在使用命名管道读取数据之前，无法经由该管道模拟。
                        // 因为要拿客户端令牌，内核必须先把请求缓冲里的数据传过来。
                        // 先读是安全的 —— ReadLineCappedAsync 有硬上限（MaxRelayPathChars），
                        // 未通过身份校验前不会对内容做任何动作。
                        var line = await ReadLineCappedAsync(reader, token);

                        // ── 客户端身份校验（fail-closed）──
                        // 管道名已含会话 ID（挡住跨会话），这一步再挡住**同会话内的其他用户**。
                        // 身份不可确认时拒绝，而不是放行 —— 与 GuardDocumentHost 的 fail-closed 口径一致。
                        if (!IsSameUser(server))
                        {
                            SeeMeLog.Warn("SingleInstance.client", "拒绝非同用户/身份不可确认的连接");
                            continue;
                        }

                        if (line == null)
                        {
                            // 超长载荷 = 恶意/异常客户端，丢弃该连接（不 throw，循环继续接下一个）
                            SeeMeLog.Warn("SingleInstance.payload", "转发载荷超长或无换行，已丢弃");
                            continue;
                        }
                        var relayPath = line.Length == 0 ? null : line;
                        if (relayPath != null && !IsAcceptableRelayPath(relayPath))
                        {
                            SeeMeLog.Warn("SingleInstance.payload", "转发路径不合法，已丢弃");
                            continue;
                        }
                        onFileReceived(relayPath);
                    }
                    catch (OperationCanceledException)
                    {
                        return;   // 正常退出路径
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested) return;
                        SeeMeLog.Error("SingleInstance.Server", ex);
                        // 避免异常（如管道名冲突）造成忙循环
                        try { await Task.Delay(200, token); } catch { return; }
                    }
                }
            }, token);
        }

        /// <summary>停止管道服务并释放互斥体（进程退出时调用）。</summary>
        public static void Shutdown()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }

        // ──────────────── 载荷与身份校验（2026-10-11 安全审查补） ────────────────

        /// <summary>
        /// 单条转发载荷的字符上限。Windows 长路径上限 32767，取 8192 已远超真实需求
        /// （真实文件路径罕有超过 260）。
        /// </summary>
        public const int MaxRelayPathChars = 8192;

        /// <summary>
        /// 读取一行，但**带上限**。
        ///
        /// <para><b>为什么不能用 <c>StreamReader.ReadLineAsync</c></b>：它没有长度上限，
        /// 一个连上管道却不发换行的客户端可以让服务端缓冲区无限增长 —— 这是**现成的内存 DoS**，
        /// 不需要任何特权（同会话任何进程都能连）。此处按块读，超限立即返回 null 并丢弃该连接。</para>
        /// </summary>
        /// <returns>读到的行；超长返回 <c>null</c>（调用方应丢弃该连接）。</returns>
        private static async Task<string?> ReadLineCappedAsync(StreamReader reader, CancellationToken ct)
        {
            var sb = new StringBuilder(256);
            var buf = new char[512];
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory(), ct);
                if (n == 0) break;                       // 对端关闭
                for (var i = 0; i < n; i++)
                {
                    var ch = buf[i];
                    if (ch == '\n') return sb.ToString().Trim();
                    if (ch == '\r') continue;
                    if (sb.Length >= MaxRelayPathChars) return null;   // 超限 → 非法
                    sb.Append(ch);
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 连接方是否为当前登录用户。取不到身份时返回 <c>false</c>（fail-closed）。
        ///
        /// <para><b>为什么比较 SID 而不是用户名</b>：<see cref="NamedPipeServerStream.GetImpersonationUserName"/>
        /// 返回的域形式（<c>MACHINE\user</c> / <c>DOMAIN\user</c> / 无前缀）随环境变化，
        /// 与 <c>WindowsIdentity.GetCurrent().Name</c> 直接字符串比较会**误拒合法连接**
        /// （实测踩过：拒绝日志里连异常都没有，因为名字取到了、只是形式不同）。
        /// SID 是唯一稳定的身份标识。</para>
        ///
        /// <para><b>依赖客户端的档位</b>：<c>RunAsClient</c> 需要客户端以
        /// <see cref="TokenImpersonationLevel.Impersonation"/>（或更高）连接；
        /// 客户端侧见 <see cref="RelayToExisting"/> 的注释 —— 那里踩过默认 <c>None</c> 的坑。</para>
        /// </summary>
        private static bool IsSameUser(NamedPipeServerStream server)
        {
            var clientDesc = "(未取得)";
            var selfDesc = "(未取得)";
            try
            {
                using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
                var mySid = me.User;

                System.Security.Principal.SecurityIdentifier? clientSid = null;
                try
                {
                    // RunAsClient：本线程临时以**客户端**身份运行，从而取到已认证的客户端 SID
                    server.RunAsClient(() =>
                    {
                        using var cid = System.Security.Principal.WindowsIdentity.GetCurrent();
                        clientSid = cid.User;
                    });
                }
                catch (Exception ex)
                {
                    SeeMeLog.Warn("SingleInstance.client", "RunAsClient 失败：" + ex.Message);
                }

                clientDesc = clientSid?.Value ?? FallbackName(server);
                selfDesc = mySid?.Value ?? "(空)";

                if (clientSid != null && mySid != null && clientSid.Equals(mySid)) return true;

                // fail-closed 的检查如果不说明原因，出问题就完全没法排查 —— 两侧身份都记下来
                SeeMeLog.Warn("SingleInstance.client", $"身份不匹配，拒绝：client={clientDesc} self={selfDesc}");
                return false;
            }
            catch (Exception ex)
            {
                SeeMeLog.Warn("SingleInstance.client",
                    $"身份校验异常（client={clientDesc} self={selfDesc}），拒绝：{ex.Message}");
                return false;
            }
        }

        /// <summary>取不到 SID 时退回用户名，仅用于**诊断输出**（不作为判据）。</summary>
        private static string FallbackName(NamedPipeServerStream server)
        {
            try
            {
                var name = server.GetImpersonationUserName();
                return string.IsNullOrEmpty(name) ? "(用户名为空)" : "name=" + name;
            }
            catch (Exception ex) { return "(取名失败:" + ex.GetType().Name + ")"; }
        }

        /// <summary>
        /// 转发路径的**传输层**体检：长度、控制字符、绝对路径。
        /// 存在性与格式准入不在这里 —— 那是应用层的 <see cref="FileAdmission"/> 的职责。
        /// </summary>
        private static bool IsAcceptableRelayPath(string path)
        {
            if (path.Length > MaxRelayPathChars) return false;
            foreach (var ch in path)
            {
                if (char.IsControl(ch)) return false;
            }
            try { return System.IO.Path.IsPathFullyQualified(path); }
            catch { return false; }
        }
    }
}
