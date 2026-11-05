using UnityEngine;
using UnityEngine.UI;

// 挂在 UGUI 的 Text 上，把「界面上烘死的文字」变成可切换的。
//
// 场景是 PlaybackSceneBuilder 在编辑器里生成后存盘的，按钮上的「播放 / 暂停」等
// 都是静态字符串。运行时只靠 OnGUI 重画是改不到它们的，所以每个需要翻译的
// Text 都挂一个本组件：Awake 记下中文字号，语言变化时换文字（必要时换字号）。
[RequireComponent(typeof(Text))]
public sealed class LocalizedText : MonoBehaviour
{
    [Tooltip("UILang 里的词条 key，例如 bar.play")]
    public string key;

    [Tooltip("英文时改用这个字号（0 = 与中文一致）。英文单词比中文长，列宽固定时要缩字")]
    public int enFontSize;

    Text _text;
    int _zhFontSize;

    void Awake()
    {
        Bind();
        Apply();
    }

    void OnEnable()
    {
        UILang.Changed += Apply;
        Apply();
    }

    void OnDisable()
    {
        UILang.Changed -= Apply;
    }

    void Bind()
    {
        if (_text != null) return;

        _text = GetComponent<Text>();
        if (_text != null) _zhFontSize = _text.fontSize;
    }

    void Apply()
    {
        Bind();
        if (_text == null || string.IsNullOrEmpty(key)) return;

        _text.text = UILang.T(key);

        if (enFontSize > 0)
            _text.fontSize = UILang.IsEnglish ? enFontSize : _zhFontSize;
    }
}
