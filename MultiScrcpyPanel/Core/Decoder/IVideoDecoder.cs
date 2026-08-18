using System;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core.Decoder;

/// <summary>
/// 解码器抽象（架构文档 §3.1 / §6.5），保留作为未来硬件解码（D3D11VA / DXVA2）的扩展点。
/// <para>
/// <c>onFrame</c> 回调参数是 <c>AVFrame*</c> 的 <see cref="IntPtr"/>（避免 unsafe 泄漏到接口签名）；
/// <b>回调内必须同步消费完毕</b>——回调返回后该帧会被复用或释放。
/// </para>
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>按 codec id 创建解码上下文。</summary>
    void Open(uint codecId);

    /// <summary>喂入一个媒体包；解出的每一帧通过 <paramref name="onFrame"/> 同步回调。</summary>
    void TryDecode(in MediaPacket packet, Action<IntPtr> onFrame);

    /// <summary>释放当前上下文（旋转 / 连续失败时重建前调用）。</summary>
    void Reset();

    /// <summary>是否已成功打开。</summary>
    bool IsOpen { get; }
}
