using UnityEngine;
using UnityEngine.UI;

// 控制条状态反馈。
//
// 改之前：四个速度按钮长得一模一样，看不出当前是 0.5x 还是 4x；
// Play / Pause 也没有任何"我现在在播 / 已暂停"的反馈，演示时老师问"现在几倍速"答不上来。
// 这里每帧比对 PlaybackSpeed 和 IsPlaying，直接改按钮底色的基础色。
//
// 说明：Button 的 ColorTint 是乘算，normalColor 是白色，所以改 targetGraphic.color
// 不会被 tint 吃掉，hover / pressed 仍然照常工作。
public sealed class ControlBarState : MonoBehaviour
{
    public PlaybackController player;

    [Tooltip("速度按钮，顺序必须与 speeds 一一对应")]
    public Button[] speedButtons;
    public float[] speeds = { 0.5f, 1f, 2f, 4f };

    public Button playButton;
    public Button pauseButton;

    [Tooltip("播放中 Play 按钮的底色，Pause 用同样颜色显示「已暂停」")]
    public Color activeColor = new Color(0.878f, 0.541f, 0.180f, 1f);
    public Color idleColor = new Color(0.200f, 0.267f, 0.361f, 1f);

    int _lastSpeedIndex = int.MinValue;
    int _lastPlaying = -1;
    Color _lastActive;
    Color _lastIdle;

    void Update()
    {
        if (player == null) return;

        int index = NearestSpeedIndex(player.PlaybackSpeed);
        int playing = player.IsPlaying ? 1 : 0;

        // 只在状态真的变了才写 color，避免每帧触发 UI 重建。
        bool dirty = index != _lastSpeedIndex || playing != _lastPlaying
                     || !Approximately(activeColor, _lastActive) || !Approximately(idleColor, _lastIdle);
        if (!dirty) return;

        _lastSpeedIndex = index;
        _lastPlaying = playing;
        _lastActive = activeColor;
        _lastIdle = idleColor;

        if (speedButtons != null)
        {
            for (int i = 0; i < speedButtons.Length; i++)
            {
                var img = speedButtons[i] != null ? speedButtons[i].targetGraphic as Image : null;
                if (img != null) img.color = (i == index) ? activeColor : idleColor;
            }
        }

        SetTint(playButton, playing == 1 ? activeColor : idleColor);
        SetTint(pauseButton, playing == 1 ? idleColor : activeColor);
    }

    static void SetTint(Button button, Color color)
    {
        if (button == null) return;
        var img = button.targetGraphic as Image;
        if (img != null) img.color = color;
    }

    int NearestSpeedIndex(float speed)
    {
        if (speeds == null || speeds.Length == 0) return -1;
        int best = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < speeds.Length; i++)
        {
            float diff = Mathf.Abs(speeds[i] - speed);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return bestDiff < 0.001f ? best : -1;
    }

    static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f
               && Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;
    }
}
