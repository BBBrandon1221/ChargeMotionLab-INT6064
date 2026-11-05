using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// 一条对话消息。role 用 "system" / "user" / "assistant"，与接口一致。
[Serializable]
public class ChatMessage
{
    public string role;
    public string content;

    public ChatMessage() { }

    public ChatMessage(string role, string content)
    {
        this.role = role;
        this.content = content;
    }
}

[Serializable]
public class ThinkingSwitch
{
    public string type;
}

[Serializable]
public class ChatRequest
{
    public string model;
    public ChatMessage[] messages;
    public float temperature;
    public int max_tokens;
    public bool stream;
    public ThinkingSwitch thinking;
}

[Serializable]
public class ChatChoice
{
    public ChatMessage message;
    public string finish_reason;
}

[Serializable]
public class ChatError
{
    public string message;
    public string type;
    public string code;
}

[Serializable]
public class ChatResponse
{
    public ChatChoice[] choices;
    public ChatError error;
}

// DeepSeek 的 /chat/completions 封装：拼请求体 → 发 → 拆回答 / 翻译错误。
// 只做一件事：把"一段对话"变成"一段文字"，不碰 UI，不碰物理数据。
public sealed class DeepSeekClient
{
    // 非流式请求，2~5 秒是常态；60 秒足够覆盖长回答 + 弱网，再长就该判失败了。
    public const int TimeoutSeconds = 60;

    readonly AIConfigData _config;

    public DeepSeekClient(AIConfigData config)
    {
        _config = config;
    }

    public bool Ready
    {
        get { return _config != null && AIConfig.HasUsableKey(_config); }
    }

    public string Model
    {
        get { return _config != null && !string.IsNullOrEmpty(_config.model) ? _config.model : AIConfig.DefaultModel; }
    }

    // 失败时回调给出的是可以直接显示给学生的中文句子，不是异常对象。
    public IEnumerator Complete(List<ChatMessage> messages, Action<string> onSuccess, Action<string> onFailure)
    {
        if (!Ready)
        {
            if (onFailure != null) onFailure("还没有可用的 API key");
            yield break;
        }

        if (messages == null || messages.Count == 0)
        {
            if (onFailure != null) onFailure("没有要发送的内容");
            yield break;
        }

        var body = BuildBody(messages);
        var url = string.IsNullOrEmpty(_config.base_url) ? AIConfig.DefaultBaseUrl : _config.base_url;

        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + _config.api_key);
            request.timeout = TimeoutSeconds;

            // 注意：不要在这里 try/catch 包 yield，Unity 的迭代器不允许在 catch 块周围 yield。
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                if (onFailure != null) onFailure(DescribeFailure(request));
                yield break;
            }

            var text = request.downloadHandler != null ? request.downloadHandler.text : null;
            string content;
            string error;
            if (!TryReadContent(text, out content, out error))
            {
                if (onFailure != null) onFailure(error);
                yield break;
            }

            if (onSuccess != null) onSuccess(content);
        }
    }

    string BuildBody(List<ChatMessage> messages)
    {
        var request = new ChatRequest
        {
            model = Model,
            messages = messages.ToArray(),
            temperature = _config.temperature,
            max_tokens = _config.max_tokens,
            stream = false,
            // 关键：不写这一段，v4-flash 会把 token 全用在 reasoning_content 上，content 返回空。
            thinking = new ThinkingSwitch { type = _config.ThinkingEnabled ? "enabled" : "disabled" },
        };
        return JsonUtility.ToJson(request);
    }

    static bool TryReadContent(string text, out string content, out string error)
    {
        content = null;
        error = null;

        if (string.IsNullOrEmpty(text))
        {
            error = "服务端返回了空响应";
            return false;
        }

        ChatResponse response = null;
        try
        {
            response = JsonUtility.FromJson<ChatResponse>(text);
        }
        catch (Exception)
        {
            error = "返回内容不是合法 JSON：" + Shorten(text);
            return false;
        }

        if (response == null)
        {
            error = "返回内容无法解析：" + Shorten(text);
            return false;
        }

        if (response.error != null && !string.IsNullOrEmpty(response.error.message))
        {
            error = "接口返回错误：" + response.error.message;
            return false;
        }

        if (response.choices == null || response.choices.Length == 0 ||
            response.choices[0] == null || response.choices[0].message == null)
        {
            error = "返回结构里没有 choices：" + Shorten(text);
            return false;
        }

        var message = response.choices[0].message;
        var body = message.content == null ? string.Empty : message.content.Trim();
        if (body.Length == 0)
        {
            // finish_reason=length 说明 max_tokens 被耗光了；开了思考模式时最先被吃掉的正是正文。
            error = response.choices[0].finish_reason == "length"
                ? "回答被长度上限截断：把 ai_config.json 里的 max_tokens 调大，并确认 thinking 是 disabled"
                : "模型返回了空内容，可以重试一次";
            return false;
        }

        content = body;
        return true;
    }

    static string DescribeFailure(UnityWebRequest request)
    {
        if (request.result == UnityWebRequest.Result.ConnectionError)
            return "请求超时或网络不通（" + request.error + "），检查网络后重试";

        if (request.result == UnityWebRequest.Result.DataProcessingError)
            return "响应处理失败（" + request.error + "）";

        var code = request.responseCode;
        switch (code)
        {
            case 401:
                return "API key 无效或已撤销（401）：去 platform.deepseek.com 重新生成一个";
            case 402:
                return "账户余额不足（402）：去 platform.deepseek.com 充值后再试";
            case 429:
                return "请求太频繁（429）：等几秒再发";
            case 400:
                return "请求被拒绝（400）：" + ExtractApiMessage(request);
            case 500:
            case 502:
            case 503:
            case 504:
                return "服务端错误（" + code + "）：稍后重试";
            default:
                return "请求失败（" + code + "）：" + ExtractApiMessage(request);
        }
    }

    // 400 这类错误，真正的原因在响应体的 error.message 里，比状态码有用得多。
    static string ExtractApiMessage(UnityWebRequest request)
    {
        try
        {
            if (request.downloadHandler == null) return request.error;
            var text = request.downloadHandler.text;
            if (string.IsNullOrEmpty(text)) return request.error;

            var response = JsonUtility.FromJson<ChatResponse>(text);
            if (response != null && response.error != null && !string.IsNullOrEmpty(response.error.message))
                return response.error.message;
            return Shorten(text);
        }
        catch (Exception)
        {
            return request.error;
        }
    }

    static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
    }
}
