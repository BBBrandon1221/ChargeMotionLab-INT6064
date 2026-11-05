using UnityEngine;

// 控制条最右端的语言切换按钮。
//
// 为什么要包一层：UnityEvent 的持久监听把调用记在「场景里的某个对象实例」上，
// 静态方法（UILang.Toggle）没有实例可记，AddPersistentListener 存进场景后会丢。
// 所以挂一个空组件，用它的实例方法去转调。
public sealed class UILangButton : MonoBehaviour
{
    public void Toggle()
    {
        UILang.Toggle();
    }
}
