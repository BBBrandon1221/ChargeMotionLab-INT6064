using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public sealed class PlaybackController : MonoBehaviour
{
    // 此控制器只回放 Python 的结果，不使用 Rigidbody 重新计算碰撞。
    public SimulationLoader Loader { get; } = new SimulationLoader();
    public float PlaybackSpeed = 1f;
    public bool IsPlaying { get; private set; }
    public int CurrentFrame { get; private set; }
    public float CurrentTime => Loader.Output == null ? 0f : Loader.Output.frames[CurrentFrame].time;
    public Dictionary<string, Transform> Targets = new Dictionary<string, Transform>();

    // 加载成功广播，供场景绑定、HUD 等订阅后刷新。
    public event Action<SimulationOutput> Loaded;

    // 最近一次加载失败的原因，HUD 可以直接读出来显示。
    public string LastError { get; private set; }

    // 用 double 累加，长时间播放不会攒出可见的浮点漂移。
    double _accum;

    // ---------- 加载 ----------

    // 同步版：Editor / Windows 打包。失败不抛异常，只记录到 LastError，调用方不用包 try。
    public void Load(string path)
    {
        try { Loader.Load(SimulationFileResolver.ToLocalPath(path)); }
        catch (Exception error) { Fail($"{path}: {error.Message}"); return; }
        OnLoaded();
    }

    // 异步版：全平台通用（Editor / Windows / WebGL / Android）。推荐统一用这个。
    public IEnumerator LoadAsync(string path)
    {
        string url;
        try { url = SimulationFileResolver.ToUrl(path); }
        catch (Exception error) { Fail(error.Message); yield break; }

        // 本地文件直接同步读，省一次协程往返。
        if (!SimulationFileResolver.RequiresWebRequest(path))
        {
            try { Loader.Load(SimulationFileResolver.ToLocalPath(path)); }
            catch (Exception error) { Fail($"{url}: {error.Message}"); yield break; }
            OnLoaded();
            yield break;
        }

        using (var request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            // request.result 需要 Unity 2020.2+；本项目是 2022.3，可直接用。
            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail($"{url}: {request.error}");
                yield break;
            }

            try { Loader.LoadFromText(request.downloadHandler.text); }
            catch (Exception error) { Fail($"{url}: {error.Message}"); yield break; }
        }

        OnLoaded();
    }

    void OnLoaded()
    {
        LastError = null;
        CurrentFrame = 0;
        _accum = 0;
        IsPlaying = false;
        ApplyFrame();
        Loaded?.Invoke(Loader.Output);
    }

    // 改参数后由 C# 求解器在内存里生成新的 Output，走这条路上屏。
    // 复用 OnLoaded()，所以 HUD、参数面板、速度标签都会收到同一次 Loaded 事件，行为与读文件完全一致。
    public bool LoadFromOutput(SimulationOutput output)
    {
        try { Loader.SetOutput(output); }
        catch (Exception error) { Fail($"重算结果无效：{error.Message}"); return false; }

        OnLoaded();
        return true;
    }

    void Fail(string message)
    {
        LastError = message;
        Debug.LogError($"[PlaybackController] {message}", this);
    }

    // ---------- 播放控制 ----------

    public void Play()
    {
        if (Loader.Output == null || Loader.Output.frames.Length < 2) return;
        // 停在末帧时再按播放就从头开始，否则按钮看起来像坏了。
        if (CurrentFrame >= Loader.Output.frames.Length - 1) { CurrentFrame = 0; _accum = 0; ApplyFrame(); }
        IsPlaying = true;
    }

    public void Pause() { IsPlaying = false; }

    public void ResetPlayback() { CurrentFrame = 0; _accum = 0; IsPlaying = false; ApplyFrame(); }

    public void StepForward()
    {
        if (Loader.Output == null) return;
        CurrentFrame = Mathf.Min(CurrentFrame + 1, Loader.Output.frames.Length - 1);
        _accum = 0;
        ApplyFrame();
    }

    public void Seek(float time)
    {
        if (Loader.Output == null) return;
        var frames = Loader.Output.frames;
        CurrentFrame = 0;
        while (CurrentFrame + 1 < frames.Length && frames[CurrentFrame + 1].time <= time) CurrentFrame++;
        _accum = 0;
        ApplyFrame();
    }

    public void SeekFrame(int index)
    {
        if (Loader.Output == null) return;
        CurrentFrame = Mathf.Clamp(index, 0, Loader.Output.frames.Length - 1);
        _accum = 0;
        ApplyFrame();
    }

    public void SetPlaybackSpeed(float speed) { PlaybackSpeed = Mathf.Clamp(speed, 0f, 16f); }

    // 按帧的时间戳推进：1x = 真实时间（2 秒的算例播 2 秒），4x = 4 倍速。
    // 一个 Unity 帧可能跨过多个物理帧（dt=0.01 vs 60fps），所以用 while 把累积时间消费完。
    void Update()
    {
        if (!IsPlaying || Loader.Output == null) return;
        var frames = Loader.Output.frames;
        if (CurrentFrame >= frames.Length - 1) { IsPlaying = false; return; }

        _accum += Time.deltaTime * PlaybackSpeed;
        while (CurrentFrame + 1 < frames.Length)
        {
            float gap = frames[CurrentFrame + 1].time - frames[CurrentFrame].time;
            if (_accum < gap) break;
            _accum -= gap;
            CurrentFrame++;
        }
        ApplyFrame();
    }

    void ApplyFrame()
    {
        if (Loader.Output == null || Loader.Output.frames.Length == 0) return;
        var frame = Loader.Output.frames[Mathf.Clamp(CurrentFrame, 0, Loader.Output.frames.Length - 1)];
        if (frame.objects == null) return;

        foreach (var item in frame.objects)
        {
            if (item == null || item.position == null || item.position.Length < 2) continue;
            if (Targets.TryGetValue(item.id, out var target) && target != null)
                target.position = new Vector3(item.position[0], item.position[1], target.position.z);
        }
    }
}
