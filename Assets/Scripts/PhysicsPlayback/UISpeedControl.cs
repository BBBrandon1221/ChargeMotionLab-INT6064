using UnityEngine;

// 速度按钮：给 4 个按钮提供可绑定（无参数）的方法
public sealed class UISpeedControl : MonoBehaviour
{
    public PlaybackController player;

    public void SetHalf() { if (player) player.SetPlaybackSpeed(0.5f); }
    public void SetNormal() { if (player) player.SetPlaybackSpeed(1f); }
    public void SetFast() { if (player) player.SetPlaybackSpeed(2f); }
    public void SetVeryFast() { if (player) player.SetPlaybackSpeed(4f); }
}
